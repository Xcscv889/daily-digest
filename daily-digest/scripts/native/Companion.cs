using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// 陪伴层：聊天、设置、Key 管理。与 MemoWpf.cs 用 partial 合在同一个窗口类里。
partial class MemoWindow
{
    static readonly string CompanionDir = Path.Combine(Dir, "companion");
    static readonly string CompanionCfg = Path.Combine(CompanionDir, "config.json");
    static readonly string CompanionKey = Path.Combine(CompanionDir, "key.dat");
    static readonly string AvatarPath = Path.Combine(CompanionDir, "avatar.png");
    static readonly string ProactiveStatePath = Path.Combine(CompanionDir, "state.json");
    static readonly string ChatDir = Path.Combine(CompanionDir, "chat");

    const string DefaultBase = "https://api.deepseek.com/v1";
    const string DefaultModel = "deepseek-chat";
    const string DefaultName = "小臻";
    const string DefaultVoice = "zh-TW-HsiaoChenNeural";

    string personaName = DefaultName;
    string apiBase = DefaultBase;
    string apiModel = DefaultModel;
    string apiProxy = "";
    string apiVoice = DefaultVoice;
    string apiKey = "";
    string ttsExe = "";

    bool chatting, sending;
    bool collapseAfterChat;
    Grid chatOverlay;
    Border chatBackdrop;
    Button speakBtn;

    // ---- 语音播报 ----
    MediaPlayer voicePlayer;
    readonly Queue<string> speakQueue = new Queue<string>();
    bool speechOn = true;
    int speakGen;
    bool playerBusy;
    string speechFileNow;
    StackPanel chatStack;
    ScrollViewer chatScroll;
    TextBox chatInput;
    TextBlock chatStatus, chatTitle;
    Grid chatAvatarHost;
    System.Windows.Shapes.Ellipse chatStatusDot;
    Button sendBtn;
    MenuItem chatMenuItem;
    MenuItem proactiveMenu;
    Window settingsWin;

    class Turn { public string Role; public string Text; }
    class ReplyView { public TextBlock Block; public bool GotText; public string Finish; }
    readonly List<Turn> turns = new List<Turn>();

    // ---------- 初始化 ----------

    // 细滚动条：6px、无箭头、半透明圆角滑块
    const string ScrollStyleXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='ThinBar' TargetType='ScrollBar'>
    <Setter Property='Width' Value='6'/>
    <Setter Property='MinWidth' Value='6'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb MinHeight='24'>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border Background='#55FFFFFF' CornerRadius='3' Margin='1,2,1,2'/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    static Style ThinScrollStyle()
    {
        var rd = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(ScrollStyleXaml);
        return (Style)rd["ThinBar"];
    }

    void InitCompanion()
    {
        LoadCompanionConfig();
        LoadRecentTurns();
        BuildChatPanel();
        InitVoice();
        InitProactive();
        InitMemory();
        UpdateChatUi();
    }

    // 头像：companion/avatar.png 优先；没有就用内置小像
    void RefreshAvatar()
    {
        if (chatAvatarHost != null)
        {
            chatAvatarHost.Children.Clear();
            chatAvatarHost.Children.Add(AvatarVisual(30));
        }
        if (chatBtn != null)
        {
            var host = new Grid { Width = 18, Height = 18 };
            host.Children.Add(AvatarVisual(18));
            if (unreadProactive)
            {
                host.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Accent,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -1, -1, 0),
                    IsHitTestVisible = false
                });
            }
            chatBtn.Content = host;
        }
    }

    FrameworkElement AvatarVisual(double size)
    {
        try
        {
            if (File.Exists(AvatarPath))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(AvatarPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                var frame = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size * 0.26),
                    Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill }
                };
                frame.Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.26, size * 0.26);
                return frame;
            }
        }
        catch (Exception ex) { Log("avatar load failed: " + ex.Message); }
        return new MascotGlyph { Width = size, Height = size };
    }

    void BuildChatPanel()
    {
        var root = card.Child as Grid;
        if (root == null) return;

        chatOverlay = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        chatOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        chatOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chatOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 不透明底：否则聊天时下面的待办会透出来
        chatBackdrop = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            CornerRadius = card.CornerRadius
        };
        Grid.SetRowSpan(chatBackdrop, 3);
        chatOverlay.Children.Add(chatBackdrop);
        chatOverlay.Resources.Add(typeof(Button),
            FlatStyle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), new CornerRadius(8)));

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = new Button
        {
            Content = "‹", Width = 30, Height = 30, FontSize = 19, Margin = new Thickness(6, 0, 2, 0),
            Foreground = Muted, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = "返回（Esc）"
        };
        back.Click += delegate { CloseChatToWidget(); };
        head.Children.Add(back);

        var titleHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // 头像：优先用 companion/avatar.png，没有就用内置的小像
        chatAvatarHost = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        RefreshAvatar();
        titleHost.Children.Add(chatAvatarHost);

        var nameBox = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chatTitle = new TextBlock { Text = personaName, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        chatStatusDot = new System.Windows.Shapes.Ellipse
        {
            Width = 5, Height = 5, Fill = DotIdle,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
        };
        chatStatus = new TextBlock { Text = "未设置 Key", FontSize = 10.5, Foreground = Muted };
        statusRow.Children.Add(chatStatusDot);
        statusRow.Children.Add(chatStatus);
        nameBox.Children.Add(chatTitle);
        nameBox.Children.Add(statusRow);
        titleHost.Children.Add(nameBox);

        Grid.SetColumn(titleHost, 1);
        head.Children.Add(titleHost);

        speakBtn = new Button
        {
            Content = speechOn ? "🔊" : "🔇", Width = 30, Height = 30, FontSize = 13,
            Margin = new Thickness(0, 0, 2, 0), Foreground = Muted, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ToolTip = speechOn ? "关闭语音播报" : "打开语音播报"
        };
        speakBtn.Click += delegate
        {
            speechOn = !speechOn;
            speakBtn.Content = speechOn ? "🔊" : "🔇";
            speakBtn.ToolTip = speechOn ? "关闭语音播报" : "打开语音播报";
            SaveCompanionConfig();
            if (!speechOn) StopSpeech();
        };
        Grid.SetColumn(speakBtn, 2);
        head.Children.Add(speakBtn);

        var gear = new Button
        {
            Content = "⋯", Width = 30, Height = 30, FontSize = 15, Margin = new Thickness(0, 0, 6, 0),
            Foreground = Muted, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = "陪伴设置"
        };
        gear.Click += delegate { OpenSettings(); };
        Grid.SetColumn(gear, 3);
        head.Children.Add(gear);

        head.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            if (InsideButton((DependencyObject)e.OriginalSource)) return;
            try { DragMove(); } catch { }
            Snap();
            SaveState();
        };
        chatOverlay.Children.Add(head);

        chatStack = new StackPanel { Margin = new Thickness(14, 6, 14, 8) };
        chatScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = chatStack
        };
        chatScroll.Resources.Add(typeof(ScrollBar), ThinScrollStyle());
        Grid.SetRow(chatScroll, 1);
        chatOverlay.Children.Add(chatScroll);

        var inputBorder = new Border
        {
            Margin = new Thickness(12, 4, 12, 12),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Padding = new Thickness(12, 5, 5, 5)
        };
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        chatInput = new TextBox
        {
            FontSize = 13,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ink,
            CaretBrush = Ink,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MinHeight = 28,
            MaxHeight = 88,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        chatInput.PreviewKeyDown += delegate(object s, KeyEventArgs e)
        {
            Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : (e.Key == Key.System ? e.SystemKey : e.Key);
            if (key == Key.Escape)
            {
                e.Handled = true;
                CloseChatToWidget();
                return;
            }
            if (key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SendChat();
            }
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(chatInput, "ChatInput");
        inputGrid.Children.Add(chatInput);

        sendBtn = new Button
        {
            Content = "↑", Width = 28, Height = 28, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, ToolTip = "发送"
        };
        sendBtn.Click += delegate { SendChat(); };
        Grid.SetColumn(sendBtn, 1);
        inputGrid.Children.Add(sendBtn);

        inputBorder.Child = inputGrid;
        Grid.SetRow(inputBorder, 2);
        chatOverlay.Children.Add(inputBorder);

        Grid.SetRowSpan(chatOverlay, 3);
        root.Children.Add(chatOverlay);
    }

    // 代码里建模板：默认 Button 的 chrome 太 Windows，这里换成圆角纯色块
    static Style FlatStyle(Brush hover, CornerRadius radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "b";
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button));
        tpl.VisualTree = border;
        var tr = new Trigger { Property = IsMouseOverProperty, Value = true };
        tr.Setters.Add(new Setter(Border.BackgroundProperty, hover, "b"));
        tpl.Triggers.Add(tr);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return style;
    }

    // ---------- 打开 / 收起 ----------

    double ChatHeight()
    {
        return Math.Min(480, Math.Max(260, SystemParameters.WorkArea.Height - 80));
    }

    // 贴边时圆角会变，聊天层要跟着变，否则四角露出方角
    void SyncChatCorners()
    {
        if (chatBackdrop != null) chatBackdrop.CornerRadius = card.CornerRadius;
    }

    void ToggleChat()
    {
        if (chatting) { CloseChat(); return; }
        collapseAfterChat = collapsed;
        SetCollapsed(false);
        ShowWindow();
        Activate();

        chatting = true;
        chatOverlay.Visibility = Visibility.Visible;
        Height = ChatHeight();
        UpdateLayout();
        ReapplySnap();

        RenderTurns();
        if (unreadProactive)
        {
            unreadProactive = false;
            var pending = pendingProactive;
            pendingProactive = null;
            if (!string.IsNullOrEmpty(pending))
            {
                AddBubble(pending, "assistant");
                turns.Add(new Turn { Role = "assistant", Text = pending });
                SaveTurn("assistant", pending);
            }
            RefreshAvatar();
        }
        if (turns.Count == 0) Greet();

        chatInput.Focus();
        chatScroll.ScrollToEnd();
        Log("chat opened");
    }

    void CloseChat()
    {
        if (!chatting) return;
        chatting = false;
        StopSpeech();
        chatOverlay.Visibility = Visibility.Collapsed;
        UpdateHeight();
        UpdateLayout();
        ReapplySnap();
        Log("chat closed");
    }

    // 返回时恢复到打开聊天的状态：本来折叠的就一步折回去，不多按一次
    void CloseChatToWidget()
    {
        CloseChat();
        if (collapseAfterChat) SetCollapsed(true);
    }

    // 折叠小窗时顺带收起聊天，避免「折叠了但聊天还挂着」
    void HideChatForCollapse()
    {
        if (!chatting) return;
        chatting = false;
        if (chatOverlay != null) chatOverlay.Visibility = Visibility.Collapsed;
    }

    void Greet()
    {
        // 只是界面上的招呼，不进上下文，也不落盘
        AddBubble("我在呀。今天过得怎么样？", "assistant");
    }

    void RenderTurns()
    {
        chatStack.Children.Clear();
        foreach (var t in turns) AddBubble(t.Text, t.Role);
        chatScroll.ScrollToEnd();
    }

    // ---------- 消息渲染 ----------

    TextBlock AddBubble(string text, string role)
    {
        bool me = role == "user";
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = me ? Brushes.White : Ink
        };
        chatStack.Children.Add(new Border
        {
            Child = tb,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 3, 0, 3),
            MaxWidth = 264,
            Background = me ? Accent : new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            HorizontalAlignment = me ? HorizontalAlignment.Right : HorizontalAlignment.Left
        });
        chatScroll.ScrollToEnd();
        return tb;
    }

    void AddNotice(string text)
    {
        chatStack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Muted,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6)
        });
        chatScroll.ScrollToEnd();
    }

    // ---------- 发送 ----------

    void SendChat()
    {
        if (sending) return;
        var text = chatInput.Text.Trim();
        if (text.Length == 0) return;

        if (apiKey.Length == 0)
        {
            AddNotice("先在这里填一下 API Key");
            OpenSettings();
            return;
        }

        chatInput.Clear();
        StopSpeech();
        AddBubble(text, "user");
        turns.Add(new Turn { Role = "user", Text = text });
        SaveTurn("user", text);

        var view = AddBubble("正在想…", "assistant");
        view.Foreground = Muted;
        var rv = new ReplyView { Block = view };
        sending = true;
        sendBtn.IsEnabled = false;

        var url = apiBase.TrimEnd('/') + "/chat/completions";
        var body = BuildRequest();
        var key = apiKey;
        var proxy = apiProxy;
        ThreadPool.QueueUserWorkItem(delegate { StreamReply(url, key, proxy, body, rv); });
    }

    string BuildRequest()
    {
        string lastUser = "";
        for (int i = turns.Count - 1; i >= 0; i--)
            if (turns[i].Role == "user") { lastUser = turns[i].Text; break; }

        var sys = SystemPrompt() + MemoryBlock(lastUser);
        if (sys.Length > 1600) sys = sys.Substring(0, 1600);

        int start = turns.Count;
        int histChars = 0;
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            int add = turns[i].Text.Length + 6;
            if (histChars + add > 900 && start < turns.Count) break;
            histChars += add;
            start = i;
        }

        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(JsonEscape(apiModel))
          .Append("\",\"stream\":true,\"temperature\":0.9,\"max_tokens\":800,\"messages\":[");
        sb.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEscape(sys)).Append("\"}");
        for (int i = start; i < turns.Count; i++)
        {
            var t = turns[i];
            sb.Append(",{\"role\":\"").Append(t.Role)
              .Append("\",\"content\":\"").Append(JsonEscape(t.Text)).Append("\"}");
        }
        sb.Append("]}");
        Log("prompt: sys=" + sys.Length + " turns=" + histChars + " chars=" + (sys.Length + histChars));
        return sb.ToString();
    }

    void StreamReply(string url, string key, string proxy, string body, ReplyView rv)
    {
        var full = new StringBuilder();
        var spoken = new StringBuilder();
        try
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch { }

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "text/event-stream";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 90000;
            req.Proxy = ProxyFor(proxy);
            req.Headers.Add("Authorization", "Bearer " + key);

            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                var ctype = resp.ContentType ?? "";
                if (ctype.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string raw;
                    using (var sr0 = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) raw = sr0.ReadToEnd();
                    Log("chat stream: ct=" + ctype + " status=" + (int)resp.StatusCode + " not-sse bytes=" + raw.Length);
                    Fail(rv, raw.TrimStart().StartsWith("<")
                        ? "接口返回的是网页而不是数据，API 地址可能少了 /v1"
                        : ParseApiError(raw, "接口没有返回流式数据，检查 API 地址"));
                    return;
                }

                var dataLines = 0;
                var deltas = 0;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0 || !line.StartsWith("data:")) continue;
                        var payload = line.Substring(5).Trim();
                        if (payload == "[DONE]") break;
                        dataLines++;
                        var finish = ParseFinish(payload);
                        if (finish != null) rv.Finish = finish;
                        var delta = ParseDelta(payload);
                        if (string.IsNullOrEmpty(delta)) continue;
                        deltas++;
                        full.Append(delta);
                        AppendReply(rv, delta);

                        // 边收边送合成：一句一完成就排进语音队列
                        spoken.Append(delta);
                        if (speechOn)
                        {
                            int cut = LastSentenceEnd(spoken.ToString());
                            if (cut > 0)
                            {
                                var sentence = spoken.ToString(0, cut);
                                spoken.Remove(0, cut);
                                QueueSpeech(sentence, false);
                            }
                        }
                    }
                }
                Log("chat stream: ct=" + ctype + " lines=" + dataLines + " deltas=" + deltas +
                    " finish=" + rv.Finish + " chars=" + full.Length);
            }

            var reply = full.ToString();
            Dispatcher.BeginInvoke((Action)delegate
            {
                if (reply.Length == 0)
                {
                    rv.Block.Text = rv.Finish == "length" ? "（想太久被截断了，再说一次吧）" : "……";
                    rv.Block.Foreground = Muted;
                }
                else
                {
                    turns.Add(new Turn { Role = "assistant", Text = reply });
                    SaveTurn("assistant", reply);
                    if (speechOn && spoken.Length > 0)
                    {
                        QueueSpeech(spoken.ToString(), false);
                        spoken.Length = 0;
                    }
                }
                sending = false;
                sendBtn.IsEnabled = true;
            });
        }
        catch (WebException ex)
        {
            var msg = "连接失败";
            var resp = ex.Response as HttpWebResponse;
            if (resp != null)
            {
                try
                {
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        msg = ParseApiError(sr.ReadToEnd(), "HTTP " + (int)resp.StatusCode);
                }
                catch { }
            }
            else msg = ex.Message;
            Fail(rv, msg);
        }
        catch (Exception ex) { Fail(rv, ex.Message); }
    }

    void Fail(ReplyView rv, string msg)
    {
        Log("chat error: " + msg);
        Dispatcher.BeginInvoke((Action)delegate
        {
            rv.Block.Text = "（" + Shorten(msg, 160) + "）";
            rv.Block.Foreground = Muted;
            sending = false;
            sendBtn.IsEnabled = true;
        });
    }

    void AppendReply(ReplyView rv, string chunk)
    {
        Dispatcher.BeginInvoke((Action)delegate
        {
            if (!rv.GotText)
            {
                rv.GotText = true;
                rv.Block.Text = "";
                rv.Block.Foreground = Ink;
            }
            rv.Block.Text += chunk;
            chatScroll.ScrollToEnd();
        });
    }

    static string ParseFinish(string json)
    {
        return JsonValue(json, "finish_reason");
    }

    static string ParseDelta(string json)
    {
        return JsonValue(json, "content");
    }

    // 不引 System.Web.Extensions：按 key 直接扫 JSON 字符串值，字段顺序无所谓
    static string JsonValue(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var pat = "\"" + key + "\":\"";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return null;
        return JsonStringAt(json, i + pat.Length - 1);
    }

    static string JsonStringAt(string json, int i)
    {
        if (i < 0 || i >= json.Length || json[i] != '"') return null;
        i++;
        var sb = new StringBuilder();
        while (i < json.Length && json[i] != '"')
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                i++;
                char e = json[i];
                if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u' && i + 4 < json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(e);
            }
            else sb.Append(json[i]);
            i++;
        }
        return sb.ToString();
    }

    static string ParseApiError(string body, string fallback)
    {
        var msg = JsonValue(body, "message");
        return string.IsNullOrEmpty(msg) ? fallback : msg;
    }

    static IWebProxy ProxyFor(string p)
    {
        if (string.IsNullOrEmpty(p) || p == "direct") return null;
        if (p == "system") return WebRequest.GetSystemWebProxy();
        return new WebProxy(p);
    }

    string SystemPrompt()
    {
        var sb = new StringBuilder();
        sb.Append("你叫「").Append(personaName).Append("」，是用户的朋友，一个住在电脑桌面小窗里的陪伴搭子。\n");
        sb.Append("说话自然、温暖，带一点台湾女生的语气（「欸」「齁」「啦」「超～」偶尔用，不要堆砌），像发微信一样短。\n");
        sb.Append("你只关心他这个人：心情、身体、今天过得怎么样。看到他在忙可以提醒休息，但不催工作、不评价完成度、不要像助理一样列清单。\n");
        sb.Append("回复通常一到三句，不要用 Markdown 标题、列表，也不要每句都加表情。\n");
        sb.Append("今天是 ").Append(DateTime.Now.ToString("yyyy年M月d日 HH:mm")).Append("。\n");
        return sb.ToString();
    }

    // ---------- 设置 ----------

    void OpenSettings()
    {
        if (settingsWin != null && settingsWin.IsVisible)
        {
            settingsWin.Activate();
            return;
        }

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI")
        };

        var shell = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(246, 28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16)
        };
        var panel = new StackPanel();
        shell.Child = panel;

        var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = "陪伴设置", FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Ink,
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
        panel.Children.Add(head);

        var nameBox = DarkTextBox(personaName);
        var baseBox = DarkTextBox(apiBase);
        var modelBox = DarkTextBox(apiModel);
        var proxyBox = DarkTextBox(apiProxy);
        var keyBox = new PasswordBox
        {
            FontSize = 12.5,
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = Ink,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            PasswordChar = '•'
        };
        var keyHint = new TextBlock
        {
            Text = apiKey.Length > 0 ? "已保存，留空则不修改" : "还没有保存过",
            FontSize = 10.5, Foreground = Muted, Margin = new Thickness(2, 4, 0, 12)
        };

        var status = new TextBlock { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(2, 2, 0, 10), TextWrapping = TextWrapping.Wrap };

        var avatarBtn = new Button
        {
            Content = File.Exists(AvatarPath) ? "更换头像…" : "选择头像…",
            FontSize = 12, Padding = new Thickness(12, 7, 12, 7),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        avatarBtn.Click += delegate { PickAvatar(status); };

        panel.Children.Add(Field("她的头像", avatarBtn));
        panel.Children.Add(Field("她的名字", Box(nameBox)));

        var voiceRow = new StackPanel { Orientation = Orientation.Horizontal };
        var voiceBtn = new Button
        {
            Content = "声音：" + VoiceLabel(apiVoice) + "　⟳",
            FontSize = 12, Padding = new Thickness(12, 7, 12, 7),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0)
        };
        voiceBtn.Click += delegate
        {
            apiVoice = NextVoice(apiVoice);
            voiceBtn.Content = "声音：" + VoiceLabel(apiVoice) + "　⟳";
            SpeakText("你好呀，我是" + personaName + "。", true);
        };
        var previewBtn = new Button
        {
            Content = "试听",
            FontSize = 12, Padding = new Thickness(14, 7, 14, 7),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        previewBtn.Click += delegate
        {
            SpeakText("你好呀，我是" + personaName + "。今天也要好好照顾自己喔。", true);
        };
        voiceRow.Children.Add(voiceBtn);
        voiceRow.Children.Add(previewBtn);
        panel.Children.Add(Field("她的声音（点一下换）", voiceRow));
        panel.Children.Add(Field("API 地址（OpenAI 兼容）", Box(baseBox)));
        panel.Children.Add(Field("模型", Box(modelBox)));
        panel.Children.Add(Field("API Key", Box(keyBox)));
        panel.Children.Add(keyHint);
        panel.Children.Add(Field("代理（留空直连 / system=系统代理 / http://127.0.0.1:7897）", Box(proxyBox)));

        panel.Children.Add(status);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var test = new Button
        {
            Content = "测试连接", FontSize = 12, Padding = new Thickness(14, 7, 14, 7),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)), Foreground = Ink,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0)
        };
        var save = new Button
        {
            Content = "保存", FontSize = 12, Padding = new Thickness(18, 7, 18, 7),
            Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        row.Children.Add(test);
        row.Children.Add(save);
        panel.Children.Add(row);

        win.Resources.Add(typeof(Button), FlatStyle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), new CornerRadius(9)));

        test.Click += delegate
        {
            var b = NormalizeBase(baseBox.Text.Trim());
            var m = modelBox.Text.Trim();
            var k = keyBox.Password.Length > 0 ? keyBox.Password : apiKey;
            var p = proxyBox.Text.Trim();
            if (k.Length == 0) { status.Text = "先填 API Key"; return; }
            status.Text = "连接中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                int code;
                var body = "{\"model\":\"" + JsonEscape(m) + "\",\"max_tokens\":8,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                var text = HttpPostJson(b + "/chat/completions", k, p, body, out code);
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (code == 200)
                    {
                        status.Text = "✓ 连接成功";
                        status.Foreground = DoneB;
                    }
                    else
                    {
                        status.Text = "✗ " + (code > 0 ? "HTTP " + code + " · " : "") + Shorten(ParseApiError(text, text), 120);
                        status.Foreground = TodoB;
                    }
                });
            });
        };

        save.Click += delegate
        {
            personaName = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : DefaultName;
            apiBase = NormalizeBase(baseBox.Text.Trim().Length > 0 ? baseBox.Text.Trim() : DefaultBase);
            apiModel = modelBox.Text.Trim().Length > 0 ? modelBox.Text.Trim() : DefaultModel;
            apiProxy = proxyBox.Text.Trim();
            if (keyBox.Password.Length > 0)
            {
                apiKey = keyBox.Password.Trim();
                SaveKey(apiKey);
            }
            SaveCompanionConfig();
            UpdateChatUi();
            win.Close();
            Log("companion settings saved: base=" + apiBase + " model=" + apiModel);
        };

        win.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) win.Close(); };
        win.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject && InsideButton((DependencyObject)e.OriginalSource)) return;
            try { win.DragMove(); } catch { }
        };
        win.Content = shell;
        settingsWin = win;
        win.Closed += delegate { settingsWin = null; };
        win.ShowDialog();
    }

    static TextBox DarkTextBox(string text)
    {
        return new TextBox
        {
            Text = text,
            FontSize = 12.5,
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = Ink,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            CaretBrush = Ink
        };
    }

    static Border Box(Control inner)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Child = inner
        };
    }

    static StackPanel Field(string label, FrameworkElement box)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = Muted, Margin = new Thickness(2, 0, 0, 5) });
        p.Children.Add(box);
        return p;
    }

    static string Shorten(string s, int n)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // 只写了域名（https://xxx.com）时补 /v1，避免打到官网首页拿到 HTML
    static string NormalizeBase(string b)
    {
        b = (b ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return b;
        Uri u;
        if (Uri.TryCreate(b, UriKind.Absolute, out u) && (u.AbsolutePath == "" || u.AbsolutePath == "/"))
            return b + "/v1";
        return b;
    }

    static string HttpPostJson(string url, string key, string proxy, string body, out int code)
    {
        code = 0;
        try
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 20000;
            req.Proxy = ProxyFor(proxy);
            req.Headers.Add("Authorization", "Bearer " + key);
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                code = (int)resp.StatusCode;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
        }
        catch (WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp != null)
            {
                code = (int)resp.StatusCode;
                try
                {
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
                }
                catch { }
            }
            return ex.Message;
        }
        catch (Exception ex) { return ex.Message; }
    }

    void PickAvatar(TextBlock status)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择头像";
            dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif";
            if (dlg.ShowDialog() != true) return;
            Directory.CreateDirectory(CompanionDir);
            File.Copy(dlg.FileName, AvatarPath, true);
            RefreshAvatar();
            if (status != null) status.Text = "头像已更新";
        }
        catch (Exception ex)
        {
            Log("avatar pick failed: " + ex.Message);
            if (status != null) status.Text = "头像保存失败：" + ex.Message;
        }
    }

    void InitVoice()
    {
        try
        {
            voicePlayer = new MediaPlayer();
            voicePlayer.MediaEnded += delegate { PlayNextSpeech(); };
            voicePlayer.MediaFailed += delegate { PlayNextSpeech(); };
        }
        catch (Exception ex) { Log("voice init failed: " + ex.Message); }
    }

    static readonly string[] VoiceOptions = new string[]
    {
        "zh-TW-HsiaoChenNeural",  // 台湾·晓臻
        "zh-TW-HsiaoYuNeural",    // 台湾·晓雨
        "zh-CN-XiaoxiaoNeural"    // 大陆·晓晓
    };

    static string VoiceLabel(string voice)
    {
        if (voice == "zh-TW-HsiaoChenNeural") return "台湾 · 晓臻";
        if (voice == "zh-TW-HsiaoYuNeural") return "台湾 · 晓雨";
        if (voice == "zh-CN-XiaoxiaoNeural") return "大陆 · 晓晓";
        return voice;
    }

    static string NextVoice(string voice)
    {
        for (int i = 0; i < VoiceOptions.Length; i++)
            if (VoiceOptions[i] == voice) return VoiceOptions[(i + 1) % VoiceOptions.Length];
        return VoiceOptions[0];
    }

    string EdgeTtsExePath()
    {
        if (!string.IsNullOrEmpty(ttsExe) && File.Exists(ttsExe)) return ttsExe;
        var p = Path.Combine(Dir, "tts", "Scripts", "edge-tts.exe");
        return File.Exists(p) ? p : "";
    }

    // 播报一段文本：拆句后并行合成；新的播报会打断旧的
    void SpeakText(string text, bool force)
    {
        if (voicePlayer == null) return;
        if (!speechOn && !force) return;
        StopSpeech();
        QueueSpeech(text, force);
    }

    // 单句进队列：每句一个后台任务，彼此并行，合成完就播
    void QueueSpeech(string text, bool force)
    {
        if (voicePlayer == null) return;
        if (!speechOn && !force) return;
        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return;

        var voice = apiVoice;
        foreach (var s in sentences)
        {
            var sentence = s;
            int gen = speakGen;
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (gen != speakGen) return;
                string file = null;
                try { file = SynthesizeEdge(sentence, voice); }
                catch (Exception ex)
                {
                    Log("tts edge failed: " + ex.Message);
                    try { file = SynthesizeSapi(sentence); } catch (Exception ex2) { Log("tts sapi failed: " + ex2.Message); }
                }
                if (file == null) return;
                if (gen != speakGen) { TryDelete(file); return; }

                var f = file;
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (gen != speakGen) { TryDelete(f); return; }
                    speakQueue.Enqueue(f);
                    if (!playerBusy) PlayNextSpeech();
                });
            });
        }
    }

    // 流式回复里找最后一句结束的位置，边生成边送合成
    static int LastSentenceEnd(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '；' || c == ';' || c == '\n')
                return i + 1;
        }
        return 0;
    }

    void StopSpeech()
    {
        speakGen++;
        speakQueue.Clear();
        playerBusy = false;
        try
        {
            if (voicePlayer != null) { voicePlayer.Stop(); voicePlayer.Close(); }
        }
        catch { }
        if (speechFileNow != null) { TryDelete(speechFileNow); speechFileNow = null; }
    }

    void PlayNextSpeech()
    {
        if (speakQueue.Count == 0)
        {
            playerBusy = false;
            // 播完把最后一个临时文件清掉，别在 %TEMP% 里攒
            if (speechFileNow != null) { TryDelete(speechFileNow); speechFileNow = null; }
            return;
        }
        playerBusy = true;
        var f = speakQueue.Dequeue();
        if (speechFileNow != null) TryDelete(speechFileNow);
        speechFileNow = f;
        try
        {
            voicePlayer.Open(new Uri(f));
            voicePlayer.Play();
            Log("tts play: " + Path.GetFileName(f));
        }
        catch (Exception ex)
        {
            Log("tts play failed: " + ex.Message);
            TryDelete(f);
            speechFileNow = null;
            PlayNextSpeech();
        }
    }

    string SynthesizeEdge(string text, string voice)
    {
        var exe = EdgeTtsExePath();
        if (exe.Length == 0) throw new Exception("没有找到 edge-tts");

        var t0 = DateTime.Now;
        var outFile = Path.Combine(Path.GetTempPath(), "xiaozhen-" + Guid.NewGuid().ToString("N") + ".mp3");
        var clean = text.Replace("\"", "").Replace("\r", " ").Replace("\n", " ");
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            Arguments = "--voice " + voice + " --text \"" + clean + "\" --write-media \"" + outFile + "\""
        };
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                throw new Exception("合成超时");
            }
            if (p.ExitCode != 0) throw new Exception("edge-tts 退出码 " + p.ExitCode);
        }
        if (!File.Exists(outFile) || new FileInfo(outFile).Length < 400) throw new Exception("没有产出音频");
        Log("tts edge ok: " + clean.Length + " chars " + (int)(DateTime.Now - t0).TotalMilliseconds + "ms");
        return outFile;
    }

    // 兜底：系统自带语音（中文只有大陆音色）
    string SynthesizeSapi(string text)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "xiaozhen-" + Guid.NewGuid().ToString("N") + ".wav");
        var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        try
        {
            synth.SetOutputToWaveFile(outFile);
            synth.Speak(text);
        }
        finally { synth.Dispose(); }
        return outFile;
    }

    static List<string> SplitSentences(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            sb.Append(c);
            if (c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '；' || c == ';' || c == '\n')
            {
                AddSentence(list, sb.ToString());
                sb.Length = 0;
            }
        }
        AddSentence(list, sb.ToString());
        return list;
    }

    static void AddSentence(List<string> list, string s)
    {
        s = (s ?? "").Trim();
        s = s.Replace("……", "，").Replace("**", "").Replace("#", "");
        if (s.Length < 2) return;
        if (s.Length > 90) s = s.Substring(0, 90);
        list.Add(s);
    }

    static void TryDelete(string path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }

    void UpdateChatUi()
    {
        bool ready = apiKey.Length > 0;
        if (chatTitle != null) chatTitle.Text = personaName;
        if (chatStatus != null) chatStatus.Text = ready ? "在线" : "未设置 Key";
        if (chatStatusDot != null) chatStatusDot.Fill = ready ? DoneB : DotIdle;
        var tip = ready ? "模型 " + apiModel + "\n" + apiBase : "先去设置里填 API Key";
        if (chatAvatarHost != null) chatAvatarHost.ToolTip = tip;
        if (chatStatus != null) chatStatus.ToolTip = tip;
        if (chatMenuItem != null) chatMenuItem.Header = "和" + personaName + "聊聊";
        if (chatBtn != null) chatBtn.ToolTip = "和" + personaName + "聊聊（Ctrl+Alt+J）";
    }

    // ---------- 配置与 Key ----------

    void LoadCompanionConfig()
    {
        try
        {
            if (File.Exists(CompanionCfg))
            {
                var json = File.ReadAllText(CompanionCfg, Utf8);
                personaName = GetStr(json, "name", DefaultName);
                var rawBase = GetStr(json, "base_url", DefaultBase);
                apiBase = NormalizeBase(rawBase);
                apiModel = GetStr(json, "model", DefaultModel);
                apiProxy = GetStr(json, "proxy", "");
                apiVoice = GetStr(json, "voice", DefaultVoice);
                ttsExe = GetStr(json, "tts_exe", "");
                speechOn = GetBool(json, "speak", true);
                // 旧配置里只写了域名，补完 /v1 后写回，省得每次启动都修
                if (apiBase != rawBase) SaveCompanionConfig();
            }
            if (File.Exists(CompanionKey))
            {
                var b64 = File.ReadAllText(CompanionKey, Utf8).Trim();
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                apiKey = Encoding.UTF8.GetString(raw);
            }
        }
        catch (Exception ex) { Log("companion config load failed: " + ex.Message); }
    }

    void SaveCompanionConfig()
    {
        try
        {
            Directory.CreateDirectory(CompanionDir);
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"name\": \"").Append(JsonEscape(personaName)).Append("\",\r\n");
            sb.Append("  \"base_url\": \"").Append(JsonEscape(apiBase)).Append("\",\r\n");
            sb.Append("  \"model\": \"").Append(JsonEscape(apiModel)).Append("\",\r\n");
            sb.Append("  \"voice\": \"").Append(JsonEscape(apiVoice)).Append("\",\r\n");
            sb.Append("  \"speak\": ").Append(speechOn ? "true" : "false").Append(",\r\n");
            sb.Append("  \"tts_exe\": \"").Append(JsonEscape(ttsExe)).Append("\",\r\n");
            sb.Append("  \"proxy\": \"").Append(JsonEscape(apiProxy)).Append("\"\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(CompanionCfg, sb.ToString(), Utf8);
        }
        catch (Exception ex) { Log("companion config save failed: " + ex.Message); }
    }

    void SaveKey(string key)
    {
        try
        {
            Directory.CreateDirectory(CompanionDir);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(CompanionKey, Convert.ToBase64String(bytes), Utf8);
        }
        catch (Exception ex) { Log("companion key save failed: " + ex.Message); }
    }

    // ---------- 本地对话日志 ----------

    void LoadRecentTurns()
    {
        try
        {
            var path = Path.Combine(ChatDir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path, Utf8);
            int start = Math.Max(0, lines.Length - 10);
            for (int i = start; i < lines.Length; i++)
            {
                var role = GetStr(lines[i], "role", "");
                var text = GetStr(lines[i], "text", "");
                if (role.Length > 0 && text.Length > 0) turns.Add(new Turn { Role = role, Text = text });
            }
        }
        catch { }
    }

    void SaveTurn(string role, string text)
    {
        try
        {
            Directory.CreateDirectory(ChatDir);
            var path = Path.Combine(ChatDir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
            var line = "{\"ts\":\"" + DateTime.Now.ToString("HH:mm:ss") + "\",\"role\":\"" + role +
                       "\",\"text\":\"" + JsonEscape(text) + "\"}\r\n";
            File.AppendAllText(path, line, Utf8);
        }
        catch { }
    }

    // ---------- JSON 小工具 ----------

    static string JsonEscape(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    static string GetStr(string json, string key, string fallback)
    {
        if (string.IsNullOrEmpty(json)) return fallback;
        int i = json.IndexOf("\"" + key + "\"");
        if (i < 0) return fallback;
        i = json.IndexOf(':', i);
        if (i < 0) return fallback;
        i++;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length || json[i] != '"') return fallback;
        i++;
        var sb = new StringBuilder();
        while (i < json.Length && json[i] != '"')
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                i++;
                char e = json[i];
                if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u' && i + 4 < json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(e);
            }
            else sb.Append(json[i]);
            i++;
        }
        return sb.ToString();
    }

    // ---------- 主动关心（P3） ----------

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    struct RECT { public int Left, Top, Right, Bottom; }

    DispatcherTimer proactiveTimer;
    bool proactiveOn = true;
    bool unreadProactive;
    string pendingProactive;
    int activeMinutes;
    int proactiveCount;
    DateTime lastProactive = DateTime.MinValue;
    string proactiveDay = "", greetDay = "", rainDay = "", sleepDay = "";
    static int weatherKindNow = -1;

    void InitProactive()
    {
        try
        {
            if (File.Exists(ProactiveStatePath))
            {
                var json = File.ReadAllText(ProactiveStatePath, Utf8);
                proactiveOn = GetBool(json, "proactive", true);
                var lp = GetStr(json, "last_proactive", "");
                DateTime t;
                if (DateTime.TryParse(lp, out t)) lastProactive = t;
                var c = GetInt(json, "count");
                proactiveCount = c > -9999 ? c : 0;
                proactiveDay = GetStr(json, "count_day", "");
                greetDay = GetStr(json, "greet_day", "");
                rainDay = GetStr(json, "rain_day", "");
                sleepDay = GetStr(json, "sleep_day", "");
                memDoneDay = GetStr(json, "mem_done", "");
                memMergeDay = GetStr(json, "mem_merge", "");
            }
        }
        catch (Exception ex) { Log("proactive state load failed: " + ex.Message); }

        if (proactiveMenu != null) proactiveMenu.IsChecked = proactiveOn;

        proactiveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        proactiveTimer.Tick += delegate { ProactiveTick(); };
        proactiveTimer.Start();
    }

    void SetProactive(bool on)
    {
        proactiveOn = on;
        SaveProactiveState();
        Log("proactive " + (on ? "on" : "off"));
    }

    void ProactiveTick()
    {
        if (!proactiveOn) return;
        var now = DateTime.Now;
        if (QuietNow(now)) return;
        if (IsFullscreenForeground()) return;

        var today = now.ToString("yyyy-MM-dd");
        if (proactiveDay != today) { proactiveDay = today; proactiveCount = 0; }
        if (proactiveCount >= 4) return;
        if (lastProactive != DateTime.MinValue && (now - lastProactive).TotalMinutes < 90) return;

        // 人不在（2 分钟没动键鼠）就不打扰，也重置久坐计时
        if (IdleTime().TotalMinutes >= 2) { activeMinutes = 0; return; }
        activeMinutes++;

        if (activeMinutes >= 100)
        {
            activeMinutes = 0;
            TryProactive("久坐", "用户已经连续在电脑前坐了将近两个小时", now);
            return;
        }
        if (now.Hour >= 8 && now.Hour < 11 && greetDay != today)
        {
            greetDay = today;
            TryProactive("早上", "新的一天刚开始", now);
            return;
        }
        if (IsRainy() && rainDay != today && now.Hour >= 7 && now.Hour < 20)
        {
            rainDay = today;
            TryProactive("下雨", "外面正在下雨", now);
            return;
        }
        if (now.Hour == 23 && sleepDay != today)
        {
            sleepDay = today;
            TryProactive("深夜", "已经快到午夜了", now);
        }
    }

    static bool QuietNow(DateTime now)
    {
        return now.Hour < 8 || now.Hour > 23 || (now.Hour == 23 && now.Minute >= 30);
    }

    static bool IsRainy()
    {
        var k = (WeatherKind)weatherKindNow;
        return k == WeatherKind.Rain || k == WeatherKind.Drizzle || k == WeatherKind.Thunder || k == WeatherKind.Sleet;
    }

    static TimeSpan IdleTime()
    {
        try
        {
            var li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            uint ms = (uint)Environment.TickCount - li.dwTime;
            return TimeSpan.FromMilliseconds(ms);
        }
        catch { return TimeSpan.Zero; }
    }

    static bool IsFullscreenForeground()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            return r.Left <= 0 && r.Top <= 0 && r.Right >= w && r.Bottom >= h;
        }
        catch { return false; }
    }

    void TryProactive(string kind, string hint, DateTime now)
    {
        lastProactive = now;
        proactiveCount++;
        SaveProactiveState();
        Log("proactive: " + kind);

        ThreadPool.QueueUserWorkItem(delegate
        {
            string text = null;
            try { text = GenerateProactive(hint); }
            catch (Exception ex) { Log("proactive gen failed: " + ex.Message); }
            if (string.IsNullOrEmpty(text)) text = ProactiveTemplate(kind);

            Dispatcher.BeginInvoke((Action)delegate { ShowProactive(text); });
        });
    }

    string GenerateProactive(string hint)
    {
        if (apiKey.Length == 0) return null;
        var sys = "你叫「" + personaName + "」，是用户的朋友。现在你要主动找他说话。"
                + "只回一句话，20 到 40 个字，像发微信，自然一点，不要提这是提醒，不要列清单。";
        var user = "现在是 " + DateTime.Now.ToString("HH:mm") + "，理由：" + hint + "。";
        var body = "{\"model\":\"" + JsonEscape(apiModel) + "\",\"max_tokens\":600,\"temperature\":0.9,\"messages\":["
                 + "{\"role\":\"system\",\"content\":\"" + JsonEscape(sys) + "\"},"
                 + "{\"role\":\"user\",\"content\":\"" + JsonEscape(user) + "\"}]}";
        int code;
        var raw = HttpPostJson(apiBase.TrimEnd('/') + "/chat/completions", apiKey, apiProxy, body, out code);
        if (code != 200)
        {
            Log("proactive gen http " + code + ": " + Shorten(ParseApiError(raw, raw), 120));
            return null;
        }
        var text = JsonValue(raw, "content");
        if (string.IsNullOrEmpty(text))
        {
            Log("proactive gen empty: bytes=" + (raw == null ? 0 : raw.Length));
            return null;
        }
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 80) text = text.Substring(0, 80);
        return text;
    }

    static string ProactiveTemplate(string kind)
    {
        if (kind == "久坐") return "欸，坐好久了吧？起来动一下，顺便喝口水啦。";
        if (kind == "早上") return "早安呀。今天也慢慢来就好。";
        if (kind == "下雨") return "外面在下雨喔，出门记得带伞。";
        if (kind == "深夜") return "快半夜了欸，别熬太晚，早点睡好不好。";
        return "在吗？记得歇一下喔。";
    }

    void ShowProactive(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Log("proactive show: " + text);
        if (chatting)
        {
            AddBubble(text, "assistant");
            turns.Add(new Turn { Role = "assistant", Text = text });
            SaveTurn("assistant", text);
            chatScroll.ScrollToEnd();
        }
        else
        {
            pendingProactive = text;
            unreadProactive = true;
            RefreshAvatar();
        }
    }

    // 菜单里的「试一句」，不走频率限制，方便看效果
    void TestProactive()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            string text = null;
            try { text = GenerateProactive("现在想和用户说句话"); }
            catch (Exception ex) { Log("proactive test failed: " + ex.Message); }
            if (string.IsNullOrEmpty(text)) text = "欸，在忙吗？记得喝口水喔。";
            Dispatcher.BeginInvoke((Action)delegate { ShowProactive(text); });
        });
    }

    void SaveProactiveState()
    {
        try
        {
            Directory.CreateDirectory(CompanionDir);
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"proactive\": ").Append(proactiveOn ? "true" : "false").Append(",\r\n");
            sb.Append("  \"last_proactive\": \"").Append(lastProactive == DateTime.MinValue ? "" : lastProactive.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\r\n");
            sb.Append("  \"count\": ").Append(proactiveCount).Append(",\r\n");
            sb.Append("  \"count_day\": \"").Append(proactiveDay).Append("\",\r\n");
            sb.Append("  \"greet_day\": \"").Append(greetDay).Append("\",\r\n");
            sb.Append("  \"rain_day\": \"").Append(rainDay).Append("\",\r\n");
            sb.Append("  \"sleep_day\": \"").Append(sleepDay).Append("\",\r\n");
            sb.Append("  \"mem_done\": \"").Append(memDoneDay).Append("\",\r\n");
            sb.Append("  \"mem_merge\": \"").Append(memMergeDay).Append("\"\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(ProactiveStatePath, sb.ToString(), Utf8);
        }
        catch { }
    }
}

// 黑长直、大眼睛的小头像。64×64 坐标系，按控件大小缩放。
// 表情先用 Mood 区分：0 微笑、1 眨眼（P2 的情绪直接接到这里）。
class MascotGlyph : FrameworkElement
{
    public int Mood;

    static readonly Brush HairB = Frozen(0x20, 0x1C, 0x1E);
    static readonly Brush SkinB = Frozen(0xFB, 0xE3, 0xD0);
    static readonly Brush NeckB = Frozen(0xF2, 0xCD, 0xB4);
    static readonly Brush ClothB = Frozen(0x6F, 0xA8, 0xA0);
    static readonly Brush EyeB = Frozen(0x2B, 0x25, 0x30);
    static readonly Brush MouthB = Frozen(0xC7, 0x6B, 0x6B);
    static readonly Brush ShineB = new SolidColorBrush(Color.FromArgb(75, 0x7E, 0x72, 0x78));
    static readonly Brush BlushB = new SolidColorBrush(Color.FromArgb(95, 0xE8, 0x7F, 0x92));

    static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static Pen P(Brush b, double w)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(RenderSize.Width, RenderSize.Height) / 64.0;
        if (s <= 0) return;
        dc.PushTransform(new ScaleTransform(s, s));

        var frame = new RectangleGeometry(new Rect(0, 0, 64, 64), 15, 15);
        dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(0xF6, 0xC8, 0xD9), Color.FromRgb(0xC9, 0xB6, 0xF2), 55), null, frame);
        dc.PushClip(frame);

        // 衣服 + 脖子
        dc.DrawEllipse(ClothB, null, new Point(32, 70), 20, 14);
        dc.DrawRectangle(NeckB, null, new Rect(28.5, 44, 7, 12));

        // 黑长直：头顶 + 两侧长发
        dc.DrawEllipse(HairB, null, new Point(32, 24), 21, 14.5);
        dc.DrawRoundedRectangle(HairB, null, new Rect(11.5, 22, 7, 42), 3.5, 3.5);
        dc.DrawRoundedRectangle(HairB, null, new Rect(45.5, 22, 7, 42), 3.5, 3.5);
        var hl = new StreamGeometry();
        using (var c = hl.Open())
        {
            c.BeginFigure(new Point(14.2, 28), false, false);
            c.QuadraticBezierTo(new Point(13.6, 46), new Point(16, 56), true, false);
        }
        dc.DrawGeometry(null, P(ShineB, 1.6), hl);

        // 脸
        dc.DrawEllipse(SkinB, null, new Point(32, 33.5), 14.6, 14.8);

        // 齐刘海：底边平直
        var bangs = new StreamGeometry();
        using (var c = bangs.Open())
        {
            c.BeginFigure(new Point(17.4, 10.8), true, true);
            c.LineTo(new Point(46.6, 10.8), true, false);
            c.LineTo(new Point(46.6, 20.6), true, false);
            c.QuadraticBezierTo(new Point(32, 23.2), new Point(17.4, 20.6), true, false);
        }
        dc.DrawGeometry(HairB, null, bangs);

        // 大眼睛
        dc.DrawEllipse(Brushes.White, null, new Point(25.8, 34.2), 3.2, 4.4);
        dc.DrawEllipse(EyeB, null, new Point(26.0, 34.6), 2.6, 3.8);
        dc.DrawEllipse(Brushes.White, null, new Point(26.9, 32.8), 0.95, 0.95);
        if (Mood == 1)
        {
            var w = new StreamGeometry();
            using (var c = w.Open())
            {
                c.BeginFigure(new Point(35.2, 35.0), false, false);
                c.QuadraticBezierTo(new Point(38.2, 31.4), new Point(41.2, 35.0), true, false);
            }
            dc.DrawGeometry(null, P(EyeB, 1.8), w);
        }
        else
        {
            dc.DrawEllipse(Brushes.White, null, new Point(38.2, 34.2), 3.2, 4.4);
            dc.DrawEllipse(EyeB, null, new Point(38.0, 34.6), 2.6, 3.8);
            dc.DrawEllipse(Brushes.White, null, new Point(38.9, 32.8), 0.95, 0.95);
        }

        // 腮红 + 嘴
        dc.DrawEllipse(BlushB, null, new Point(22.6, 39.8), 3.0, 1.9);
        dc.DrawEllipse(BlushB, null, new Point(41.4, 39.8), 3.0, 1.9);
        var m = new StreamGeometry();
        using (var c = m.Open())
        {
            c.BeginFigure(new Point(30.6, 41.8), false, false);
            c.QuadraticBezierTo(new Point(32, 43.6), new Point(33.4, 41.8), true, false);
        }
        dc.DrawGeometry(null, P(MouthB, 1.5), m);

        dc.Pop();
    }
}
