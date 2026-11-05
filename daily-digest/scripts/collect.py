#!/usr/bin/env python3
"""Deterministically extract one day of Claude Code activity into day.json.

Read-only over ~/.claude/projects/**/*.jsonl. No network.

Day D covers local time D 04:00 -> D+1 04:00.
"""

import argparse
import glob
import hashlib
import json
import os
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone

NOISE_PREFIXES = (
    "<local-command-caveat>",
    "<command-name>",
    "<local-command-stdout>",
    "<command-message>",
    "<system-reminder>",
    "Base directory for this skill",
    "[Request interrupted",
    "Caveat:",
)

EXCERPT_MARKERS = (
    "所以", "因此", "结论", "根因", "修复了", "已修", "已验证", "验证通过",
    "未完成", "风险", "建议", "关键", "下一步", "最终", "原因", "注意",
    "所以", "但是", "但是", "完成", "失败",
)

FILE_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit", "Read"}
SENTENCE_SPLIT = re.compile(r"(?<=[。！？!?；;])\s*|\n+")


def parse_ts(value):
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone()
    except ValueError:
        return None


def clean_prompt(text):
    if not text:
        return ""
    text = re.sub(r'<pasted_content[^>]*>', "[粘贴] ", text)
    text = text.strip()
    if not text or text.startswith(NOISE_PREFIXES):
        return ""
    if text.startswith("[") and text.endswith("]"):
        return ""
    return text


def text_blocks(content):
    """Yield plain text from a message content field."""
    if isinstance(content, str):
        yield content
        return
    if not isinstance(content, list):
        return
    for item in content:
        if isinstance(item, dict) and item.get("type") == "text":
            yield item.get("text") or ""


def sentence_candidates(text):
    text = re.sub(r"```.*?```", " ", text, flags=re.S)
    text = re.sub(r"`[^`\n]+`", " ", text)
    for sentence in SENTENCE_SPLIT.split(text):
        sentence = sentence.strip().strip("-*• ")
        if 12 <= len(sentence) <= 220 and any(m in sentence for m in EXCERPT_MARKERS):
            yield sentence


def load_config(path):
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    return {}


def iter_transcripts(root):
    for path in glob.glob(os.path.join(root, "**", "*.jsonl"), recursive=True):
        project = os.path.basename(os.path.dirname(path))
        yield project, path


def collect(projects_root, day, config):
    local_tz = datetime.now().astimezone().tzinfo
    raw = config.get("day_boundary", "04:00").split(":")
    start = datetime(day.year, day.month, day.day, int(raw[0]), int(raw[1]), tzinfo=local_tz)
    end = start + timedelta(days=1)

    excluded = set(config.get("exclude_projects") or [])
    exclude_mode = config.get("exclude_mode", "stats_only")  # full | stats_only | content_anonymous
    max_prompt = int(config.get("prompt_max_chars", 800))
    max_sentences = int((config.get("assistant_excerpt") or {}).get("max_sentences_per_session", 8))
    redact = [re.compile(p) for p in (config.get("redact_patterns") or [])]
    path_skip = [re.compile(p) for p in (config.get("exclude_path_patterns") or [])]

    def blocked(text):
        return any(pattern.search(text) for pattern in path_skip)

    def mask(text):
        for pattern in path_skip:
            text = pattern.sub("已排除项目", text)
        return text

    def scrub(text):
        for pattern in redact:
            text = pattern.sub("[REDACTED]", text)
        return text

    sessions = {}
    skipped_projects = Counter()
    parse_failures = 0
    total_lines = 0

    for project, path in iter_transcripts(projects_root):
        is_excluded = project in excluded
        if is_excluded and exclude_mode == "full":
            skipped_projects[project] += 1
            continue
        if is_excluded:
            skipped_projects[project] += 1
        collect_content = (not is_excluded) or (exclude_mode == "content_anonymous")
        try:
            fh = open(path, encoding="utf-8", errors="replace")
        except OSError:
            parse_failures += 1
            continue
        with fh:
            for line in fh:
                total_lines += 1
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    parse_failures += 1
                    continue
                ts = parse_ts(row.get("timestamp"))
                if ts is None or not (start <= ts < end):
                    continue
                if row.get("isSidechain"):
                    continue
                kind = row.get("type")
                sid = row.get("sessionId")
                if not sid:
                    continue
                session = sessions.setdefault(sid, {
                    "project": project,
                    "redacted": is_excluded,
                    "session_id": sid,
                    "events": [],
                    "prompts": [],
                    "prompt_ids": set(),
                    "tools": Counter(),
                    "files": Counter(),
                    "commands": [],
                    "errors": 0,
                    "tokens": Counter(),
                    "excerpt": [],
                    "excerpt_chars": 0,
                })
                session["events"].append(ts)

                if kind == "user":
                    if row.get("isMeta"):
                        continue
                    content = (row.get("message") or {}).get("content")
                    if isinstance(content, list):
                        for item in content:
                            if not isinstance(item, dict) or item.get("type") != "tool_result":
                                continue
                            if item.get("is_error"):
                                session["errors"] += 1
                    for block in text_blocks(content):
                        if not collect_content:
                            break
                        prompt = clean_prompt(block)
                        if not prompt or blocked(prompt):
                            continue
                        pid = row.get("promptId")
                        if pid and pid in session["prompt_ids"]:
                            continue
                        if pid:
                            session["prompt_ids"].add(pid)
                        session["prompts"].append(scrub(mask(prompt[:max_prompt])))

                elif kind == "assistant":
                    content = (row.get("message") or {}).get("content")
                    usage = (row.get("message") or {}).get("usage") or {}
                    for key in ("input_tokens", "output_tokens"):
                        session["tokens"][key] += usage.get(key) or 0
                    if isinstance(content, list):
                        for item in content:
                            if not isinstance(item, dict):
                                continue
                            if item.get("type") == "tool_use":
                                name = item.get("name") or "?"
                                session["tools"][name] += 1
                                payload = item.get("input") or {}
                                if name in FILE_TOOLS and payload.get("file_path") and collect_content:
                                    path_value = payload["file_path"]
                                    if not blocked(path_value):
                                        session["files"][path_value] += 1
                                    elif is_excluded:
                                        session["files"][mask(path_value)] += 1
                                if name == "Bash" and payload.get("command") and collect_content:
                                    cmd = payload["command"]
                                    if not blocked(cmd):
                                        session["commands"].append(scrub(cmd[:160]))
                                    elif is_excluded:
                                        session["commands"].append(scrub(mask(cmd[:160])))
                            elif item.get("type") == "text" and collect_content and len(session["excerpt"]) < max_sentences:
                                for sentence in sentence_candidates(item.get("text") or ""):
                                    if blocked(sentence) and not is_excluded:
                                        continue
                                    session["excerpt"].append(scrub(mask(sentence)))
                                    session["excerpt_chars"] += len(sentence)
                                    if len(session["excerpt"]) >= max_sentences:
                                        break

    out_sessions = []
    hour_minutes = defaultdict(int)
    for session in sessions.values():
        if not session["prompts"] and not session["tools"] and not session["commands"]:
            continue
        events = sorted(session["events"])
        # active time: gaps under 15 min count, longer gaps drop to 1 min
        # a gap over 25 min also splits the day into separate work blocks
        blocks = []
        block_start = events[0]
        block_minutes = 0.0
        minutes = 0.0
        for prev, cur in zip(events, events[1:]):
            gap = (cur - prev).total_seconds() / 60
            if gap > 25:
                blocks.append({
                    "start": block_start.strftime("%H:%M"),
                    "end": prev.strftime("%H:%M"),
                    "minutes": round(block_minutes),
                })
                block_start = cur
                block_minutes = 0.0
                continue
            minutes += gap if gap <= 15 else 1
            block_minutes += gap if gap <= 15 else 1
        blocks.append({
            "start": block_start.strftime("%H:%M"),
            "end": events[-1].strftime("%H:%M"),
            "minutes": round(block_minutes),
        })
        for event in events:
            hour_minutes[event.hour] += 1
        out_sessions.append({
            "project": session["project"],
            "redacted": session["redacted"],
            "session_id": session["session_id"],
            "started_at": events[0].isoformat(timespec="seconds"),
            "ended_at": events[-1].isoformat(timespec="seconds"),
            "active_minutes": round(minutes),
            "blocks": blocks,
            "prompts": session["prompts"],
            "tools": dict(session["tools"].most_common()),
            "files": dict(session["files"].most_common(15)),
            "commands": session["commands"][:40],
            "errors": session["errors"],
            "tokens": dict(session["tokens"]),
            "excerpt": session["excerpt"],
        })

    out_sessions.sort(key=lambda s: s["started_at"])
    histogram = []
    for hour in range(24):
        bucket_start = start.replace(hour=hour, minute=0)
        if bucket_start < start:
            bucket_start += timedelta(days=1)
        if bucket_start >= end:
            continue
        histogram.append({"hour": bucket_start.strftime("%H:%M"), "events": hour_minutes.get(hour, 0)})

    max_total_chars = int((config.get("assistant_excerpt") or {}).get("max_total_chars", 10000))
    used = sum(sum(len(e) for e in s["excerpt"]) for s in out_sessions)
    if used > max_total_chars:
        budget = max_total_chars
        for session in sorted(out_sessions, key=lambda s: -sum(len(e) for e in s["excerpt"])):
            kept = []
            for sentence in session["excerpt"]:
                if kept and budget - len(sentence) < 0:
                    break
                kept.append(sentence)
                budget -= len(sentence)
            session["excerpt"] = kept

    # per-project time, redacted projects get stable A/B/C labels
    by_project = {}
    for session in out_sessions:
        key = session["project"]
        row = by_project.setdefault(key, {
            "name": key,
            "redacted": session["redacted"],
            "minutes": 0,
            "sessions": 0,
            "first": session["started_at"],
        })
        row["minutes"] += session["active_minutes"]
        row["sessions"] += 1
        row["first"] = min(row["first"], session["started_at"])
    projects = sorted(by_project.values(), key=lambda r: (-r["minutes"], r["first"]))
    letter = ord("A")
    labels = {}
    for row in projects:
        if row["redacted"]:
            row["label"] = f"已排除项目 · {chr(letter)}"
            letter += 1
        else:
            row["label"] = row["name"]
        row["key"] = hashlib.sha1(row["name"].encode("utf-8")).hexdigest()[:6]
        labels[row["name"]] = row["label"]
    breakdown = [
        {k: row[k] for k in ("label", "redacted", "minutes", "sessions", "key")}
        for row in projects
    ]
    for session in out_sessions:
        session["project_label"] = labels.get(session["project"], session["project"])

    totals = {
        "sessions": len(out_sessions),
        "redacted_sessions": sum(1 for s in out_sessions if s["redacted"]),
        "redacted_minutes": sum(s["active_minutes"] for s in out_sessions if s["redacted"]),
        "projects": sorted({s["project_label"] for s in out_sessions}),
        "by_project": breakdown,
        "active_minutes": sum(s["active_minutes"] for s in out_sessions),
        "prompts": sum(len(s["prompts"]) for s in out_sessions),
        "errors": sum(s["errors"] for s in out_sessions),
        "tokens": {
            "input": sum(s["tokens"].get("input_tokens", 0) for s in out_sessions),
            "output": sum(s["tokens"].get("output_tokens", 0) for s in out_sessions),
        },
    }

    return {
        "date": day.isoformat(),
        "window": {"start": start.isoformat(timespec="seconds"), "end": end.isoformat(timespec="seconds")},
        "config": {
            "day_boundary": config.get("day_boundary", "04:00"),
            "excluded_projects": sorted(excluded),
            "prompt_max_chars": max_prompt,
            "max_sentences_per_session": max_sentences,
        },
        "totals": totals,
        "hour_histogram": histogram,
        "sessions": out_sessions,
        "diagnostics": {
            "lines_read": total_lines,
            "parse_failures": parse_failures,
            "excluded_projects": dict(skipped_projects),
        },
    }


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--date", required=True, help="YYYY-MM-DD, the day whose 04:00 window to collect")
    parser.add_argument("--projects-root", default=os.path.expanduser("~/.claude/projects"))
    parser.add_argument("--config", default=os.path.expanduser("~/.daily-digest/config.json"))
    parser.add_argument("--out", default="")
    args = parser.parse_args()

    day = datetime.strptime(args.date, "%Y-%m-%d").date()
    config = load_config(args.config)
    data = collect(args.projects_root, day, config)

    out = args.out or os.path.join(os.path.expanduser("~/.daily-digest/work"), f"day-{args.date}.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)

    totals = data["totals"]
    print(f"date={data['date']} window={data['window']['start']} -> {data['window']['end']}")
    print(f"sessions={totals['sessions']} projects={len(totals['projects'])} "
          f"active={totals['active_minutes']}min prompts={totals['prompts']} "
          f"errors={totals['errors']} lines={data['diagnostics']['lines_read']} "
          f"parse_failures={data['diagnostics']['parse_failures']}")
    if data["diagnostics"]["excluded_projects"]:
        print(f"excluded({config.get('exclude_mode', 'stats_only')}):", data["diagnostics"]["excluded_projects"])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
