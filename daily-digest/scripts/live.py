#!/usr/bin/env python3
"""Live view of today's activity for the desktop memo.

Scans only transcripts touched since the day boundary, so it stays fast.
Writes ~/.daily-digest/live.json. Read-only over the transcript files.
"""

import glob
import io
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timedelta

ROOT = os.path.expanduser("~/.daily-digest")
CONFIG = os.path.join(ROOT, "config.json")
OUT = os.path.join(ROOT, "live.json")


def load_config():
    try:
        return json.load(io.open(CONFIG, encoding="utf-8"))
    except Exception:
        return {}


def main():
    cfg = load_config()
    boundary = cfg.get("day_boundary", "04:00").split(":")
    now = datetime.now().astimezone()
    start = now.replace(hour=int(boundary[0]), minute=int(boundary[1]), second=0, microsecond=0)
    if start > now:
        start -= timedelta(days=1)

    excluded = set(cfg.get("exclude_projects") or [])
    root = os.path.expanduser(cfg.get("projects_root", "~/.claude/projects"))

    recent = []
    edited = defaultdict(int)
    minutes = defaultdict(float)
    counts = defaultdict(int)
    excluded_minutes = 0.0
    events = 0

    for path in glob.glob(os.path.join(root, "**", "*.jsonl"), recursive=True):
        try:
            if datetime.fromtimestamp(os.path.getmtime(path)).astimezone() < start:
                continue
        except OSError:
            continue
        project = os.path.basename(os.path.dirname(path))
        prev = None
        with io.open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                try:
                    row = json.loads(line)
                except Exception:
                    continue
                ts = row.get("timestamp")
                if not ts:
                    continue
                try:
                    moment = datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone()
                except ValueError:
                    continue
                if moment < start:
                    continue
                events += 1
                if (row.get("type") == "user" and not row.get("isMeta")
                        and not row.get("isSidechain") and project not in excluded):
                    content = (row.get("message") or {}).get("content")
                    texts = []
                    if isinstance(content, str):
                        texts = [content]
                    elif isinstance(content, list):
                        texts = [c.get("text") or "" for c in content
                                 if isinstance(c, dict) and c.get("type") == "text"]
                    for text in texts:
                        text = text.strip()
                        if len(text) < 6 or text.startswith(("<", "Base directory", "[Request", "[Image", "[粘贴")):
                            continue
                        recent.append(text)
                if row.get("type") == "assistant" and project not in excluded:
                    content = (row.get("message") or {}).get("content")
                    if isinstance(content, list):
                        for item in content:
                            if not isinstance(item, dict) or item.get("type") != "tool_use":
                                continue
                            if item.get("name") not in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
                                continue
                            path = (item.get("input") or {}).get("file_path") or ""
                            if not path or ".daily-digest" in path.lower() or "\temp\\" in path.lower():
                                continue
                            parts = path.replace("/", "\\").split("\\")
                            label = parts[-1] if len(parts) < 2 else parts[-2] + "/" + parts[-1]
                            edited[label] += 1

                if prev is not None and prev[0] == project:
                    gap = (moment - prev[1]).total_seconds() / 60
                    if 0 < gap <= 15:
                        minutes[project] += gap
                prev = (project, moment)

    projects = []
    letter = ord("A")
    for name, value in sorted(minutes.items(), key=lambda kv: -kv[1]):
        if name in excluded:
            label = f"已排除项目 · {chr(letter)}"
            letter += 1
            excluded_minutes += value
        else:
            label = name
        projects.append({
            "label": label,
            "redacted": name in excluded,
            "minutes": round(value),
            "key": __import__("hashlib").sha1(name.encode("utf-8")).hexdigest()[:6],
        })

    payload = {
        "date": now.strftime("%Y-%m-%d"),
        "updated": now.strftime("%H:%M"),
        "total_minutes": round(sum(minutes.values())),
        "excluded_minutes": round(excluded_minutes),
        "events": events,
        "projects": projects[:4],
        "recent": [t[:120] for t in recent[-8:]],
        "edited": [{ "name": k, "count": v } for k, v in sorted(edited.items(), key=lambda kv: -kv[1])[:6]],
    }
    with io.open(OUT, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=1)

    # 给桌面小窗读的纯文本：今天最近做过的几件事
    txt = os.path.join(ROOT, "today.txt")
    lines = ["# " + payload["date"] + " " + payload["updated"]]
    for f in payload["edited"][:4]:
        lines.append("改 " + f["name"])
    tail = 2 if len(lines) > 1 else 4
    for item in payload["recent"][-tail:]:
        item = " ".join(item.split())
        if len(item) > 46:
            item = item[:46] + "…"
        lines.append("问 " + item)
    io.open(txt, "w", encoding="utf-8").write(chr(10).join(lines) + chr(10))

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
