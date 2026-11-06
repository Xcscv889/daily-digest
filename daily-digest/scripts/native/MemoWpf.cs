using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

class MemoApp : Application
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\DailyDigestMemoWpf", out created))
        {
            if (!created) return;
            var app = new MemoApp();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Startup += delegate { new MemoWindow().Show(); };
            app.Run();
        }
    }
}

class MemoWindow : Window
{
    const string Root = @".daily-digest";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Root);
    static readonly string DocPath = Path.Combine(Dir, "current.md");
    static readonly string TasksPath = Path.Combine(Dir, "tasks.md");
    static readonly string StatePath = Path.Combine(Dir, "memo.json");
    static readonly string LogPath = Path.Combine(Dir, "memo.log");
    static readonly string DonePath = Path.Combine(Dir, "done.md");
    static readonly string TodayPath = Path.Combine(Dir, "today.txt");
    static readonly string LiveScript = Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(typeof(MemoWindow).Assembly.Location) ?? ".") ?? ".", "live.py");

    const int WM_HOTKEY = 0x0312, SNAP = 24, PEEK = 12;
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    struct POINT { public int X; public int Y; }

    const string Xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        CornerRadius='14' Background='#FF0F0D13' BorderBrush='#26FFFFFF' BorderThickness='1'>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='*'/>
      <RowDefinition Height='Auto'/>
    </Grid.RowDefinitions>
    <Grid Name='Header' Grid.Row='0' Height='38' Background='Transparent'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <Ellipse Grid.Column='0' Width='7' Height='7' Margin='16,0,8,0' VerticalAlignment='Center' Fill='#FF74A9D4'/>
      <Grid Grid.Column='1'>
        <TextBlock Name='TitleText' HorizontalAlignment='Left' VerticalAlignment='Center' FontSize='13' FontWeight='SemiBold'
                   Foreground='#FFE9F0F7' Text='日报' TextTrimming='CharacterEllipsis' MaxWidth='150'/>

      </Grid>
      <Grid Name='ClockHost' Grid.Column='0' Grid.ColumnSpan='5' HorizontalAlignment='Center'
            VerticalAlignment='Center' IsHitTestVisible='False'/>
      <TextBlock Name='WeatherText' Grid.Column='2' VerticalAlignment='Center' FontSize='11'
                 Foreground='#96AABE' Margin='0,0,10,0'/>
      <Button Name='RefreshBtn' Grid.Column='3' Content='⟳' Width='26' Height='26' Margin='0,0,2,0'
              Background='Transparent' BorderThickness='0' Foreground='#96AABE' Cursor='Hand' ToolTip='刷新'/>
      <Button Name='FoldBtn' Grid.Column='4' Content='–' Width='26' Height='26' Margin='0,0,8,0'
              Background='Transparent' BorderThickness='0' Foreground='#96AABE' Cursor='Hand' ToolTip='折叠'/>
    </Grid>
    <ScrollViewer Name='Body' Grid.Row='1' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'
                  VerticalAlignment='Stretch'>
      <StackPanel Name='BodyPanel' Margin='16,0,16,6'/>
    </ScrollViewer>
    <Grid Grid.Row='2' Name='InputRow' Margin='16,2,16,12'>
      <TextBox Name='QuickInput' MinHeight='30' MaxHeight='96' FontSize='12.5' Padding='8,5'
               TextWrapping='Wrap' AcceptsReturn='True' VerticalScrollBarVisibility='Auto'
               Background='#22FFFFFF' BorderBrush='#33FFFFFF' Foreground='#FFE9F0F7' CaretBrush='#FFE9F0F7'
               VerticalContentAlignment='Center'/>
      <TextBlock Name='QuickHint' Text='记一件事，回车保存' FontSize='12' IsHitTestVisible='False'
                 Margin='10,0,0,0' VerticalAlignment='Center' Foreground='#96AABE'/>
    </Grid>
  </Grid>
</Border>";

    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(233, 240, 247));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(150, 170, 190));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(116, 169, 212));
    static readonly Brush DoneB = new SolidColorBrush(Color.FromRgb(121, 201, 154));
    static readonly Brush TodoB = new SolidColorBrush(Color.FromRgb(210, 160, 91));

    Border card;
    Grid header;
    StackPanel bodyPanel;
    TextBlock titleText, quickHint, weatherText, petText;
    Clock7 clock7;
    TextBox quickInput;
    Grid inputRow;
    Button foldBtn, refreshBtn;
    ContextMenu menu;

    DispatcherTimer refreshTimer, autoTimer;
    HwndSource source;
    DateTime stampDoc = DateTime.MinValue, stampTasks = DateTime.MinValue;

    bool collapsed, hidden, autoHide = true, voiceWake = true, stateDirty;
    bool todoCollapsed, doneCollapsed, showDone = true;
    System.Speech.Recognition.SpeechRecognitionEngine speech;
    DateTime hideAt = DateTime.MaxValue;
    double opacity = 1.0;
    bool snapTop, snapRight, snapBottom, snapLeft;
    bool dragging;

    public MemoWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Manual;
        Height = 320;
        card = (Border)XamlReader.Parse(Xaml);
        Content = card;

        header = (Grid)card.FindName("Header");
        bodyPanel = (StackPanel)card.FindName("BodyPanel");
        titleText = (TextBlock)card.FindName("TitleText");
        weatherText = (TextBlock)card.FindName("WeatherText");
        var clockHost = (Grid)card.FindName("ClockHost");
        clock7 = new Clock7();
        petText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 170, 190)), FontSize = 12, Visibility = Visibility.Collapsed };
        clockHost.Children.Add(clock7);
        clockHost.Children.Add(petText);
        quickInput = (TextBox)card.FindName("QuickInput");
        inputRow = (Grid)quickInput.Parent;
        quickHint = (TextBlock)card.FindName("QuickHint");
        foldBtn = (Button)card.FindName("FoldBtn");
        refreshBtn = (Button)card.FindName("RefreshBtn");

        BuildMenu();
        LoadState();

        header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            if (InsideButton((DependencyObject)e.OriginalSource)) return;
            StopAutoHide();
            DragMove();
            Snap();
            SaveState();
        };
        header.MouseRightButtonUp += delegate { menu.IsOpen = true; };
        card.MouseRightButtonUp += delegate(object s2, MouseButtonEventArgs e2)
        {
            menu.IsOpen = true;
            e2.Handled = true;
        };
        refreshBtn.Click += delegate { Render(); };
        foldBtn.Click += delegate { SetCollapsed(!collapsed); };

        quickInput.TextChanged += delegate
        {
            int lines = quickInput.LineCount < 1 ? 1 : quickInput.LineCount;
            quickInput.Height = Math.Min(96, Math.Max(30, 10 + 20 * lines));
            quickHint.Visibility = quickInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        quickInput.PreviewKeyDown += OnInputKey;
        quickInput.GotFocus += delegate { quickHint.Visibility = Visibility.Collapsed; };

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refreshTimer.Tick += delegate { CheckFiles(); };
        refreshTimer.Start();

        autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        autoTimer.Tick += delegate { AutoHideTick(); };
        autoTimer.Start();

        StateChanged += delegate
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
                Width = 360;
                Snap();
                Log("window state reset to normal");
            }
        };

        Loaded += delegate
        {
            var wa = SystemParameters.WorkArea;
            if (Left == 0 && Top == 0) { Left = wa.Right - Width; Top = wa.Top; }
            Opacity = opacity;
            Snap();
            Render();
            source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(WndProc);
            try { RegisterHotKey(new WindowInteropHelper(this).Handle, 1, 3, (uint)Key.N); } catch { }
            StartClock();
            RefreshLive();
            var liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            liveTimer.Tick += delegate { RefreshLive(); };
            liveTimer.Start();
            LoadWeather();
            if (voiceWake) StartVoice();
            var weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            weatherTimer.Tick += delegate { LoadWeather(); };
            weatherTimer.Start();
        };
        Closed += delegate { StopVoice(); try { UnregisterHotKey(new WindowInteropHelper(this).Handle, 1); } catch { } SaveState(); };
    }

    bool showPet;
    DispatcherTimer clockTimer;
    int petFrame;
    static readonly string[] PetFrames = new string[] {
        "(•‿•)", "(•_•)", "(¬‿¬)", "(・ω・)", "(￣▽￣)", "(´･ω･`)", "(＾▽＾)"
    };

    void StartClock()
    {
        clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        clockTimer.Tick += delegate
        {
            if (clock7 == null) return;
            if (showPet)
            {
                petFrame = (petFrame + 1) % PetFrames.Length;
                clock7.Visibility = Visibility.Collapsed;
                petText.Text = PetFrames[petFrame];
                petText.Visibility = Visibility.Visible;
            }
            else
            {
                petText.Visibility = Visibility.Collapsed;
                clock7.Visibility = Visibility.Visible;
                clock7.Value = DateTime.Now.ToString("HH:mm");
                clock7.InvalidateMeasure();
                clock7.InvalidateVisual();
            }
        };
        clockTimer.Start();
    }

    void StartVoice()
    {
        try
        {
            if (speech != null) return;
            System.Speech.Recognition.RecognizerInfo info = null;
            foreach (var r in System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers())
                if (r.Culture.Name.StartsWith("zh")) { info = r; break; }
            if (info == null) { Log("voice: 没有中文语音识别引擎"); return; }

            speech = new System.Speech.Recognition.SpeechRecognitionEngine(info);
            var choices = new System.Speech.Recognition.Choices();
            choices.Add(new string[] { "芝麻开门", "芝麻开门口令", "打开日报", "日报出来", "小窗出来", "日报小窗" });
            var gb = new System.Speech.Recognition.GrammarBuilder(choices);
            speech.LoadGrammar(new System.Speech.Recognition.Grammar(gb));
            speech.SpeechRecognized += delegate(object s2, System.Speech.Recognition.SpeechRecognizedEventArgs e2)
            {
                if (e2.Result == null) return;
                double conf = e2.Result.Confidence;
                Log("voice: '" + e2.Result.Text + "' conf=" + conf.ToString("0.00"));
                if (conf < 0.55) return;
                Dispatcher.BeginInvoke((Action)delegate
                {
                    SetCollapsed(false);
                    ShowWindow();
                    Activate();
                    quickInput.Focus();
                });
            };
            speech.SpeechRecognitionRejected += delegate(object s2, System.Speech.Recognition.SpeechRecognitionRejectedEventArgs e2)
            {
                if (e2.Result != null && e2.Result.Confidence > 0.4)
                    Log("voice rejected: '" + e2.Result.Text + "' conf=" + e2.Result.Confidence.ToString("0.00"));
            };
            speech.SetInputToDefaultAudioDevice();
            speech.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
            Log("voice: 监听中（芝麻开门）");
        }
        catch (Exception ex) { Log("voice error: " + ex.Message); speech = null; }
    }

    void StopVoice()
    {
        try { if (speech != null) { speech.RecognizeAsyncStop(); speech.Dispose(); speech = null; Log("voice: 已停止"); } }
        catch { }
    }

    void LoadWeather()
    {
        try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; }
        catch { }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string text = "";
            try
            {
                double lat = 39.90, lon = 116.40;
                try
                {
                    var cfg = File.Exists(Path.Combine(Dir, "config.json")) ? File.ReadAllText(Path.Combine(Dir, "config.json")) : "";
                    double t = GetDouble(cfg, "latitude", 0);
                    if (Math.Abs(t) > 0.001) lat = t;
                    t = GetDouble(cfg, "longitude", 0);
                    if (Math.Abs(t) > 0.001) lon = t;
                }
                catch { }

                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + "&longitude=" + lon.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + "&current=temperature_2m,weather_code&timezone=auto";
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 8000;
                req.Proxy = null;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var mTemp = System.Text.RegularExpressions.Regex.Match(json, "\"temperature_2m\":\\s*(-?\\d+(?:\\.\\d+)?)");
                    var mCode = System.Text.RegularExpressions.Regex.Match(json, "\"weather_code\":\\s*(\\d+)");
                    if (mTemp.Success)
                    {
                        double temp = double.Parse(mTemp.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        int code = mCode.Success ? int.Parse(mCode.Groups[1].Value) : -1;
                        text = WeatherIcon(code) + " " + Math.Round(temp) + "°";
                    }
                }
            }
            catch (Exception ex) { text = "天气不可用"; Log("weather error: " + ex.Message); }

            Dispatcher.BeginInvoke((Action)delegate
            {
                if (weatherText != null) weatherText.Text = text;
            });
        });
    }

    static string WeatherIcon(int code)
    {
        if (code < 0) return "";
        if (code == 0 || code == 1) return "☀";
        if (code == 2) return "⛅";
        if (code == 3) return "☁";
        if (code == 45 || code == 48) return "🌫";
        if (code >= 51 && code <= 67) return "🌧";
        if (code >= 71 && code <= 77) return "❄";
        if (code >= 80 && code <= 82) return "🌧";
        if (code >= 85 && code <= 86) return "❄";
        if (code >= 95) return "⛈";
        return "☁";
    }

    static bool InsideButton(DependencyObject node)
    {
        for (int i = 0; i < 20 && node != null; i++)
        {
            if (node is Button) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            SetCollapsed(false);
            ShowWindow();
            try
            {
                uint pid;
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, out pid);
                uint me = GetCurrentThreadId();
                AttachThreadInput(me, fgThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(me, fgThread, false);
            }
            catch { }
            Activate();
            quickInput.Focus();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    void BuildMenu()
    {
        menu = new ContextMenu();
        menu.Items.Add(Item("刷新", delegate { Render(); }));
        menu.Items.Add(Item("打开全文", delegate { OpenDoc(); }));
        var hide = new MenuItem { Header = "贴边自动隐藏", IsCheckable = true, IsChecked = autoHide };
        hide.Click += delegate { autoHide = hide.IsChecked; if (!autoHide) ShowWindow(); stateDirty = true; SaveState(); };
        menu.Items.Add(hide);

        var showDoneItem = new MenuItem { Header = "显示「今天做了」", IsCheckable = true, IsChecked = showDone };
        showDoneItem.Click += delegate { showDone = showDoneItem.IsChecked; stateDirty = true; SaveState(); Render(); };
        menu.Items.Add(showDoneItem);

        var pet = new MenuItem { Header = "显示小宠物（代替时间）", IsCheckable = true, IsChecked = showPet };
        pet.Click += delegate { showPet = pet.IsChecked; stateDirty = true; SaveState(); };
        menu.Items.Add(pet);

        var voice = new MenuItem { Header = "语音唤出（说「芝麻开门」）", IsCheckable = true, IsChecked = voiceWake };
        voice.Click += delegate { voiceWake = voice.IsChecked; if (voiceWake) StartVoice(); else StopVoice(); stateDirty = true; SaveState(); };
        menu.Items.Add(voice);
        var opacityMenu = new MenuItem { Header = "透明度" };
        double[] levels = new double[] { 1.0, 0.92, 0.8, 0.6 };
        foreach (var level in levels)
        {
            double v = level;
            var mi = new MenuItem { Header = ((int)(v * 100)).ToString() + "%", IsCheckable = true, IsChecked = Math.Abs(opacity - v) < 0.01 };
            mi.Click += delegate { opacity = v; Opacity = v; stateDirty = true; SaveState(); };
            opacityMenu.Items.Add(mi);
        }
        menu.Items.Add(opacityMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出", delegate { SaveState(); Close(); }));
    }

    static MenuItem Item(string text, RoutedEventHandler handler)
    {
        var mi = new MenuItem { Header = text };
        mi.Click += handler;
        return mi;
    }

    // ---------- snap ----------
    void StopAutoHide() { }

    void Snap()
    {
        var wa = SystemParameters.WorkArea;
        snapLeft = Math.Abs(Left - wa.Left) < SNAP;
        snapTop = Math.Abs(Top - wa.Top) < SNAP;
        snapRight = Math.Abs(wa.Right - (Left + ActualWidth)) < SNAP;
        snapBottom = Math.Abs(wa.Bottom - (Top + ActualHeight)) < SNAP;
        if (snapLeft) Left = wa.Left;
        if (snapRight) Left = wa.Right - ActualWidth;
        if (snapTop) Top = wa.Top;
        if (snapBottom) Top = wa.Bottom - ActualHeight;
        UpdateCorners();
    }

    void UpdateCorners()
    {
        double tr = snapTop || snapRight ? 0 : 14;
        double tl = snapTop || snapLeft ? 0 : 14;
        double br = snapBottom || snapRight ? 0 : 14;
        double bl = snapBottom || snapLeft ? 0 : 14;
        card.CornerRadius = new CornerRadius(tl, tr, br, bl);
    }

    bool Docked { get { return snapTop || snapBottom || snapLeft || snapRight; } }

    // ---------- autohide ----------
    void AutoHideTick()
    {
        if (!autoHide || !Docked || dragging) return;
        POINT p;
        if (!GetCursorPos(out p)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double cx = p.X / scale, cy = p.Y / scale;
        bool inside = cx >= Left - 10 && cx <= Left + ActualWidth + 10 && cy >= Top - 10 && cy <= Top + ActualHeight + 10;
        if (inside)
        {
            hideAt = DateTime.MaxValue;
            if (hidden) ShowWindow();
        }
        else if (hideAt == DateTime.MaxValue)
        {
            hideAt = DateTime.Now;
        }
        else if ((DateTime.Now - hideAt).TotalSeconds >= 3)
        {
            if (!hidden) HideWindow();
        }
    }

    void HideWindow()
    {
        var wa = SystemParameters.WorkArea;
        if (snapTop) Top = wa.Top - ActualHeight + PEEK;
        if (snapBottom) Top = wa.Bottom - PEEK;
        if (snapLeft) Left = wa.Left - ActualWidth + PEEK;
        if (snapRight) Left = wa.Right - PEEK;
        hidden = true;
        Log("hide top=" + Top + " left=" + Left + " h=" + ActualHeight);
    }

    void ShowWindow()
    {
        var wa = SystemParameters.WorkArea;
        if (snapTop) Top = wa.Top;
        if (snapBottom) Top = wa.Bottom - ActualHeight;
        if (snapLeft) Left = wa.Left;
        if (snapRight) Left = wa.Right - ActualWidth;
        hidden = false;
        Log("show top=" + Top + " left=" + Left);
    }

    // ---------- files ----------
    void CheckFiles()
    {
        try
        {
            var a = File.Exists(DocPath) ? File.GetLastWriteTimeUtc(DocPath) : DateTime.MinValue;
            var b = File.Exists(TasksPath) ? File.GetLastWriteTimeUtc(TasksPath) : DateTime.MinValue;
            if (a != stampDoc || b != stampTasks) Render();
        }
        catch { }
    }

    static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Utf8) : ""; }
        catch { return ""; }
    }

    static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + msg + "\r\n", Utf8); }
        catch { }
    }

    // ---------- render ----------
    void Render()
    {
        try
        {
            stampDoc = File.Exists(DocPath) ? File.GetLastWriteTimeUtc(DocPath) : DateTime.MinValue;
            stampTasks = File.Exists(TasksPath) ? File.GetLastWriteTimeUtc(TasksPath) : DateTime.MinValue;

            bodyPanel.Children.Clear();

            // 1) 今天要做的（来自 tasks.md）
            var tasks = ReadTasks();
            int openCount = 0;
            foreach (var t in tasks) if (!t.Done) openCount++;
            if (tasks.Count > 0)
            {
                AddToggleLabel("今天要做的 · 未完成 " + openCount + (todoCollapsed ? "  ▸" : "  ▾"),
                    delegate { todoCollapsed = !todoCollapsed; stateDirty = true; SaveState(); Render(); });
                if (!todoCollapsed)
                {
                    int so = 0;
                    foreach (var t in tasks)
                    {
                        if (t.Done) continue;
                        if (so >= 6) break;
                        so++;
                        AddTaskRow(t);
                    }
                }
            }

            var doneToday = showDone ? ReadDoneToday() : new List<string>();
            if (doneToday.Count > 0)
            {
                AddToggleLabel("今天做了 · " + doneToday.Count + " 件" + (doneCollapsed ? "  ▸" : "  ▾"),
                    delegate { doneCollapsed = !doneCollapsed; stateDirty = true; SaveState(); Render(); });
                if (!doneCollapsed)
                {
                    int shown = 0;
                    for (int i = doneToday.Count - 1; i >= 0 && shown < 5; i--, shown++)
                        AddMutedLine("✓ " + doneToday[i]);
                }
            }
            else if (showDone)
            {
                var recent = ReadTodayList();
                if (recent.Count > 0)
                {
                    AddToggleLabel("今天做了 · 最近 " + recent.Count + " 项" + (doneCollapsed ? "  ▸" : "  ▾"),
                        delegate { doneCollapsed = !doneCollapsed; stateDirty = true; SaveState(); Render(); });
                    if (!doneCollapsed)
                    {
                        for (int i = recent.Count - 1; i >= 0 && i >= recent.Count - 5; i--)
                            AddMutedLine("· " + recent[i]);
                    }
                }
            }

            // 2) 解析日报：标题 / 摘要 / 今天先问自己
            string title = "";
            string summary = "";
            var questions = new List<string>();
            string section = "";
            foreach (var raw in ReadText(DocPath).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("# ")) { title = line.Substring(2).Trim(); continue; }
                if (line.StartsWith("> ")) { if (summary.Length == 0) summary = line.Substring(2).Trim(); continue; }
                if (line.StartsWith("## ")) { section = line.Substring(3).Trim(); continue; }
                if ((section.Contains("先问自己") || section.Contains("追问")) && line.Length > 2
                    && char.IsDigit(line[0]) && line[1] == '.')
                    questions.Add(line.Substring(2).Trim());
            }

            // 3) 今天先问自己
            if (questions.Count > 0)
            {
                AddLabel("今天先问自己");
                int idx = 0;
                foreach (var q in questions)
                {
                    idx++;
                    AddNumberedRow(idx + ".", q);
                }
            }

            // 4) 昨天一行摘要
            if (summary.Length > 0)
            {
                AddLabel(string.IsNullOrEmpty(title) ? "昨天" : "昨天 · " + title);
                AddMutedLine(summary);
            }

            // 5) 没有待办也没日报时给个提示
            if (bodyPanel.Children.Count == 0)
                AddMutedLine("还没有内容，回车记一条待办吧");

            string[] wd = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            DateTime now = DateTime.Now;
            titleText.Text = now.ToString("M月d日") + " " + wd[(int)now.DayOfWeek];

            UpdateHeight();
            ReapplySnap();
            Log("rendered items=" + bodyPanel.Children.Count + " h=" + Height + " collapsed=" + collapsed + " desired=" + bodyPanel.DesiredSize.Height);
        }
        catch (Exception ex) { Log("render error: " + ex.Message); }
    }

    void UpdateHeight()
    {
        if (collapsed) { Height = 38; return; }
        bodyPanel.Measure(new Size(Math.Max(120, Width - 34), double.PositiveInfinity));
        double content = bodyPanel.DesiredSize.Height;
        double maxBody = Math.Max(160, SystemParameters.WorkArea.Height / 2 - 110);
        Height = 38 + Math.Min(content, maxBody) + 66;
        UpdateLayout();
    }

    void ReapplySnap()
    {
        var wa = SystemParameters.WorkArea;
        if (snapRight) Left = wa.Right - ActualWidth;
        if (snapLeft) Left = wa.Left;
        if (snapTop) Top = wa.Top;
        if (snapBottom) Top = wa.Bottom - ActualHeight;
        UpdateCorners();
    }

    void AddMutedLine(string text)
    {
        bodyPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        });
    }

    void AddNumberedRow(string mark, string text)
    {
        var grid = RowGrid(2);
        grid.Children.Add(Mark(mark, Accent));
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Ink,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        bodyPanel.Children.Add(grid);
    }

    void AddToggleLabel(string text, MouseButtonEventHandler onClick)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Muted,
            FontSize = 10.5,
            Margin = new Thickness(0, 12, 0, 5),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        tb.MouseLeftButtonUp += onClick;
        bodyPanel.Children.Add(tb);
    }

    void AddLabel(string text)
    {
        bodyPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Muted,
            FontSize = 10.5,
            Margin = new Thickness(0, 12, 0, 5)
        });
    }

    void AddTextRow(string text, Brush markColor, string mark)
    {
        var grid = RowGrid(2);
        grid.Children.Add(Mark(mark, markColor));
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Ink,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        bodyPanel.Children.Add(grid);
    }

    static TextBlock Mark(string text, Brush color)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = color,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    static Grid RowGrid(int cols)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (cols > 2) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    void AddTaskRow(TaskItem t)
    {
        var grid = RowGrid(3);
        grid.Children.Add(Mark(t.Done ? "✓" : "○", t.Done ? DoneB : TodoB));

        var tb = new TextBlock
        {
            Text = t.Text,
            Foreground = t.Done ? Muted : Ink,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);

        var del = new Button
        {
            Content = "×",
            Width = 20,
            Height = 18,
            FontSize = 13,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            Opacity = 0,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = "删除这条",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(del, 2);
        int line = t.Line;
        del.Click += delegate { DeleteTask(line); };
        grid.Children.Add(del);

        grid.MouseEnter += delegate { del.Opacity = 1; };
        grid.MouseLeave += delegate { del.Opacity = 0; };
        MouseButtonEventHandler dbl = delegate(object s, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleTask(line); e.Handled = true; }
        };
        grid.MouseLeftButtonDown += dbl;

        bodyPanel.Children.Add(grid);
    }

    // ---------- tasks ----------
    class TaskItem { public bool Done; public string Text; public int Line; }

    List<string> ReadTodayList()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(TodayPath)) return list;
            foreach (var raw in File.ReadAllLines(TodayPath, Utf8))
            {
                var line = raw.Trim();
                if (line.Length < 4 || line.StartsWith("#")) continue;
                list.Add(line);
            }
        }
        catch { }
        return list;
    }

    void RefreshLive()
    {
        try
        {
            if (!File.Exists(LiveScript)) return;
            var psi = new System.Diagnostics.ProcessStartInfo("python", "-X utf8 \"" + LiveScript + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log("live refresh failed: " + ex.Message); }
    }

    List<string> ReadDoneToday()
    {
        var list = new List<string>();
        if (!File.Exists(DonePath)) return list;
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var raw in File.ReadAllLines(DonePath, Utf8))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ") && line.Length > 12 && line.Substring(2).StartsWith(today))
            {
                var text = line.Substring(13).Trim();
                if (text.Length > 0) list.Add(text);
            }
        }
        return list;
    }

    void RecordDone(string text, bool done)
    {
        try
        {
            var lines = new List<string>();
            if (File.Exists(DonePath)) lines.AddRange(File.ReadAllLines(DonePath, Utf8));
            if (done)
                lines.Add("- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + text);
            else
                lines.RemoveAll(delegate(string l) { return l.TrimEnd().EndsWith(" " + text); });
            File.WriteAllLines(DonePath, lines.ToArray(), Utf8);
        }
        catch (Exception ex) { Log("done record failed: " + ex.Message); }
    }

    List<TaskItem> ReadTasks()
    {
        var list = new List<TaskItem>();
        if (!File.Exists(TasksPath)) return list;
        var lines = File.ReadAllLines(TasksPath, Utf8);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("- [x] ")) list.Add(new TaskItem { Done = true, Text = lines[i].Substring(6), Line = i });
            else if (lines[i].StartsWith("- [ ] ")) list.Add(new TaskItem { Done = false, Text = lines[i].Substring(6), Line = i });
        }
        return list;
    }

    void ToggleTask(int line)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(TasksPath, Utf8));
            if (line < 0 || line >= lines.Count) return;
            string title = lines[line].Substring(6).Trim();
            if (lines[line].StartsWith("- [ ] ")) { lines[line] = "- [x] " + title; RecordDone(title, true); }
            else if (lines[line].StartsWith("- [x] ")) { lines[line] = "- [ ] " + title; RecordDone(title, false); }
            else return;
            File.WriteAllLines(TasksPath, lines.ToArray(), Utf8);
            Log("task toggled line " + line);
            Render();
        }
        catch (Exception ex) { Log("toggle failed: " + ex.Message); }
    }

    void DeleteTask(int line)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(TasksPath, Utf8));
            if (line < 0 || line >= lines.Count) return;
            lines.RemoveAt(line);
            File.WriteAllLines(TasksPath, lines.ToArray(), Utf8);
            Log("task deleted line " + line);
            Render();
        }
        catch (Exception ex) { Log("delete failed: " + ex.Message); }
    }

    void AddTask(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) return;
        if (!File.Exists(TasksPath)) File.WriteAllText(TasksPath, "# 待办\r\n", Utf8);
        File.AppendAllText(TasksPath, "- [ ] " + DateTime.Now.ToString("HH:mm") + " " + text + "\r\n", Utf8);
        Log("task added: " + text);
    }

    // ---------- input ----------
    void OnInputKey(object s, KeyEventArgs e)
    {
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : (e.Key == Key.System ? e.SystemKey : e.Key);
        if (key == Key.Escape) { quickInput.Clear(); e.Handled = true; return; }
        if (key != Key.Return) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int pos = quickInput.CaretIndex;
            quickInput.Text = quickInput.Text.Insert(pos, "\n");
            quickInput.CaretIndex = pos + 1;
        }
        else
        {
            AddTask(quickInput.Text);
            quickInput.Clear();
            Render();
        }
        e.Handled = true;
    }

    // ---------- collapse ----------
    void SetCollapsed(bool value)
    {
        collapsed = value;
        bodyPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        inputRow.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        foldBtn.Content = value ? "+" : "–";
        UpdateHeight();
        ReapplySnap();
    }

    void OpenDoc()
    {
        try { System.Diagnostics.Process.Start("code", "\"" + DocPath + "\""); }
        catch { try { System.Diagnostics.Process.Start("notepad", "\"" + DocPath + "\""); } catch { } }
    }

    // ---------- state ----------
    void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var json = File.ReadAllText(StatePath);
            int l = GetInt(json, "Left"), t = GetInt(json, "Top");
            if (l > -10000 && t > -10000 && (l != 0 || t != 0)) { Left = l; Top = t; }
            autoHide = GetBool(json, "AutoHide", true);
            opacity = GetDouble(json, "Opacity", 1.0);
            voiceWake = GetBool(json, "VoiceWake", true);
            showPet = GetBool(json, "ShowPet", false);
            todoCollapsed = GetBool(json, "TodoCollapsed", false);
            doneCollapsed = GetBool(json, "DoneCollapsed", false);
            showDone = GetBool(json, "ShowDone", true);
        }
        catch { }
    }

    static bool GetBool(string json, string key, bool fallback)
    {
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) return fallback;
        i = json.IndexOf(':', i);
        if (i < 0) return fallback;
        int start = i + 1;
        while (start < json.Length && json[start] == ' ') start++;
        return string.Compare(json, start, "true", 0, 4, true) == 0;
    }

    static double GetDouble(string json, string key, double fallback)
    {
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) return fallback;
        i = json.IndexOf(':', i);
        if (i < 0) return fallback;
        int start = i + 1;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.')) end++;
        double v;
        return double.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
    }

    static int GetInt(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) return -99999;
        i = json.IndexOf(':', i);
        if (i < 0) return -99999;
        int start = i + 1;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        int v;
        return int.TryParse(json.Substring(start, end - start), out v) ? v : -99999;
    }

    void SaveState()
    {
        if (!stateDirty) return;
        try
        {
            var sb = new StringBuilder();
            double saveLeft = Left, saveTop = Top;
            if (hidden)
            {
                var wa = SystemParameters.WorkArea;
                if (snapRight) saveLeft = wa.Right - ActualWidth;
                if (snapLeft) saveLeft = wa.Left;
                if (snapTop) saveTop = wa.Top;
                if (snapBottom) saveTop = wa.Bottom - ActualHeight;
            }
            sb.Append("{\"Left\":").Append((int)saveLeft)
              .Append(",\"Top\":").Append((int)saveTop)
              .Append(",\"Opacity\":").Append(opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"AutoHide\":").Append(autoHide ? "true" : "false")
              .Append(",\"VoiceWake\":").Append(voiceWake ? "true" : "false")
              .Append(",\"ShowPet\":").Append(showPet ? "true" : "false")
              .Append(",\"TodoCollapsed\":").Append(todoCollapsed ? "true" : "false")
              .Append(",\"DoneCollapsed\":").Append(doneCollapsed ? "true" : "false")
              .Append(",\"ShowDone\":").Append(showDone ? "true" : "false")
              .Append("}");
            File.WriteAllText(StatePath, sb.ToString(), Utf8);
            stateDirty = false;
        }
        catch { }
    }
}

class Clock7 : FrameworkElement
{
    public string Value = "";
    static readonly int[] Patterns = new int[] { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };
    static readonly Brush On = Freeze(new SolidColorBrush(Color.FromRgb(140, 225, 175)));
    static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromArgb(28, 150, 170, 190)));

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    const double W = 10, H = 18, T = 2.0, GAP = 4.5, COLON = 4;

    double TotalWidth()
    {
        double x = 0;
        foreach (char ch in Value)
            x += ch == ':' ? COLON + GAP : W + GAP;
        return Math.Max(0, x - GAP);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(TotalWidth(), H);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0;
        foreach (char ch in Value)
        {
            if (ch == ':')
            {
                dc.DrawRectangle(On, null, new Rect(x, 4, T, T));
                dc.DrawRectangle(On, null, new Rect(x, H - 4 - T, T, T));
                x += COLON + GAP;
            }
            else if (ch >= '0' && ch <= '9')
            {
                DrawDigit(dc, x, Patterns[ch - '0']);
                x += W + GAP;
            }
            else x += GAP;
        }
    }

    static void DrawDigit(DrawingContext dc, double x, double mask)
    {
        double midY = (H - T) / 2;
        double half = (H - T) / 2 - T;
        int m = (int)mask;
        Seg(dc, x + T, 0, W - 2 * T, T, (m & 1) != 0);              // a 上
        Seg(dc, x + W - T, T, T, half, (m & 2) != 0);                // b 右上
        Seg(dc, x + W - T, (H + T) / 2, T, half, (m & 4) != 0);      // c 右下
        Seg(dc, x + T, H - T, W - 2 * T, T, (m & 8) != 0);           // d 下
        Seg(dc, x, (H + T) / 2, T, half, (m & 16) != 0);             // e 左下
        Seg(dc, x, T, T, half, (m & 32) != 0);                       // f 左上
        Seg(dc, x + T, midY, W - 2 * T, T, (m & 64) != 0);           // g 中
    }

    static void Seg(DrawingContext dc, double x, double y, double w, double h, bool on)
    {
        if (!on) return;
        dc.DrawRectangle(On, null, new Rect(x, y, w, h));
    }
}
