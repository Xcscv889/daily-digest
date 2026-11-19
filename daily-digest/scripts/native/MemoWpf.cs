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
    static readonly string CareerDailyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "career-evidence", "daily");
    static readonly string SummaryPath = Path.Combine(Dir, "today-summary.txt");
    static readonly string SummaryScript = Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(typeof(MemoWindow).Assembly.Location) ?? ".") ?? ".", "today-summary.ps1");
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
        CornerRadius='18' Background='#FF1C1C1E' BorderBrush='#22FFFFFF' BorderThickness='1'>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='*'/>
      <RowDefinition Height='Auto'/>
    </Grid.RowDefinitions>
    <Grid Name='Header' Grid.Row='0' Height='44' Background='Transparent'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <Ellipse Name='StatusDot' Grid.Column='0' Width='6' Height='6' Margin='16,0,9,0' VerticalAlignment='Center' Fill='#FF48484A'/>
      <Grid Grid.Column='1'>
        <TextBlock Name='TitleText' HorizontalAlignment='Left' VerticalAlignment='Center' FontSize='13.5' FontWeight='SemiBold'
                   Foreground='#FFF5F5F7' Text='日报' TextTrimming='CharacterEllipsis' MaxWidth='150'/>
      </Grid>
      <Grid Name='ClockHost' Grid.Column='0' Grid.ColumnSpan='5' HorizontalAlignment='Center'
            VerticalAlignment='Center' IsHitTestVisible='False'/>
      <TextBlock Name='WeatherText' Grid.Column='2' VerticalAlignment='Center' FontSize='12'
                 Foreground='#FF98989F' Margin='0,0,10,0'/>
      <Button Name='RefreshBtn' Grid.Column='3' Content='⟳' Width='26' Height='26' Margin='0,0,2,0'
              Background='Transparent' BorderThickness='0' Foreground='#FF98989F' Cursor='Hand' ToolTip='刷新'/>
      <Button Name='FoldBtn' Grid.Column='4' Content='–' Width='26' Height='26' Margin='0,0,8,0'
              Background='Transparent' BorderThickness='0' Foreground='#FF98989F' Cursor='Hand' ToolTip='折叠'/>
    </Grid>
    <ScrollViewer Name='Body' Grid.Row='1' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'
                  VerticalAlignment='Stretch'>
      <StackPanel Name='BodyPanel' Margin='16,2,16,10'/>
    </ScrollViewer>
    <Grid Grid.Row='2' Name='InputRow' Margin='16,4,16,14'>
      <TextBox Name='QuickInput' MinHeight='32' MaxHeight='96' FontSize='13' Padding='10,7'
               TextWrapping='Wrap' AcceptsReturn='True' VerticalScrollBarVisibility='Auto'
               Background='Transparent' BorderThickness='0' Foreground='#FFF5F5F7' CaretBrush='#FFF5F5F7'
               VerticalContentAlignment='Center'/>
      <TextBlock Name='QuickHint' Text='记一件事，回车保存' FontSize='12.5' IsHitTestVisible='False'
                 Margin='12,0,0,0' VerticalAlignment='Center' Foreground='#FF98989F'/>
    </Grid>
  </Grid>
</Border>";

    // 右键菜单：跟小窗一致的苹果风（深灰底、12px 圆角、hairline、蓝色勾）
    const string MenuXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Foreground' Value='#FFF5F5F7'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Grid Margin='12'>
            <Border Background='#F21C1C1E' BorderBrush='#29FFFFFF' BorderThickness='1' CornerRadius='12'>
              <Border.Effect>
                <DropShadowEffect BlurRadius='22' ShadowDepth='6' Direction='270' Opacity='0.45' Color='#000000'/>
              </Border.Effect>
            </Border>
            <StackPanel IsItemsHost='True' Margin='6' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Foreground' Value='#FFF5F5F7'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Grid>
            <Border x:Name='bg' Background='Transparent' CornerRadius='7' Margin='1,1'>
              <Grid Height='26'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='24'/>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='20'/>
                </Grid.ColumnDefinitions>
                <TextBlock x:Name='check' Text='✓' FontSize='11.5' FontWeight='SemiBold' Foreground='#FF0A84FF'
                           HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
                <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='False'
                                  VerticalAlignment='Center' Margin='0,0,10,0'/>
                <TextBlock x:Name='arrow' Grid.Column='2' Text='›' FontSize='14' Foreground='#FF8E8E93'
                           HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
              </Grid>
            </Border>
            <Popup x:Name='PART_Popup' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'
                   Placement='Right' HorizontalOffset='-6' VerticalOffset='-8'
                   IsOpen='{TemplateBinding IsSubmenuOpen}'>
              <Grid Margin='8'>
                <Border Background='#F21C1C1E' BorderBrush='#29FFFFFF' BorderThickness='1' CornerRadius='12'>
                  <Border.Effect>
                    <DropShadowEffect BlurRadius='18' ShadowDepth='5' Direction='270' Opacity='0.45' Color='#000000'/>
                  </Border.Effect>
                </Border>
                <StackPanel IsItemsHost='True' Margin='6'/>
              </Grid>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='bg' Property='Background' Value='#16FFFFFF'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='check' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='Role' Value='SubmenuHeader'>
              <Setter TargetName='arrow' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='#FF6E6E73'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(245, 245, 247));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(152, 152, 159));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(10, 132, 255));
    static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(72, 72, 74));
    static readonly Brush DoneB = new SolidColorBrush(Color.FromRgb(48, 209, 88));
    static readonly Brush TodoB = new SolidColorBrush(Color.FromRgb(255, 159, 10));

    Border card;
    Grid header;
    StackPanel bodyPanel;
    ScrollViewer body;
    TextBlock titleText, quickHint, weatherText, petText;
    TextBlock clockText;
    WeatherGlyph weatherGlyph;
    TextBox quickInput;
    Grid inputRow;
    Button foldBtn, refreshBtn;
    System.Windows.Shapes.Ellipse statusDot;
    ContextMenu menu;

    DispatcherTimer refreshTimer, autoTimer;
    HwndSource source;
    DateTime stampDoc = DateTime.MinValue, stampTasks = DateTime.MinValue, stampEvidence = DateTime.MinValue;

    bool collapsed, hidden, autoHide = true, voiceWake = true, stateDirty;
    bool todoCollapsed, doneCollapsed, showDone = true, yesterdayCollapsed = true, evidenceCollapsed = true;
    System.Speech.Recognition.SpeechRecognitionEngine speech;
    DateTime hideAt = DateTime.MaxValue;
    double opacity = 1.0;
    bool snapTop, snapRight, snapBottom, snapLeft;
    bool dragging;

    public MemoWindow()
    {
        WindowStyle = WindowStyle.None;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
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
        body = (ScrollViewer)card.FindName("Body");
        bodyPanel = (StackPanel)card.FindName("BodyPanel");
        titleText = (TextBlock)card.FindName("TitleText");
        weatherText = (TextBlock)card.FindName("WeatherText");
        weatherText.Margin = new Thickness(0);
        weatherGlyph = new WeatherGlyph
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var weatherHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        header.Children.Remove(weatherText);
        weatherHost.Children.Add(weatherGlyph);
        weatherHost.Children.Add(weatherText);
        Grid.SetColumn(weatherHost, 2);
        header.Children.Add(weatherHost);
        var clockHost = (Grid)card.FindName("ClockHost");
        clockText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
            VerticalAlignment = VerticalAlignment.Center
        };
        petText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 159)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        clockHost.Children.Add(clockText);
        clockHost.Children.Add(petText);
        quickInput = (TextBox)card.FindName("QuickInput");
        inputRow = (Grid)quickInput.Parent;
        inputRow.Children.Insert(0, new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Margin = new Thickness(0, 1, 0, 1)
        });
        quickHint = (TextBlock)card.FindName("QuickHint");
        foldBtn = (Button)card.FindName("FoldBtn");
        refreshBtn = (Button)card.FindName("RefreshBtn");
        statusDot = (System.Windows.Shapes.Ellipse)card.FindName("StatusDot");

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
            var summaryBoot = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            summaryBoot.Tick += delegate { summaryBoot.Stop(); RefreshSummary(); };
            summaryBoot.Start();
            var summaryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(60) };
            summaryTimer.Tick += delegate { RefreshSummary(); };
            summaryTimer.Start();
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
            if (clockText == null) return;
            if (showPet)
            {
                petFrame = (petFrame + 1) % PetFrames.Length;
                clockText.Visibility = Visibility.Collapsed;
                petText.Text = PetFrames[petFrame];
                petText.Visibility = Visibility.Visible;
            }
            else
            {
                petText.Visibility = Visibility.Collapsed;
                clockText.Visibility = Visibility.Visible;
                clockText.Text = DateTime.Now.ToString("HH:mm");
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
            int kind = -1;
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

                // open-meteo 在国内直连超时，只能走代理；wttr.in 可直连，不依赖 VPN
                string url = "https://wttr.in/" + lat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + lon.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + "?format=j1";
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 8000;
                req.UserAgent = "DailyMemo/1.0";
                req.Proxy = null;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var mTemp = System.Text.RegularExpressions.Regex.Match(json, "\"temp_C\"\\s*:\\s*\"(-?\\d+(?:\\.\\d+)?)\"");
                    var mCode = System.Text.RegularExpressions.Regex.Match(json, "\"weatherCode\"\\s*:\\s*\"(\\d+)\"");
                    var mDesc = System.Text.RegularExpressions.Regex.Match(json, "\"weatherDesc\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]*)\"");
                    var mCloud = System.Text.RegularExpressions.Regex.Match(json, "\"cloudcover\"\\s*:\\s*\"(\\d+)\"");
                    if (mTemp.Success)
                    {
                        double temp = double.Parse(mTemp.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        int code = mCode.Success ? int.Parse(mCode.Groups[1].Value) : -1;
                        string desc = mDesc.Success ? mDesc.Groups[1].Value : "";
                        int cloud = mCloud.Success ? int.Parse(mCloud.Groups[1].Value) : -1;
                        kind = (int)WeatherIcon(code, desc, cloud);
                        text = Math.Round(temp) + "°";
                    }
                }
            }
            catch (Exception ex) { text = "天气不可用"; Log("weather error: " + ex.Message); }

            Dispatcher.BeginInvoke((Action)delegate
            {
                if (weatherText != null) weatherText.Text = text;
                if (weatherGlyph != null)
                {
                    weatherGlyph.Visibility = kind < 0 ? Visibility.Collapsed : Visibility.Visible;
                    weatherGlyph.Kind = (WeatherKind)kind;
                }
            });
        });
    }

    static WeatherKind WeatherIcon(int code, string desc, int cloud)
    {
        // 149 = Smoky haze（霾）：wttr.in 常把「晴但空气差」的天报成这个码。
        // 旧版按 haze→Fog 兜底成「云+三条杠」，晴天却显示成云团就是这个原因。
        // 16px 画不下霾层，云量低时直接给太阳（霾天太阳确实在），云量高才当雾。
        if (code == 113) return WeatherKind.Sun;
        if (code == 116) return cloud >= 0 && cloud < 15 ? WeatherKind.Sun : WeatherKind.Partly;
        if (code == 119) return cloud >= 0 && cloud < 45 ? WeatherKind.Partly : WeatherKind.Cloud;
        if (code == 122) return cloud >= 0 && cloud < 70 ? WeatherKind.Cloud : WeatherKind.Overcast;
        if (code == 149) return cloud >= 50 ? WeatherKind.Fog : WeatherKind.Sun;
        if (code == 143 || code == 248 || code == 260) return WeatherKind.Fog;
        if (code == 176 || code == 293 || code == 296 || code == 299 || code == 302 || code == 305
            || code == 308 || code == 353 || code == 356 || code == 359) return WeatherKind.Rain;
        if (code == 185 || code == 263 || code == 266 || code == 281 || code == 284) return WeatherKind.Drizzle;
        if (code == 200 || code == 386 || code == 389 || code == 392 || code == 395) return WeatherKind.Thunder;
        if (code == 179 || code == 227 || code == 230 || code == 323 || code == 326 || (code >= 329 && code <= 338)
            || code == 368 || code == 371) return WeatherKind.Snow;
        if (code == 182 || code == 311 || code == 314 || code == 317 || code == 320 || code == 350
            || code == 362 || code == 365 || code == 374 || code == 377) return WeatherKind.Sleet;
        // 未知码按英文描述兜底；霾/烟/沙尘要和雾分开，不能都画成云
        string d = (desc ?? "").ToLowerInvariant();
        if (d.Contains("thunder") || d.Contains("storm")) return WeatherKind.Thunder;
        if (d.Contains("blizzard") || d.Contains("snow") || d.Contains("ice")) return WeatherKind.Snow;
        if (d.Contains("sleet")) return WeatherKind.Sleet;
        if (d.Contains("rain") || d.Contains("shower") || d.Contains("drizzle")) return WeatherKind.Rain;
        if (d.Contains("haze") || d.Contains("smoke") || d.Contains("dust") || d.Contains("sand"))
            return cloud >= 50 ? WeatherKind.Fog : WeatherKind.Sun;
        if (d.Contains("fog") || d.Contains("mist")) return WeatherKind.Fog;
        if (d.Contains("sunny") || d.Contains("clear")) return WeatherKind.Sun;
        if (d.Contains("overcast")) return WeatherKind.Overcast;
        if (d.Contains("cloud")) return WeatherKind.Partly;
        return WeatherKind.Cloud;
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
        var skin = (ResourceDictionary)XamlReader.Parse(MenuXaml);
        menu = new ContextMenu();
        menu.Style = (Style)skin[typeof(ContextMenu)];
        menu.Resources = skin;
        menu.HasDropShadow = false;
        menu.Items.Add(Item("刷新", delegate { Render(); }));
        menu.Items.Add(Item("打开全文", delegate { OpenDoc(); }));
        var hide = new MenuItem { Header = "贴边自动隐藏", IsCheckable = true, IsChecked = autoHide };
        hide.Click += delegate { autoHide = hide.IsChecked; if (!autoHide) ShowWindow(); stateDirty = true; SaveState(); };
        menu.Items.Add(hide);

        var refreshDone = new MenuItem { Header = "刷新今天做了（AI）" };
        refreshDone.Click += delegate { RefreshSummary(); };
        menu.Items.Add(refreshDone);

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
        menu.Items.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(12, 5, 12, 5),
            Background = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255))
        });
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
        double tr = snapTop || snapRight ? 0 : 18;
        double tl = snapTop || snapLeft ? 0 : 18;
        double br = snapBottom || snapRight ? 0 : 18;
        double bl = snapBottom || snapLeft ? 0 : 18;
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
            var c = File.GetLastWriteTimeUtc(Path.Combine(CareerDailyDir, DateTime.Now.ToString("yyyy-MM-dd") + ".md"));
            if (a != stampDoc || b != stampTasks || c != stampEvidence) Render();
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
            stampEvidence = File.GetLastWriteTimeUtc(Path.Combine(CareerDailyDir, DateTime.Now.ToString("yyyy-MM-dd") + ".md"));

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

            var aiSummary = showDone ? ReadTodaySummary() : new List<string>();
            bool summaryStale = showDone && aiSummary.Count == 0
                && (IsStale(SummaryPath) || IsStale(TodayPath));
            string todayTag = DateTime.Now.ToString("yyyy-MM-dd");
            bool dotFresh = HeaderDate(SummaryPath) == todayTag || HeaderDate(TodayPath) == todayTag;
            statusDot.Fill = dotFresh ? Accent : DotIdle;
            statusDot.ToolTip = dotFresh ? "今天的记录已生成" : "今天还没有记录";
            if (aiSummary.Count > 0)
            {
                AddToggleLabel("今天做了 · " + aiSummary.Count + " 项" + (doneCollapsed ? "  ▸" : "  ▾"),
                    delegate { doneCollapsed = !doneCollapsed; stateDirty = true; SaveState(); Render(); });
                if (!doneCollapsed)
                    foreach (var line in aiSummary) AddMutedLine("· " + line);
            }

            var doneToday = (showDone && aiSummary.Count == 0) ? ReadDoneToday() : new List<string>();
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
            else if (showDone && aiSummary.Count == 0)
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

            // 1c) 今日留痕（career-evidence 每天生成的记录）
            var evidence = ReadCareerToday();
            if (evidence.Count > 0)
            {
                AddToggleLabel("今日留痕 · " + evidence.Count + " 条" + (evidenceCollapsed ? "  ▸" : "  ▾"),
                    delegate { evidenceCollapsed = !evidenceCollapsed; stateDirty = true; SaveState(); Render(); });
                if (!evidenceCollapsed)
                    foreach (var line in evidence) AddMutedLine("· " + line);
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

            // 4) 昨天一行摘要（按 current.md 标题里的日期算，不写死「昨天」）；点标题打开当日全文
            if (summary.Length > 0)
            {
                DateTime day;
                int ago = ParseTitleDate(title, out day) ? (DateTime.Today - day.Date).Days : -1;
                string head;
                if (ago <= 1) head = "昨天 · " + title;
                else if (ago > 1) head = "上次日报 · " + title + "（" + ago + " 天前）";
                else head = string.IsNullOrEmpty(title) ? "昨天" : "昨天 · " + title;
                var target = day;
                AddToggleLabel(head + "  ↗", delegate { OpenDay(target); });
                AddMutedLine(summary);
            }

            // 4b) 今天的记录还没有
            if (summaryStale)
                AddMutedLine("今天还没有记录");

            // 5) 没有待办也没日报时给个提示
            if (bodyPanel.Children.Count == 0)
                AddMutedLine("还没有内容，回车记一条待办吧");

            string[] wd = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            DateTime now = DateTime.Now;
            titleText.Text = now.ToString("M月d日") + " " + wd[(int)now.DayOfWeek];

            UpdateHeight();
            body.ScrollToTop();
            ReapplySnap();
            Log("rendered items=" + bodyPanel.Children.Count + " h=" + Height + " collapsed=" + collapsed + " desired=" + bodyPanel.DesiredSize.Height);
        }
        catch (Exception ex) { Log("render error: " + ex.Message); }
    }

    void UpdateHeight()
    {
        if (collapsed) { Height = 44; return; }
        bodyPanel.Measure(new Size(Math.Max(120, Width - 34), double.PositiveInfinity));
        double content = bodyPanel.DesiredSize.Height;
        double maxBody = Math.Max(160, SystemParameters.WorkArea.Height / 2 - 110);
        Height = 44 + Math.Min(content, maxBody) + 70;
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
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3)
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
            FontSize = 13,
            LineHeight = 20,
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
            FontSize = 11,
            Margin = new Thickness(0, 16, 0, 6),
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
            FontSize = 11,
            Margin = new Thickness(0, 16, 0, 6)
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
            FontSize = 13,
            LineHeight = 20,
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
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    static Grid RowGrid(int cols)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
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
            FontSize = 13,
            LineHeight = 20,
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

    // current.md 标题形如「11月6日 周五」，只到月和日，年份按「不能是未来」推断
    static bool ParseTitleDate(string title, out DateTime day)
    {
        day = DateTime.MinValue;
        if (string.IsNullOrEmpty(title)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(title, @"(\d{1,2})\s*月\s*(\d{1,2})\s*日");
        if (!m.Success) return false;
        int mo = int.Parse(m.Groups[1].Value), da = int.Parse(m.Groups[2].Value);
        if (mo < 1 || mo > 12 || da < 1 || da > 31) return false;
        try
        {
            var d = new DateTime(DateTime.Today.Year, mo, da);
            if (d > DateTime.Today.AddDays(1)) d = d.AddYears(-1);
            day = d;
            return true;
        }
        catch { return false; }
    }

    // 文件首行带 "# YYYY-MM-DD HH:mm"，日期不是今天就当没数据
    static string HeaderDate(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using (var sr = new StreamReader(path, Utf8))
            {
                for (int i = 0; i < 3; i++)
                {
                    var line = sr.ReadLine();
                    if (line == null) break;
                    var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"20\d{2}-\d{2}-\d{2}");
                    if (m.Success) return m.Value;
                }
            }
        }
        catch { }
        return "";
    }

    static bool IsStale(string path)
    {
        string d = HeaderDate(path);
        return d.Length > 0 && d != DateTime.Now.ToString("yyyy-MM-dd");
    }

    List<string> ReadTodayList()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(TodayPath)) return list;
            if (IsStale(TodayPath)) return list;
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

    List<string> ReadCareerToday()
    {
        var list = new List<string>();
        try
        {
            var path = Path.Combine(CareerDailyDir, DateTime.Now.ToString("yyyy-MM-dd") + ".md");
            if (!File.Exists(path)) return list;
            foreach (var raw in File.ReadAllLines(path, Utf8))
            {
                var line = raw.Trim();
                if (!line.StartsWith("- ")) continue;
                var text = line.Substring(2);
                int cut = text.IndexOf(" Tags:", StringComparison.Ordinal);
                if (cut < 0) cut = text.IndexOf(" [Record]", StringComparison.Ordinal);
                if (cut > 0) text = text.Substring(0, cut);
                text = text.TrimEnd('.', ' ');
                if (text.Length > 0) list.Add(text);
            }
        }
        catch { }
        return list;
    }

    List<string> ReadTodaySummary()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(SummaryPath)) return list;
            if (IsStale(SummaryPath)) return list;
            foreach (var raw in File.ReadAllLines(SummaryPath, Utf8))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line.StartsWith("#")) continue;
                list.Add(line);
                if (list.Count >= 4) break;
            }
        }
        catch { }
        return list;
    }

    void RefreshSummary()
    {
        try
        {
            if (!File.Exists(SummaryScript)) return;
            var psi = new System.Diagnostics.ProcessStartInfo("pwsh",
                "-NoProfile -WindowStyle Hidden -File \"" + SummaryScript + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log("summary refresh failed: " + ex.Message); }
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
        var frames = Environment.StackTrace.Replace("\r", "").Split('\n');
        Log("collapse=" + value + " at " + (frames.Length > 4 ? frames[3].Trim() + " <- " + frames[4].Trim() : "?"));
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

    // 打开某天的完整日报；文件不存在时退回 current.md
    void OpenDay(DateTime day)
    {
        string path = DocPath;
        if (day != DateTime.MinValue && day != DateTime.MaxValue)
        {
            var p = Path.Combine(Dir, "days", day.ToString("yyyy-MM-dd") + ".md");
            if (File.Exists(p)) path = p;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch
        {
            try { System.Diagnostics.Process.Start("code", "\"" + path + "\""); }
            catch { try { System.Diagnostics.Process.Start("notepad", "\"" + path + "\""); } catch { } }
        }
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
            yesterdayCollapsed = GetBool(json, "YesterdayCollapsed", true);
            evidenceCollapsed = GetBool(json, "EvidenceCollapsed", true);
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
              .Append(",\"YesterdayCollapsed\":").Append(yesterdayCollapsed ? "true" : "false")
              .Append(",\"EvidenceCollapsed\":").Append(evidenceCollapsed ? "true" : "false")
              .Append(",\"ShowDone\":").Append(showDone ? "true" : "false")
              .Append("}");
            File.WriteAllText(StatePath, sb.ToString(), Utf8);
            stateDirty = false;
        }
        catch { }
    }
}

enum WeatherKind { None, Sun, Partly, Cloud, Overcast, Fog, Rain, Drizzle, Thunder, Snow, Sleet }

class WeatherGlyph : FrameworkElement
{
    WeatherKind kind = WeatherKind.None;
    public WeatherKind Kind
    {
        get { return kind; }
        set { if (kind != value) { kind = value; InvalidateVisual(); } }
    }

    static Brush B(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush SunB = B(255, 204, 0);
    static readonly Brush CloudB = B(176, 186, 196);
    static readonly Brush CloudDark = B(134, 145, 157);
    static readonly Brush RainB = B(79, 163, 247);
    static readonly Brush SnowB = B(214, 236, 255);
    static readonly Pen SunPen = Pen(1.25, SunB);
    static readonly Pen RainPen = Pen(1.5, RainB);
    static readonly Pen SnowPen = Pen(1.25, SnowB);
    static readonly Pen FogPen = Pen(1.15, CloudDark);

    static Pen Pen(double thickness, Brush brush)
    {
        var p = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(RenderSize.Width, RenderSize.Height) / 16.0;
        if (s <= 0 || kind == WeatherKind.None) return;
        dc.PushTransform(new ScaleTransform(s, s));
        switch (kind)
        {
            case WeatherKind.Sun:
                Sun(dc, 8, 8, 3.3, 4.9, 6.6);
                break;
            case WeatherKind.Partly:
                Sun(dc, 10.7, 4.6, 2.3, 3.4, 4.5);
                Cloud(dc, 0.2, 1.2, CloudB);
                break;
            case WeatherKind.Cloud:
                Cloud(dc, 0.2, 0.35, CloudB);
                break;
            case WeatherKind.Overcast:
                Cloud(dc, 1.7, -0.1, CloudDark);
                Cloud(dc, -0.5, 1.2, CloudB);
                break;
            case WeatherKind.Fog:
                Cloud(dc, 0.2, -1.1, CloudB);
                Bar(dc, FogPen, 12.9);
                break;
            case WeatherKind.Rain:
                Cloud(dc, 0.2, -1.1, CloudB);
                for (int i = 0; i < 3; i++)
                {
                    double x = 6.0 + i * 2.3;
                    dc.DrawLine(RainPen, new Point(x, 12.4), new Point(x - 1.0, 15.4));
                }
                break;
            case WeatherKind.Drizzle:
                Cloud(dc, 0.2, -1.1, CloudB);
                Dot(dc, 5.9, 13.3, 0.75, RainB);
                Dot(dc, 8.3, 14.4, 0.75, RainB);
                Dot(dc, 10.7, 13.3, 0.75, RainB);
                break;
            case WeatherKind.Thunder:
                Cloud(dc, 0.2, -1.1, CloudB);
                Bolt(dc);
                break;
            case WeatherKind.Snow:
                Cloud(dc, 0.2, -1.1, CloudB);
                Flake(dc, 6.0, 13.5, 1.0);
                Flake(dc, 8.3, 14.4, 1.0);
                Flake(dc, 10.6, 13.5, 1.0);
                break;
            case WeatherKind.Sleet:
                Cloud(dc, 0.2, -1.1, CloudB);
                Dot(dc, 6.3, 13.9, 0.75, RainB);
                Flake(dc, 9.7, 13.6, 1.0);
                break;
        }
        dc.Pop();
    }

    static void Sun(DrawingContext dc, double cx, double cy, double r, double r1, double r2)
    {
        dc.DrawEllipse(SunB, null, new Point(cx, cy), r, r);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            dc.DrawLine(SunPen, new Point(cx + cos * r1, cy + sin * r1), new Point(cx + cos * r2, cy + sin * r2));
        }
    }

    // 底边固定在 11.3（旧版是 12.4），给下面的雨丝/雪花/雾线留出空隙，
    // 否则 16px 下会糊成「云压着三条杠」的纸杯形状
    static void Cloud(DrawingContext dc, double ox, double oy, Brush brush)
    {
        dc.DrawEllipse(brush, null, new Point(5.3 + ox, 8.8 + oy), 2.7, 2.7);
        dc.DrawEllipse(brush, null, new Point(8.4 + ox, 7.2 + oy), 3.2, 3.2);
        dc.DrawEllipse(brush, null, new Point(11.2 + ox, 8.9 + oy), 2.2, 2.2);
        dc.DrawRectangle(brush, null, new Rect(4.9 + ox, 8.3 + oy, 8.2, 3.0));
    }

    static void Bar(DrawingContext dc, Pen pen, double y)
    {
        dc.DrawLine(pen, new Point(3.0, y), new Point(13.0, y));
        dc.DrawLine(pen, new Point(4.6, y + 2.3), new Point(11.4, y + 2.3));
    }

    static void Dot(DrawingContext dc, double x, double y, double r, Brush brush)
    {
        dc.DrawEllipse(brush, null, new Point(x, y), r, r);
    }

    static void Flake(DrawingContext dc, double x, double y, double r)
    {
        dc.DrawLine(SnowPen, new Point(x - r, y - r), new Point(x + r, y + r));
        dc.DrawLine(SnowPen, new Point(x - r, y + r), new Point(x + r, y - r));
    }

    static void Bolt(DrawingContext dc)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(9.0, 11.8), true, true);
            c.LineTo(new Point(6.6, 11.8), true, false);
            c.LineTo(new Point(8.2, 13.9), true, false);
            c.LineTo(new Point(7.1, 13.9), true, false);
            c.LineTo(new Point(9.0, 15.8), true, false);
            c.LineTo(new Point(8.5, 14.2), true, false);
            c.LineTo(new Point(9.9, 14.2), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(SunB, null, geo);
    }
}

