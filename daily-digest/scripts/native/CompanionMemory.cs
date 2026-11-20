using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// P4 长期记忆：文件存储 + 本地 BM25 + 每日提取 + 每周合并。
// 设计原则见 docs/companion-memory.md：写入不打分，价值靠使用涌现，只归档不删除。
partial class MemoWindow
{
    static readonly string ProfilePath = Path.Combine(CompanionDir, "profile.md");
    static readonly string MemoryPath = Path.Combine(CompanionDir, "memories.jsonl");
    static readonly string ArchivePath = Path.Combine(CompanionDir, "archive.jsonl");

    class MemItem
    {
        public string Id, Ts, Kind, Text, Last, SupersededBy, Src;
        public int Uses, Valid, Pin;
    }

    readonly List<MemItem> memories = new List<MemItem>();
    readonly object memLock = new object();
    string profileText = "";
    DateTime profileStamp = DateTime.MinValue;
    string memDoneDay = "", memMergeDay = "";
    bool memDirty;
    int memSeq;
    Window memoryWin;
    StackPanel memListPanel;

    void InitMemory()
    {
        LoadProfile();
        LoadMemories();
        ThreadPool.QueueUserWorkItem(delegate { EnsureMemoryMaintenance(); });
    }

    void LoadProfile()
    {
        try
        {
            if (!File.Exists(ProfilePath)) { profileText = ""; return; }
            var t = File.GetLastWriteTimeUtc(ProfilePath);
            if (t == profileStamp) return;
            profileStamp = t;
            profileText = File.ReadAllText(ProfilePath, Utf8).Trim();
        }
        catch (Exception ex) { Log("profile load failed: " + ex.Message); }
    }

    void LoadMemories()
    {
        try
        {
            if (!File.Exists(MemoryPath)) return;
            lock (memLock)
            {
                memories.Clear();
                foreach (var line in File.ReadAllLines(MemoryPath, Utf8))
                {
                    if (line.Trim().Length == 0) continue;
                    var m = new MemItem
                    {
                        Id = GetStr(line, "id", ""),
                        Ts = GetStr(line, "ts", ""),
                        Kind = GetStr(line, "kind", "fact"),
                        Text = GetStr(line, "text", ""),
                        Last = GetStr(line, "last", ""),
                        SupersededBy = GetStr(line, "superseded_by", ""),
                        Src = GetStr(line, "src", ""),
                        Uses = GetInt(line, "uses"),
                        Valid = GetInt(line, "valid"),
                        Pin = GetInt(line, "pin")
                    };
                    if (m.Valid == -99999) m.Valid = 1;
                    if (m.Uses == -99999) m.Uses = 0;
                    if (m.Pin == -99999) m.Pin = 0;
                    if (m.Text.Length > 0) memories.Add(m);
                }
                memSeq = memories.Count;
            }
        }
        catch (Exception ex) { Log("memory load failed: " + ex.Message); }
    }

    void SaveMemories()
    {
        try
        {
            Directory.CreateDirectory(CompanionDir);
            var sb = new StringBuilder();
            lock (memLock)
            {
                foreach (var m in memories)
                {
                    sb.Append("{\"id\":\"").Append(JsonEscape(m.Id))
                      .Append("\",\"ts\":\"").Append(JsonEscape(m.Ts))
                      .Append("\",\"kind\":\"").Append(JsonEscape(m.Kind))
                      .Append("\",\"text\":\"").Append(JsonEscape(m.Text))
                      .Append("\",\"uses\":").Append(m.Uses)
                      .Append(",\"last\":\"").Append(JsonEscape(m.Last))
                      .Append("\",\"valid\":").Append(m.Valid)
                      .Append(",\"pin\":").Append(m.Pin)
                      .Append(",\"superseded_by\":\"").Append(JsonEscape(m.SupersededBy))
                      .Append("\",\"src\":\"").Append(JsonEscape(m.Src))
                      .Append("\"}\r\n");
                }
            }
            File.WriteAllText(MemoryPath, sb.ToString(), Utf8);
            memDirty = false;
        }
        catch (Exception ex) { Log("memory save failed: " + ex.Message); }
    }

    // ---------- 检索 ----------

    // 中文按字二元组、英文按词，统一成词项集合
    static HashSet<string> Terms(string s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(s)) return set;
        var lower = s.ToLowerInvariant();
        var chars = new List<char>();
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c)) chars.Add(c);
            else
            {
                AddBigrams(set, chars);
                chars.Clear();
            }
        }
        AddBigrams(set, chars);

        var ascii = new StringBuilder();
        foreach (var c in lower)
        {
            if (c < 128 && char.IsLetterOrDigit(c)) ascii.Append(c);
            else
            {
                if (ascii.Length >= 2) set.Add(ascii.ToString());
                ascii.Length = 0;
            }
        }
        if (ascii.Length >= 2) set.Add(ascii.ToString());
        return set;
    }

    static void AddBigrams(HashSet<string> set, List<char> chars)
    {
        for (int i = 0; i + 1 < chars.Count; i++)
            set.Add(new string(new char[] { chars[i], chars[i + 1] }));
        if (chars.Count == 1) set.Add(chars[0].ToString());
    }

    static double RecencyWeight(string ts)
    {
        DateTime d;
        if (!DateTime.TryParse(ts, out d)) return 0.3;
        double days = Math.Max(0, (DateTime.Now - d).TotalDays);
        return Math.Pow(0.5, days / 60.0);
    }

    List<MemItem> PickMemories(string query, int top)
    {
        var picked = new List<MemItem>();
        if (string.IsNullOrEmpty(query)) return picked;
        var qTerms = Terms(query);
        if (qTerms.Count == 0) return picked;

        lock (memLock)
        {
            var docs = new List<MemItem>();
            foreach (var m in memories) if (m.Valid == 1) docs.Add(m);
            if (docs.Count == 0) return picked;

            var df = new Dictionary<string, int>();
            var docTerms = new List<HashSet<string>>();
            var docLens = new List<int>();
            int totalLen = 0;
            foreach (var d in docs)
            {
                var t = Terms(d.Text);
                docTerms.Add(t);
                docLens.Add(t.Count);
                totalLen += t.Count;
                foreach (var term in t)
                {
                    int c;
                    df.TryGetValue(term, out c);
                    df[term] = c + 1;
                }
            }

            double avgdl = Math.Max(1.0, totalLen / (double)docs.Count);
            const double k1 = 1.2, b = 0.75;
            var scores = new double[docs.Count];

            for (int i = 0; i < docs.Count; i++)
            {
                double s = 0;
                foreach (var term in qTerms)
                {
                    int dfv;
                    if (!df.TryGetValue(term, out dfv)) continue;
                    if (!docTerms[i].Contains(term)) continue;
                    double idf = Math.Log(1 + (docs.Count - dfv + 0.5) / (dfv + 0.5));
                    double tf = 1;
                    s += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * docLens[i] / avgdl));
                }
                if (s <= 0) continue;
                scores[i] = s + RecencyWeight(docs[i].Ts) * 0.8
                          + Math.Min(docs[i].Uses, 5) * 0.05
                          + (docs[i].Pin == 1 ? 0.6 : 0);
            }

            var idx = new List<int>();
            for (int i = 0; i < scores.Length; i++) if (scores[i] > 0) idx.Add(i);
            idx.Sort(delegate(int x, int y) { return scores[y].CompareTo(scores[x]); });

            for (int i = 0; i < idx.Count && picked.Count < top; i++)
            {
                var m = docs[idx[i]];
                picked.Add(m);
                m.Uses++;
                m.Last = DateTime.Now.ToString("yyyy-MM-dd");
                memDirty = true;
            }
        }
        return picked;
    }

    // 拼进 system prompt；总长度由 BuildRequest 再兜底截断
    string MemoryBlock(string query)
    {
        var sb = new StringBuilder();
        LoadProfile();
        if (!string.IsNullOrEmpty(profileText))
        {
            sb.Append("关于他，你知道这些长期事实：\n")
              .Append(profileText.Length > 400 ? profileText.Substring(0, 400) : profileText)
              .Append("\n");
        }
        if (query != null && query.Trim().Length >= 4)
        {
            var picked = PickMemories(query.Trim(), 3);
            if (picked.Count > 0)
            {
                Log("memory hit: " + picked.Count);
                ThreadPool.QueueUserWorkItem(delegate { if (memDirty) SaveMemories(); });
                sb.Append("你记得这些小事（可能相关）：\n");
                foreach (var m in picked)
                    sb.Append("- ").Append(m.Text.Length > 50 ? m.Text.Substring(0, 50) : m.Text).Append("\n");
            }
        }
        return sb.ToString();
    }

    // ---------- 后台维护：每日提取 + 每周合并 ----------

    void EnsureMemoryMaintenance()
    {
        try
        {
            LoadProfile();
            if (memories.Count == 0) LoadMemories();
            var today = DateTime.Today;

            if (Directory.Exists(ChatDir))
            {
                var todo = new List<DateTime>();
                foreach (var f in Directory.GetFiles(ChatDir, "*.jsonl"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    DateTime d;
                    if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                    if (d >= today) continue;
                    if (memDoneDay.Length > 0 && string.CompareOrdinal(name, memDoneDay) <= 0) continue;
                    todo.Add(d);
                }
                todo.Sort();
                int done = 0;
                foreach (var d in todo)
                {
                    if (done >= 2) break;
                    if (ExtractDay(d))
                    {
                        memDoneDay = d.ToString("yyyy-MM-dd");
                        SaveProactiveState();
                        done++;
                    }
                    else break;
                }
            }

            DateTime lastMerge;
            if (!DateTime.TryParse(memMergeDay, out lastMerge)) lastMerge = DateTime.MinValue;
            if ((today - lastMerge).TotalDays >= 7)
            {
                if (ConsolidateMemories())
                {
                    memMergeDay = today.ToString("yyyy-MM-dd");
                    SaveProactiveState();
                }
            }

            if (memDirty) SaveMemories();
        }
        catch (Exception ex) { Log("memory maintenance failed: " + ex.Message); }
    }

    bool ExtractDay(DateTime day)
    {
        if (apiKey.Length == 0) return false;
        try
        {
            var path = Path.Combine(ChatDir, day.ToString("yyyy-MM-dd") + ".jsonl");
            if (!File.Exists(path)) return false;

            var chat = new StringBuilder();
            foreach (var line in File.ReadAllLines(path, Utf8))
            {
                var role = GetStr(line, "role", "");
                var text = GetStr(line, "text", "");
                if (text.Length == 0) continue;
                chat.Append(role == "user" ? "他：" : "她：").Append(text).Append("\n");
                if (chat.Length > 4000) break;
            }
            if (chat.Length < 30) return true;

            var existing = new StringBuilder();
            lock (memLock)
            {
                int n = 0;
                for (int i = memories.Count - 1; i >= 0 && n < 40; i--)
                {
                    var m = memories[i];
                    if (m.Valid != 1) continue;
                    existing.Append(m.Id).Append("|").Append(m.Text).Append("\n");
                    n++;
                }
            }

            var sys = "你是记忆整理器。只从以“他：”开头的用户发言里提取他**明确说过**的信息。\n"
                    + "规则：不推断、不总结性格、不编造；每条都必须能在原话里找到依据；一次情绪波动不算长期事实。\n"
                    + "只记以后还用得上的：喜好、习惯、关系、计划、雷区、重要经历。不记天气、寒暄、助手自己的话。\n"
                    + "每行一条：ACTION|kind|text|ID。ACTION 只能是 ADD 或 SUPERSEDE（ID 填旧编号）；"
                    + "kind 只能是 fact/preference/episode/boundary；text 用第三人称“他…”，不超过 30 字。\n"
                    + "最多 4 条；拿不准就不记；没有可记的就输出 NONE。";
            var user = "现有记忆（编号|内容）：\n" + existing + "\n对话：\n" + chat;

            var body = "{\"model\":\"" + JsonEscape(apiModel) + "\",\"max_tokens\":500,\"temperature\":0.2,\"messages\":["
                     + "{\"role\":\"system\",\"content\":\"" + JsonEscape(sys) + "\"},"
                     + "{\"role\":\"user\",\"content\":\"" + JsonEscape(user) + "\"}]}";
            int code;
            var raw = HttpPostJson(apiBase.TrimEnd('/') + "/chat/completions", apiKey, apiProxy, body, out code);
            if (code != 200) { Log("memory extract http " + code); return false; }
            var content = JsonValue(raw, "content");
            if (string.IsNullOrEmpty(content)) { Log("memory extract empty"); return false; }

            ApplyMemoryOps(content, day);
            Log("memory extract ok: " + day.ToString("yyyy-MM-dd"));
            return true;
        }
        catch (Exception ex) { Log("memory extract failed: " + ex.Message); return false; }
    }

    void ApplyMemoryOps(string content, DateTime day)
    {
        lock (memLock)
        {
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                var action = parts[0].Trim().ToUpperInvariant();
                var kind = parts[1].Trim();
                var text = parts[2].Trim();
                var oldId = parts.Length > 3 ? parts[3].Trim() : "";
                if (text.Length < 2) continue;
                if (text.Length > 40) text = text.Substring(0, 40);
                if (kind != "fact" && kind != "preference" && kind != "episode" && kind != "boundary") kind = "fact";
                if (IsDuplicateLocked(text)) continue;

                var id = NewMemId();
                if (action == "SUPERSEDE" && oldId.Length > 0)
                {
                    var old = memories.Find(delegate(MemItem m) { return m.Id == oldId && m.Valid == 1; });
                    if (old != null) { old.Valid = 0; old.SupersededBy = id; }
                }
                memories.Add(new MemItem
                {
                    Id = id,
                    Ts = day.ToString("yyyy-MM-dd"),
                    Kind = kind,
                    Text = text,
                    Uses = 0,
                    Last = day.ToString("yyyy-MM-dd"),
                    Valid = 1,
                    Pin = 0,
                    SupersededBy = "",
                    Src = day.ToString("yyyy-MM-dd")
                });
            }
            PruneLocked();
        }
        SaveMemories();
    }

    string NewMemId()
    {
        memSeq++;
        return "m" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + memSeq;
    }

    static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        var letters = new List<char>();
        foreach (var c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) letters.Add(c);
        for (int i = 0; i + 1 < letters.Count; i++)
            set.Add(new string(new char[] { letters[i], letters[i + 1] }));
        return set;
    }

    static double Similarity(string a, string b)
    {
        var x = Bigrams(a);
        var y = Bigrams(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        int inter = 0;
        foreach (var t in x) if (y.Contains(t)) inter++;
        return inter / (double)Math.Min(x.Count, y.Count);
    }

    bool IsDuplicateLocked(string text)
    {
        foreach (var m in memories)
        {
            if (m.Valid != 1) continue;
            if (Similarity(text, m.Text) >= 0.75) return true;
        }
        return false;
    }

    static double KeepScore(MemItem m)
    {
        return (m.Pin == 1 ? 100 : 0) + RecencyWeight(m.Ts) * 2 + Math.Min(m.Uses, 8) * 0.3;
    }

    // 超过 500 条时把最弱的 50 条移进 archive，不物理删除
    void PruneLocked()
    {
        int valid = 0;
        foreach (var m in memories) if (m.Valid == 1) valid++;
        if (valid <= 500) return;

        var cand = memories.FindAll(delegate(MemItem m) { return m.Valid == 1 && m.Pin != 1; });
        cand.Sort(delegate(MemItem a, MemItem b) { return KeepScore(a).CompareTo(KeepScore(b)); });

        int need = valid - 500;
        var drop = new HashSet<string>();
        for (int i = 0; i < cand.Count && drop.Count < need; i++) drop.Add(cand[i].Id);
        if (drop.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var m in memories)
        {
            if (!drop.Contains(m.Id)) continue;
            sb.Append("{\"id\":\"").Append(JsonEscape(m.Id))
              .Append("\",\"ts\":\"").Append(JsonEscape(m.Ts))
              .Append("\",\"kind\":\"").Append(JsonEscape(m.Kind))
              .Append("\",\"text\":\"").Append(JsonEscape(m.Text))
              .Append("\",\"src\":\"").Append(JsonEscape(m.Src)).Append("\"}\r\n");
        }
        try
        {
            Directory.CreateDirectory(CompanionDir);
            File.AppendAllText(ArchivePath, sb.ToString(), Utf8);
        }
        catch (Exception ex) { Log("archive append failed: " + ex.Message); }

        memories.RemoveAll(delegate(MemItem m) { return drop.Contains(m.Id); });
        Log("memory archived: " + drop.Count);
    }

    // ---------- 每周合并 ----------

    bool ConsolidateMemories()
    {
        if (apiKey.Length == 0) return false;
        List<MemItem> valid;
        lock (memLock)
        {
            valid = memories.FindAll(delegate(MemItem m) { return m.Valid == 1; });
        }
        if (valid.Count < 6) return true;

        var used = new HashSet<string>();
        var clusters = new List<List<MemItem>>();
        for (int i = 0; i < valid.Count && clusters.Count < 3; i++)
        {
            var a = valid[i];
            if (used.Contains(a.Id)) continue;
            var cluster = new List<MemItem> { a };
            for (int j = i + 1; j < valid.Count; j++)
            {
                var b = valid[j];
                if (used.Contains(b.Id) || b.Kind != a.Kind) continue;
                if (Similarity(a.Text, b.Text) >= 0.5) cluster.Add(b);
            }
            if (cluster.Count >= 3)
            {
                foreach (var m in cluster) used.Add(m.Id);
                clusters.Add(cluster);
            }
        }
        if (clusters.Count == 0) return true;

        bool any = false;
        foreach (var cluster in clusters)
        {
            try
            {
                if (MergeCluster(cluster)) any = true;
            }
            catch (Exception ex) { Log("memory merge failed: " + ex.Message); }
        }
        if (any) SaveMemories();
        return true;
    }

    bool MergeCluster(List<MemItem> cluster)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var m in cluster) sb.Append(i++).Append(". ").Append(m.Text).Append("\n");

        var sys = "把下面这些关于同一个人的零散记忆合并成一到两条更概括的事实，保留具体名字、数字、时间。"
                + "每行格式：kind|text，最多两行，每行不超过 35 字，不要解释。";
        var body = "{\"model\":\"" + JsonEscape(apiModel) + "\",\"max_tokens\":300,\"temperature\":0.2,\"messages\":["
                 + "{\"role\":\"system\",\"content\":\"" + JsonEscape(sys) + "\"},"
                 + "{\"role\":\"user\",\"content\":\"" + JsonEscape(sb.ToString()) + "\"}]}";
        int code;
        var raw = HttpPostJson(apiBase.TrimEnd('/') + "/chat/completions", apiKey, apiProxy, body, out code);
        if (code != 200) return false;
        var content = JsonValue(raw, "content");
        if (string.IsNullOrEmpty(content)) return false;

        var newIds = new List<string>();
        lock (memLock)
        {
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split('|');
                if (parts.Length < 2) continue;
                var kind = parts[0].Trim();
                var text = parts[1].Trim();
                if (text.Length < 2) continue;
                if (text.Length > 40) text = text.Substring(0, 40);
                if (IsDuplicateLocked(text)) continue;
                var id = NewMemId();
                newIds.Add(id);
                memories.Add(new MemItem
                {
                    Id = id,
                    Ts = DateTime.Now.ToString("yyyy-MM-dd"),
                    Kind = kind,
                    Text = text,
                    Uses = 0,
                    Last = DateTime.Now.ToString("yyyy-MM-dd"),
                    Valid = 1,
                    Pin = 0,
                    SupersededBy = "",
                    Src = "merge"
                });
            }
            if (newIds.Count == 0) return false;
            foreach (var m in cluster)
            {
                m.Valid = 0;
                m.SupersededBy = newIds[0];
            }
        }
        Log("memory merged: " + cluster.Count + " -> " + newIds.Count);
        return true;
    }

    // ---------- 管理窗 ----------

    void OpenMemoryWindow()
    {
        if (memoryWin != null && memoryWin.IsVisible) { memoryWin.Activate(); return; }

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            Width = 460,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI")
        };

        var shell = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(246, 28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = "她记得的事",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "✕", Width = 26, Height = 26, FontSize = 12, Foreground = Muted,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        close.Click += delegate { win.Close(); };
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        root.Children.Add(head);

        memListPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = memListPanel
        };
        scroll.Resources.Add(typeof(ScrollBar), ThinScrollStyle());
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var tidy = new Button
        {
            Content = "整理昨天", FontSize = 11.5, Padding = new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0)
        };
        tidy.Click += delegate
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { ExtractDay(DateTime.Today.AddDays(-1)); } catch { }
                Dispatcher.BeginInvoke((Action)delegate { FillMemoryList(); });
            });
        };
        var merge = new Button
        {
            Content = "合并相近", FontSize = 11.5, Padding = new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0)
        };
        merge.Click += delegate
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { ConsolidateMemories(); } catch { }
                Dispatcher.BeginInvoke((Action)delegate { FillMemoryList(); });
            });
        };
        var openFile = new Button
        {
            Content = "打开文件", FontSize = 11.5, Padding = new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        openFile.Click += delegate
        {
            try
            {
                if (!File.Exists(MemoryPath)) File.WriteAllText(MemoryPath, "", Utf8);
                System.Diagnostics.Process.Start("notepad", "\"" + MemoryPath + "\"");
            }
            catch { }
        };
        footer.Children.Add(tidy);
        footer.Children.Add(merge);
        footer.Children.Add(openFile);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        shell.Child = root;
        win.Content = shell;
        memoryWin = win;
        win.Resources.Add(typeof(Button),
            FlatStyle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), new CornerRadius(8)));
        win.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject && InsideButton((DependencyObject)e.OriginalSource)) return;
            try { win.DragMove(); } catch { }
        };
        win.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) win.Close(); };
        win.Closed += delegate { memoryWin = null; SaveMemories(); };
        FillMemoryList();
        win.ShowDialog();
    }

    void FillMemoryList()
    {
        if (memListPanel == null) return;
        memListPanel.Children.Clear();
        List<MemItem> snapshot;
        lock (memLock) snapshot = new List<MemItem>(memories);
        snapshot.Sort(delegate(MemItem a, MemItem b)
        {
            if (a.Valid != b.Valid) return b.Valid.CompareTo(a.Valid);
            return string.CompareOrdinal(b.Ts, a.Ts);
        });

        if (snapshot.Count == 0)
        {
            memListPanel.Children.Add(new TextBlock
            {
                Text = "还没有记忆。聊过一天之后，点「整理昨天」试试。",
                Foreground = Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8)
            });
            return;
        }

        foreach (var m in snapshot)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = m.Text,
                Foreground = m.Valid == 1 ? Ink : Muted,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (m.Valid != 1) text.TextDecorations = TextDecorations.Strikethrough;
            row.Children.Add(text);

            var meta = new TextBlock
            {
                Text = (m.Kind == "preference" ? "偏好" : m.Kind == "boundary" ? "雷区" : m.Kind == "episode" ? "经历" : "事实")
                     + " · " + m.Ts + (m.Pin == 1 ? " · 置顶" : "") + (m.Uses > 0 ? " · 用过 " + m.Uses : ""),
                Foreground = Muted,
                FontSize = 10,
                Margin = new Thickness(8, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(meta, 1);
            row.Children.Add(meta);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var pin = new Button
            {
                Content = m.Pin == 1 ? "取消钉" : "钉住",
                FontSize = 10.5,
                Padding = new Thickness(7, 3, 7, 3),
                Background = Brushes.Transparent,
                Foreground = m.Pin == 1 ? Accent : Muted,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            var item = m;
            pin.Click += delegate
            {
                lock (memLock) item.Pin = item.Pin == 1 ? 0 : 1;
                SaveMemories();
                FillMemoryList();
            };
            var del = new Button
            {
                Content = "忘记",
                FontSize = 10.5,
                Padding = new Thickness(7, 3, 7, 3),
                Background = Brushes.Transparent,
                Foreground = Muted,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            del.Click += delegate
            {
                lock (memLock) memories.Remove(item);
                SaveMemories();
                FillMemoryList();
            };
            btns.Children.Add(pin);
            btns.Children.Add(del);
            Grid.SetColumn(btns, 2);
            row.Children.Add(btns);

            memListPanel.Children.Add(row);
        }
    }
}
