# Daily digest desktop memo (WPF)
# Reads ~/.daily-digest/current.md, always on top, collapsible.

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DpiAware {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@
try { [DpiAware]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null } catch { }

# single instance: a second copy exits immediately
$mutex = New-Object Threading.Mutex($false, "Local\DailyDigestMemoMutex")
if (-not $mutex.WaitOne(0)) { exit }

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$root = Join-Path $env:USERPROFILE ".daily-digest"
$docPath = Join-Path $root "current.md"
$statePath = Join-Path $root "memo.json"
$logPath = Join-Path $root "memo.log"
$livePath = Join-Path $root "live.json"
$liveScript = Join-Path $PSScriptRoot "live.py"
$tasksPath = Join-Path $root "tasks.md"

function Write-Log($msg) {
    try {
        if ((Test-Path $logPath) -and ((Get-Item $logPath).Length -gt 1MB)) {
            Move-Item $logPath "$logPath.old" -Force
        }
        Add-Content -Path $logPath -Value ("{0} {1}" -f (Get-Date -Format "s"), $msg) -Encoding utf8
    } catch { }
}

$state = '{"Left":null,"Top":null,"Opacity":1.0,"Collapsed":false,"Topmost":true}' | ConvertFrom-Json
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw | ConvertFrom-Json } catch { Write-Log "bad memo.json, using defaults" }
}
function Save-State {
    @{ Left = $script:state.Left; Top = $script:state.Top; Opacity = $script:state.Opacity; AutoHide = $script:autoHide; ShowLive = $script:showLive } |
        ConvertTo-Json | Set-Content $statePath -Encoding utf8
}

$brushBg      = [Windows.Media.BrushConverter]::new().ConvertFromString("#FF0F0D13")
$brushBgHover = [Windows.Media.BrushConverter]::new().ConvertFromString("#22FFFFFF")
$brushInk     = [Windows.Media.BrushConverter]::new().ConvertFromString("#F2E9F0F7")
$brushMuted   = [Windows.Media.BrushConverter]::new().ConvertFromString("#A8B4C2D4")
$brushAccent  = [Windows.Media.BrushConverter]::new().ConvertFromString("#FF74A9D4")
$brushDone    = [Windows.Media.BrushConverter]::new().ConvertFromString("#FF79C99A")
$brushTodo    = [Windows.Media.BrushConverter]::new().ConvertFromString("#FFD2A05B")
$brushLine    = [Windows.Media.BrushConverter]::new().ConvertFromString("#26FFFFFF")

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Width="360" SizeToContent="Height" ShowInTaskbar="False" Topmost="True" ResizeMode="NoResize"
        Title="日报">
  <Border Name="Card" CornerRadius="14" Background="#FF0F0D13" BorderThickness="1"
          BorderBrush="{DynamicResource CardLine}">
    <StackPanel>
      <Grid Name="Header" Height="38" Margin="14,0,8,0" Background="Transparent">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <Ellipse Name="Dot" Grid.Column="0" Width="7" Height="7" Margin="0,0,8,0"
                 Fill="{DynamicResource AccentBrush}" VerticalAlignment="Center"/>
        <TextBlock Name="TitleText" Grid.Column="1" VerticalAlignment="Center"
                   FontSize="13" FontWeight="SemiBold" Foreground="{DynamicResource InkBrush}"
                   Text="日报" TextTrimming="CharacterEllipsis"/>
        <TextBlock Name="StatusText" Grid.Column="2" VerticalAlignment="Center" Margin="0,0,6,0"
                   FontSize="10.5" Foreground="{DynamicResource MutedBrush}" Visibility="Collapsed"/>
        <Button Name="RefreshBtn" Grid.Column="3" Content="⟳" FontSize="13" Width="26" Height="26"
                Background="Transparent" BorderThickness="0" Foreground="{DynamicResource MutedBrush}"
                Cursor="Hand" ToolTip="刷新"/>
        <Button Name="FoldBtn" Grid.Column="4" Content="–" FontSize="14" Width="26" Height="26"
                Background="Transparent" BorderThickness="0" Foreground="{DynamicResource MutedBrush}"
                Cursor="Hand" ToolTip="折叠"/>
      </Grid>
      <ScrollViewer Name="Body" MaxHeight="470" VerticalScrollBarVisibility="Hidden"
                    HorizontalScrollBarVisibility="Disabled" Margin="0,0,0,6">
        <StackPanel Name="BodyPanel" Margin="16,0,16,8">
          <TextBlock Name="EmptyText" Text="今天没有会话记录" Foreground="{DynamicResource MutedBrush}"
                     FontSize="12.5" Margin="0,4,0,10" Visibility="Collapsed"/>
        </StackPanel>
      </ScrollViewer>
      <Grid Margin="16,2,16,12">
        <TextBox Name="QuickInput" MinHeight="30" MaxHeight="96" FontSize="12.5" Padding="8,5" BorderThickness="1"
                 TextWrapping="Wrap" AcceptsReturn="True" VerticalScrollBarVisibility="Auto"
                 Background="#22FFFFFF" BorderBrush="#33FFFFFF" Foreground="{DynamicResource InkBrush}"
                 CaretBrush="{DynamicResource InkBrush}" VerticalContentAlignment="Center"/>
        <TextBlock Name="QuickHint" Text="记一件事，回车保存" FontSize="12" IsHitTestVisible="False"
                   Margin="10,0,0,0" VerticalAlignment="Center" Foreground="{DynamicResource MutedBrush}"/>
      </Grid>
    </StackPanel>
  </Border>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Parse($xaml)
$window.Resources["CardBg"] = $brushBg
$window.Resources["CardLine"] = $brushLine
$window.Resources["InkBrush"] = $brushInk
$window.Resources["MutedBrush"] = $brushMuted
$window.Resources["AccentBrush"] = $brushAccent

$card = $window.FindName("Card")
$header = $window.FindName("Header")
$titleText = $window.FindName("TitleText")
$bodyPanel = $window.FindName("BodyPanel")
$body = $window.FindName("Body")
$dot = $window.FindName("Dot")
$foldBtn = $window.FindName("FoldBtn")
$refreshBtn = $window.FindName("RefreshBtn")
$statusText = $window.FindName("StatusText")
$quickInput = $window.FindName("QuickInput")
$quickHint = $window.FindName("QuickHint")
$emptyText = $window.FindName("EmptyText")

function New-Label([string]$text) {
    $tb = New-Object Windows.Controls.TextBlock
    $tb.Text = $text
    $tb.FontSize = 10.5
    $tb.Foreground = $brushMuted
    $tb.Margin = "0,12,0,6"
    $tb.Opacity = 0.85
    return $tb
}

function New-ItemLine([string]$text, [string]$kind, [int]$index) {
    $grid = New-Object Windows.Controls.Grid
    $grid.Margin = "0,2,0,2"
    $c1 = New-Object Windows.Controls.ColumnDefinition
    $c1.Width = [Windows.GridLength]::new(18)
    $c2 = New-Object Windows.Controls.ColumnDefinition
    $c2.Width = [Windows.GridLength]::new(1, [Windows.GridUnitType]::Star)
    $grid.ColumnDefinitions.Add($c1) | Out-Null
    $grid.ColumnDefinitions.Add($c2) | Out-Null

    $mark = New-Object Windows.Controls.TextBlock
    $mark.Text = if ($kind -eq "done") { "✓" } elseif ($kind -eq "todo") { "○" } elseif ($kind -eq "ask") { "$index." } else { "·" }
    $mark.FontSize = 12
    $mark.Foreground = if ($kind -eq "done") { $brushDone } elseif ($kind -eq "todo") { $brushTodo } elseif ($kind -eq "ask") { $brushAccent } else { $brushMuted }
    $mark.VerticalAlignment = "Top"
    [Windows.Controls.Grid]::SetColumn($mark, 0) | Out-Null
    $grid.Children.Add($mark) | Out-Null

    $tb = New-Object Windows.Controls.TextBlock
    $tb.Text = $text
    $tb.TextWrapping = "Wrap"
    $tb.FontSize = 12.5
    $tb.LineHeight = 19
    $tb.Foreground = $brushInk
    [Windows.Controls.Grid]::SetColumn($tb, 1) | Out-Null
    $grid.Children.Add($tb) | Out-Null

    return $grid
}

function Read-Tasks {
    if (-not (Test-Path $tasksPath)) { return @() }
    $out = @()
    $lines = @(Get-Content $tasksPath -Encoding utf8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^- \[( |x)\]\s+(.+)$') {
            $out += @{ Done = ($Matches[1] -eq "x"); Text = $Matches[2].Trim(); Line = $i }
        }
    }
    return $out
}

function New-TaskRow([string]$text, [bool]$done, [int]$lineNo) {
    $grid = New-Object Windows.Controls.Grid
    $grid.Margin = "0,2,0,2"
    $grid.Background = "Transparent"
    $c1 = New-Object Windows.Controls.ColumnDefinition
    $c1.Width = [Windows.GridLength]::new(18)
    $c2 = New-Object Windows.Controls.ColumnDefinition
    $c2.Width = [Windows.GridLength]::new(1, [Windows.GridUnitType]::Star)
    $c3 = New-Object Windows.Controls.ColumnDefinition
    $c3.Width = [Windows.GridLength]::Auto
    [void]$grid.ColumnDefinitions.Add($c1)
    [void]$grid.ColumnDefinitions.Add($c2)
    [void]$grid.ColumnDefinitions.Add($c3)

    $mark = New-Object Windows.Controls.TextBlock
    $mark.Text = if ($done) { "✓" } else { "○" }
    $mark.FontSize = 12
    $mark.Foreground = if ($done) { $brushDone } else { $brushTodo }
    $mark.VerticalAlignment = "Top"
    [Windows.Controls.Grid]::SetColumn($mark, 0)
    [void]$grid.Children.Add($mark)

    $tb = New-Object Windows.Controls.TextBlock
    $tb.Text = $text
    $tb.TextWrapping = "Wrap"
    $tb.FontSize = 12.5
    $tb.LineHeight = 19
    $tb.Foreground = $brushInk
    $tb.Opacity = if ($done) { 0.55 } else { 1.0 }
    [Windows.Controls.Grid]::SetColumn($tb, 1)
    [void]$grid.Children.Add($tb)

    $del = New-Object Windows.Controls.Button
    $del.Content = "×"
    $del.FontSize = 14
    $del.Width = 20
    $del.Height = 18
    $del.Margin = "6,0,0,0"
    $del.Padding = "0"
    $del.VerticalAlignment = "Top"
    $del.Background = "Transparent"
    $del.BorderThickness = 0
    $del.Foreground = $brushMuted
    $del.Cursor = "Hand"
    $del.Opacity = 0.0
    $del.ToolTip = "删除这条"
    [Windows.Controls.Grid]::SetColumn($del, 2)
    [void]$grid.Children.Add($del)

    $del.Add_Click(({ param($s2, $e2) Delete-Task $lineNo }).GetNewClosure())
    $grid.Add_MouseEnter(({ param($s2, $e2) $del.Opacity = 1.0 }).GetNewClosure())
    $grid.Add_MouseLeave(({ param($s2, $e2) $del.Opacity = 0.0 }).GetNewClosure())
    $grid.Add_MouseLeftButtonDown(({ param($s2, $e2) if ($e2.ClickCount -eq 2) { Toggle-Task $lineNo } }).GetNewClosure())

    return $grid
}

function Delete-Task([int]$lineNo) {
    try {
        $lines = New-Object System.Collections.ArrayList
        foreach ($l in @(Get-Content $tasksPath -Encoding utf8)) { [void]$lines.Add($l) }
        if ($lineNo -lt 0 -or $lineNo -ge $lines.Count) { return }
        if ($lines[$lineNo] -notmatch '^- \[[ x]\] ') { return }
        $lines.RemoveAt($lineNo)
        Set-Content $tasksPath $lines -Encoding utf8
        Write-Log ("task deleted line " + $lineNo)
        Render-Document
    } catch { Write-Log ("delete failed: " + $_.Exception.Message) }
}

function Toggle-Task([int]$lineNo) {
    try {
        $lines = @(Get-Content $tasksPath -Encoding utf8)
        if ($lineNo -lt 0 -or $lineNo -ge $lines.Count) { return }
        $line = $lines[$lineNo]
        if ($line -match '^- \[ \] ') { $lines[$lineNo] = $line -replace '^- \[ \] ', '- [x] ' }
        elseif ($line -match '^- \[x\] ') { $lines[$lineNo] = $line -replace '^- \[x\] ', '- [ ] ' }
        else { return }
        Set-Content $tasksPath $lines -Encoding utf8
        Write-Log ("task toggled line " + $lineNo)
        Render-Document
    } catch { Write-Log ("toggle failed: " + $_.Exception.Message) }
}

function Add-Task([string]$text) {
    $text = ($text -replace "`r?`n", " ").Trim()
    if (-not $text) { return }
    if (-not (Test-Path $tasksPath)) { Set-Content $tasksPath "# 待办" -Encoding utf8 }
    Add-Content $tasksPath ("- [ ] {0} {1}" -f (Get-Date -Format "HH:mm"), $text) -Encoding utf8
    Write-Log ("task added: " + $text)
}

function Render-Document {
    $bodyPanel.Children.Clear() | Out-Null
    $bodyPanel.Children.Add($emptyText) | Out-Null

    if (-not (Test-Path $docPath)) {
        $emptyText.Visibility = "Visible"
        $emptyText.Text = "还没有日报，跑一次 /daily-digest"
        $titleText.Text = (Get-Date -Format "M月d日")
        $dot.Fill = $brushMuted
        return
    }

    $lines = [IO.File]::ReadAllText($docPath, [Text.Encoding]::UTF8) -split "`r?`n"
    $title = ""
    $sections = New-Object System.Collections.ArrayList
    $current = $null
    foreach ($line in $lines) {
        $text = $line.Trim()
        if ($text -match '^#\s+(.+)$') { $title = $Matches[1]; continue }
        if ($text -match '^##\s+(.+)$') {
            $current = @{ Label = $Matches[1]; Items = (New-Object System.Collections.ArrayList) }
            [void]$sections.Add($current)
            continue
        }
        $kind = $null; $body = $null
        if ($text -match '^- \[x\]\s+(.+)$') { $kind = "done"; $body = $Matches[1] }
        elseif ($text -match '^- \[ \]\s+(.+)$') { $kind = "todo"; $body = $Matches[1] }
        elseif ($text -match '^\d+\.\s+(.+)$') { $kind = "ask"; $body = $Matches[1] }
        elseif ($text -match '^-\s+(.+)$') { $kind = "note"; $body = $Matches[1] }
        if (-not $kind) { continue }
        if ($null -eq $current) {
            $current = @{ Label = ""; Items = (New-Object System.Collections.ArrayList) }
            [void]$sections.Add($current)
        }
        [void]$current.Items.Add(@{ Kind = $kind; Text = $body })
    }

    $hasContent = $false

    # 我的待办（手动记录的，独立于日报）
    $myTasks = Read-Tasks
    if ($myTasks.Count -gt 0) {
        $openTasks = @($myTasks | Where-Object { -not $_.Done })
        $doneTasks = @($myTasks | Where-Object { $_.Done })
        if ($openTasks.Count -gt 0 -or $doneTasks.Count -gt 0) {
            $bodyPanel.Children.Add((New-Label ("我的待办 · 未完成 " + $openTasks.Count + "（双击勾掉）"))) | Out-Null
            foreach ($task in ($openTasks | Select-Object -First 6)) {
                $bodyPanel.Children.Add((New-TaskRow $task.Text $false $task.Line)) | Out-Null
            }
            foreach ($task in ($doneTasks | Select-Object -Last 2)) {
                $bodyPanel.Children.Add((New-TaskRow $task.Text $true $task.Line)) | Out-Null
            }
            $hasContent = $true
        }
    }

    # 今天实时块（日报还没生成时尤其有用）
    $live = $null
    if (Test-Path $livePath) { try { $live = Get-Content $livePath -Raw -Encoding utf8 | ConvertFrom-Json } catch { } }
    $titleIsToday = $title -and $title.StartsWith((Get-Date -Format "M月d日"))
    if ($script:showLive -and $live -and -not $titleIsToday -and $live.total_minutes -gt 0) {
        $hours = [math]::Floor($live.total_minutes / 60)
        $mins = $live.total_minutes % 60
        $span = if ($hours -gt 0) { "$hours 小时 $mins 分" } else { "$mins 分" }
        $bodyPanel.Children.Add((New-Label ("今天 · 实时 " + $live.updated + " · 共 " + $span))) | Out-Null

        foreach ($line in @($live.recent)) {
            if (-not $line) { continue }
            $short = if ($line.Length -gt 36) { $line.Substring(0, 36) + "…" } else { $line }
            $bodyPanel.Children.Add((New-ItemLine $short "note")) | Out-Null
        }
        $hasContent = $true
    }

    foreach ($section in $sections) {
        if ($section.Items.Count -eq 0) { continue }
        if ($section.Label) { $bodyPanel.Children.Add((New-Label $section.Label)) | Out-Null }
        $askIndex = 0
        foreach ($item in $section.Items) {
            if ($item.Kind -eq "ask") { $askIndex++ }
            $bodyPanel.Children.Add((New-ItemLine $item.Text $item.Kind $askIndex)) | Out-Null
            $hasContent = $true
        }
    }

    if ($title) {
        $todayLabel = (Get-Date -Format "M月d日")
        $titleText.Text = if ($title.StartsWith($todayLabel)) { $title } else { "昨天 · " + $title }
    } else {
        $titleText.Text = (Get-Date -Format "M月d日")
    }
    $emptyText.Visibility = if ($hasContent) { "Collapsed" } else { "Visible" }
    $emptyText.Text = "没有可显示的日报"

    $items = ($sections | ForEach-Object { $_.Items.Count } | Measure-Object -Sum).Sum
    if ($hasContent -and $items -le 1) {
        $note = New-Object Windows.Controls.TextBlock
        $note.Text = "今天的日报明早 08:00 生成；想现在看就说 /daily-digest"
        $note.FontSize = 11.5
        $note.TextWrapping = "Wrap"
        $note.Margin = "0,10,0,2"
        $note.Foreground = $brushMuted
        $bodyPanel.Children.Add($note) | Out-Null
    }
    $dot.Fill = if ($hasContent) { $brushAccent } else { $brushMuted }
    $sa = (Get-Item $docPath -ErrorAction SilentlyContinue).LastWriteTimeUtc
    $sb = (Get-Item $tasksPath -ErrorAction SilentlyContinue).LastWriteTimeUtc
    $script:lastStamp = if ($sa -and $sb) { if ($sa -gt $sb) { $sa } else { $sb } } elseif ($sa) { $sa } else { $sb }
    Reapply-SnapLater
    Write-Log "rendered $docPath (items=$([int]$hasContent))"
}

function Apply-State {
    $window.Opacity = [double]$script:state.Opacity
    $window.Topmost = $true
    if ($null -ne $script:state.Left -and $null -ne $script:state.Top) {
        $window.Left = [double]$script:state.Left
        $window.Top = [double]$script:state.Top
    }
    Set-Collapsed $false
}

$script:collapsed = $false

function Set-Collapsed([bool]$collapsed) {
    $script:collapsed = $collapsed
    $body.Visibility = if ($collapsed) { "Collapsed" } else { "Visible" }
    $foldBtn.Content = if ($collapsed) { "+" } else { "–" }
    Reapply-SnapLater
}

$script:statusTimer = New-Object Windows.Threading.DispatcherTimer
$script:statusTimer.Interval = [TimeSpan]::FromSeconds(2.5)
$script:statusTimer.Add_Tick({ $statusText.Visibility = "Collapsed"; $script:statusTimer.Stop() })

$script:snap = @{ Left = $false; Right = $false; Top = $false; Bottom = $false }
$script:snapThreshold = 24

function Update-Corners {
    $tl = 14; $tr = 14; $br = 14; $bl = 14
    if ($script:snap.Top)    { $tl = 0; $tr = 0 }
    if ($script:snap.Bottom) { $bl = 0; $br = 0 }
    if ($script:snap.Left)   { $tl = 0; $bl = 0 }
    if ($script:snap.Right)  { $tr = 0; $br = 0 }
    try { $card.CornerRadius = New-Object Windows.CornerRadius($tl, $tr, $br, $bl) } catch { }
}

function Invoke-Snap([double]$threshold) {
    $window.UpdateLayout()
    $wa = [Windows.SystemParameters]::WorkArea
    $w = $window.ActualWidth
    $h = $window.ActualHeight
    if ($w -le 0 -or $h -le 0) { return }

    $left = $window.Left
    $top = $window.Top

    $script:snap.Left = [math]::Abs($left - $wa.Left) -lt $threshold
    $script:snap.Right = [math]::Abs(($left + $w) - $wa.Right) -lt $threshold
    $script:snap.Top = [math]::Abs($top - $wa.Top) -lt $threshold
    $script:snap.Bottom = [math]::Abs(($top + $h) - $wa.Bottom) -lt $threshold

    if ($script:snap.Left) { $left = $wa.Left }
    if ($script:snap.Right) { $left = $wa.Right - $w }
    if ($script:snap.Top) { $top = $wa.Top }
    if ($script:snap.Bottom) { $top = $wa.Bottom - $h }

    # 别把窗口推出工作区
    $left = [math]::Max($wa.Left, [math]::Min($left, $wa.Right - $w))
    $top = [math]::Max($wa.Top, [math]::Min($top, $wa.Bottom - $h))

    if ($left -ne $window.Left) { $window.Left = $left }
    if ($top -ne $window.Top) { $window.Top = $top }
    Update-Corners
}

$script:snapTick = New-Object Windows.Threading.DispatcherTimer
$script:snapTick.Interval = [TimeSpan]::FromMilliseconds(120)
$script:snapTick.Add_Tick({ $script:snapTick.Stop(); Reapply-Snap })

function Reapply-SnapLater {
    if ($null -eq $script:snapTick) { return }
    if ($script:snap.Left -or $script:snap.Right -or $script:snap.Top -or $script:snap.Bottom) { $script:snapTick.Start() }
}

function Reapply-Snap {
    if ($script:hidden) { return }
    if ($window.Width -ne 360) { $window.Width = 360 }
    if (-not ($script:snap.Left -or $script:snap.Right -or $script:snap.Top -or $script:snap.Bottom)) { return }
    try { $window.UpdateLayout() } catch { Write-Log ("layout error: " + $_.Exception.Message); return }
    $wa = [Windows.SystemParameters]::WorkArea
    if ($script:snap.Right) { $window.Left = $wa.Right - $window.ActualWidth }
    if ($script:snap.Left) { $window.Left = $wa.Left }
    if ($script:snap.Bottom) { $window.Top = $wa.Bottom - $window.ActualHeight }
    if ($script:snap.Top) { $window.Top = $wa.Top }
    Update-Corners
}

# ---- interactions ----
function Test-InsideButton($node) {
    $guard = 0
    while ($null -ne $node -and $guard -lt 20) {
        if ($node -is [Windows.Controls.Button]) { return $true }
        if ($node -is [Windows.Media.Visual] -or $node -is [Windows.Media.Media3D.Visual3D]) {
            $node = [Windows.Media.VisualTreeHelper]::GetParent($node)
        } else {
            $node = $null
        }
        $guard++
    }
    return $false
}

$header.Add_MouseLeftButtonDown({
    if (Test-InsideButton $args[1].OriginalSource) { return }
    Stop-Animation
    if ($window.WindowState -ne "Normal") { $window.WindowState = "Normal"; $window.Width = 360 }
    $window.DragMove()
    if ($window.WindowState -ne "Normal") { $window.WindowState = "Normal"; $window.Width = 360 }
    Invoke-Snap $script:snapThreshold
    Save-State
})
$header.Add_MouseRightButtonUp({ $menu.IsOpen = $true })

$refreshBtn.Add_Click({
    try { Render-Document } catch { Write-Log ("render error: " + $_.Exception.Message) }
    $statusText.Text = "已刷新 " + (Get-Date -Format "HH:mm:ss")
    $statusText.Visibility = "Visible"
    $script:statusTimer.Stop()
    $script:statusTimer.Start()
})
$foldBtn.Add_Click({
    try {
        Set-Collapsed (-not $script:collapsed)
        Save-State
    } catch { Write-Log ("fold error: " + $_.Exception.Message) }
})
$window.Add_Closed({ Save-State })

$quickInput.Add_TextChanged({
    try {
        $lines = [math]::Max(1, $quickInput.LineCount)
        $quickInput.Height = [math]::Min(96, [math]::Max(30, 10 + 20 * $lines))
    } catch { }
})
$quickInput.Add_GotFocus({ $quickHint.Visibility = "Collapsed" })
$quickInput.Add_LostFocus({ if (-not $quickInput.Text) { $quickHint.Visibility = "Visible" } })
$quickInput.Add_PreviewKeyDown({
    param($sender, $e)
    # 中文输入法下按键是 ImeProcessed，需要取真实键
    $key = $e.Key
    if ($key -eq [Windows.Input.Key]::ImeProcessed) { $key = $e.ImeProcessedKey }
    elseif ($key -eq [Windows.Input.Key]::System) { $key = $e.SystemKey }

    if ($key -eq [Windows.Input.Key]::Escape) {
        $quickInput.Clear()
        $e.Handled = $true
        return
    }
    if ($key -ne [Windows.Input.Key]::Return) { return }
    $ctrl = ([Windows.Input.Keyboard]::Modifiers -band [Windows.Input.ModifierKeys]::Control) -ne 0
    if ($ctrl) {
        $pos = $quickInput.CaretIndex
        $quickInput.Text = $quickInput.Text.Insert($pos, "`n")
        $quickInput.CaretIndex = $pos + 1
        $e.Handled = $true
        return
    }
    try {
        Add-Task $quickInput.Text
        $quickInput.Clear()
        Render-Document
    } catch { Write-Log ("task save failed: " + $_.Exception.Message) }
    $e.Handled = $true
})

# ---- context menu ----
$menu = New-Object Windows.Controls.ContextMenu
function Add-MenuItem([string]$header, [scriptblock]$action) {
    $mi = New-Object Windows.Controls.MenuItem
    $mi.Header = $header
    $mi.Add_Click($action)
    $menu.Items.Add($mi) | Out-Null
}
Add-MenuItem "刷新" { Render-Document }
Add-MenuItem "打开全文" {
    try { Start-Process "code" $docPath } catch { Start-Process "notepad" $docPath }
}
Add-MenuItem "显示今日进度" {
    $script:showLive = -not $script:showLive
    Render-Document
    Save-State
}
Add-MenuItem "贴边自动隐藏" {
    $script:autoHide = -not $script:autoHide
    if (-not $script:autoHide) { Show-Memo }
    Save-State
}
foreach ($pct in @(1.0, 0.92, 0.8, 0.6)) {
    $label = "透明度 {0}%" -f [int]($pct * 100)
    $value = $pct
    Add-MenuItem $label ([scriptblock]::Create("`$script:state.Opacity = $value; `$window.Opacity = $value; Save-State"))
}
$menu.Items.Add((New-Object Windows.Controls.Separator)) | Out-Null
Add-MenuItem "退出" { Save-State; $window.Close() }
$window.ContextMenu = $menu

# ---- live refresh: poll mtime (PowerShell event handlers do not run while ShowDialog blocks) ----
$script:lastStamp = [datetime]::MinValue
$script:timer = New-Object Windows.Threading.DispatcherTimer
$script:timer.Interval = [TimeSpan]::FromSeconds(2)
$script:timer.Add_Tick({
    try {
        $stampA = (Get-Item $docPath -ErrorAction SilentlyContinue).LastWriteTimeUtc
        $stampB = (Get-Item $tasksPath -ErrorAction SilentlyContinue).LastWriteTimeUtc
        $stamp = if ($stampA -and $stampB) { if ($stampA -gt $stampB) { $stampA } else { $stampB } } elseif ($stampA) { $stampA } else { $stampB }
        if ($stamp -and $stamp -ne $script:lastStamp) {
            try { Render-Document } catch { Write-Log ("render error: " + $_.Exception.Message) }
        }
    } catch { Write-Log ("timer error: " + $_.Exception.Message) }
})
$script:timer.Start()

$script:liveTimer = New-Object Windows.Threading.DispatcherTimer
$script:liveTimer.Interval = [TimeSpan]::FromSeconds(60)
function Update-Live {
    if (-not (Test-Path $liveScript)) { return }
    try { Start-Process -FilePath "python" -ArgumentList @("-X", "utf8", "`"$liveScript`"") -WindowStyle Hidden -ErrorAction Stop | Out-Null }
    catch { Write-Log ("live scan failed: " + $_.Exception.Message) }
}
$script:liveRenderTick = New-Object Windows.Threading.DispatcherTimer
$script:liveRenderTick.Interval = [TimeSpan]::FromMilliseconds(1500)
$script:liveRenderTick.Add_Tick({
    $script:liveRenderTick.Stop()
    try { Render-Document } catch { Write-Log ("render error: " + $_.Exception.Message) }
})

$script:liveTimer.Add_Tick({ Update-Live; $script:liveRenderTick.Start() })
$script:liveTimer.Start()

$window.Add_Loaded({
    if ($null -eq $script:state.Left) {
        $work = [Windows.SystemParameters]::WorkArea
        $window.Left = $work.Right - $window.ActualWidth
        $window.Top = $work.Top
    }
    Invoke-Snap $script:snapThreshold
    Write-Log ("position: {0},{1} size {2}x{3}" -f $window.Left, $window.Top, $window.ActualWidth, $window.ActualHeight)
})

# ---- 150ms ease-out animation ----
$script:animTimer = New-Object Windows.Threading.DispatcherTimer
$script:animTimer.Interval = [TimeSpan]::FromMilliseconds(16)
$script:anim = $null

function Stop-Animation {
    $script:animTimer.Stop()
    $script:anim = $null
}

function Animate-To([double]$toLeft, [double]$toTop, [int]$durationMs = 150) {
    Stop-Animation
    $script:anim = @{
        FromL    = [double]$window.Left
        FromT    = [double]$window.Top
        ToL      = $toLeft
        ToT      = $toTop
        Start    = Get-Date
        Duration = [double][math]::Max($durationMs, 1)
    }
    $script:animTimer.Start()
}

$script:animTimer.Add_Tick({
    if ($null -eq $script:anim) { $script:animTimer.Stop(); return }
    $progress = ((Get-Date) - $script:anim.Start).TotalMilliseconds / $script:anim.Duration
    if ($progress -ge 1) { $progress = 1 }
    $eased = 1 - [math]::Pow(1 - $progress, 3)
    $window.Left = $script:anim.FromL + ($script:anim.ToL - $script:anim.FromL) * $eased
    $window.Top = $script:anim.FromT + ($script:anim.ToT - $script:anim.FromT) * $eased
    if ($progress -ge 1) { Stop-Animation }
})

# ---- edge auto-hide ----
Add-Type @"
using System;using System.Runtime.InteropServices;
public class KeyState {
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
}
"@
Add-Type @"
using System;using System.Runtime.InteropServices;
public class HotKeyApi {
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
}
"@
Add-Type @"
using System;using System.Runtime.InteropServices;
public class CurPos { [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p); public struct PT { public int X; public int Y; } }
"@

$script:autoHide = if ($null -ne $script:state.AutoHide) { [bool]$script:state.AutoHide } else { $false }
$script:showLive = if ($null -ne $script:state.ShowLive) { [bool]$script:state.ShowLive } else { $true }
$script:hidden = $false
$script:hideAt = $null

function Get-DockTarget {
    $wa = [Windows.SystemParameters]::WorkArea
    $left = $window.Left
    $top = $window.Top
    if ($script:snap.Right)  { $left = $wa.Right - $window.ActualWidth }
    if ($script:snap.Left)   { $left = $wa.Left }
    if ($script:snap.Bottom) { $top = $wa.Bottom - $window.ActualHeight }
    if ($script:snap.Top)    { $top = $wa.Top }
    return @{ Left = $left; Top = $top }
}

function Show-Memo {
    if (-not $script:hidden) { return }
    $t = Get-DockTarget
    Animate-To $t.Left $t.Top
    $script:hidden = $false
    Reapply-SnapLater
}

function Hide-Memo {
    if (-not ($script:snap.Top -or $script:snap.Bottom -or $script:snap.Left -or $script:snap.Right)) { return }
    $peek = 8
    $wa = [Windows.SystemParameters]::WorkArea
    $left = $window.Left
    $top = $window.Top
    if ($script:snap.Top)    { $top = $wa.Top - $window.ActualHeight + $peek }
    if ($script:snap.Bottom) { $top = $wa.Bottom - $peek }
    if ($script:snap.Left)   { $left = $wa.Left - $window.ActualWidth + $peek }
    if ($script:snap.Right)  { $left = $wa.Right - $peek }
    Animate-To $left $top
    $script:hidden = $true
}

$script:autoTimer = New-Object Windows.Threading.DispatcherTimer
$script:autoTimer.Interval = [TimeSpan]::FromMilliseconds(250)
# 热键单独用一个快轮询（60ms），自动隐藏仍用 250ms
$script:hotPrev = $false
$script:hkTimer = New-Object Windows.Threading.DispatcherTimer
$script:hkTimer.Interval = [TimeSpan]::FromMilliseconds(60)
$script:hkTimer.Add_Tick({
    try {
        $nDown = ([KeyState]::GetAsyncKeyState(0x4E) -band 0x8000) -ne 0
        if ($nDown -and -not $script:hotPrev) {
            $ctrl = ([KeyState]::GetAsyncKeyState(0x11) -band 0x8000) -ne 0
            $alt = ([KeyState]::GetAsyncKeyState(0x12) -band 0x8000) -ne 0
            if ($ctrl -and $alt) {
                Set-Collapsed $false
                Show-Memo
                try {
                    $hwnd2 = (New-Object Windows.Interop.WindowInteropHelper($window)).Handle
                    $fgPid = [uint32]0
                    $fgThread = [KeyState]::GetWindowThreadProcessId([KeyState]::GetForegroundWindow(), [ref]$fgPid)
                    $me = [KeyState]::GetCurrentThreadId()
                    [KeyState]::AttachThreadInput($me, $fgThread, $true) | Out-Null
                    [KeyState]::SetForegroundWindow($hwnd2) | Out-Null
                    [KeyState]::AttachThreadInput($me, $fgThread, $false) | Out-Null
                } catch { }
                $window.Activate()
                $quickInput.Focus() | Out-Null
            }
        }
        $script:hotPrev = $nDown
    } catch { Write-Log ("hotkey poll error: " + $_.Exception.Message) }
})
$script:hkTimer.Start()

$script:autoTimer.Add_Tick({
    if (-not $script:autoHide) { return }
    # 启动时窗口尺寸可能还是 0，贴边标记没建立；这里自愈一次
    if (-not ($script:snap.Top -or $script:snap.Bottom -or $script:snap.Left -or $script:snap.Right)) {
        Invoke-Snap $script:snapThreshold
        return
    }
    if ($script:menu.IsOpen) { return }

    try {
        $pt = New-Object CurPos+PT
        [CurPos]::GetCursorPos([ref]$pt) | Out-Null
        $scale = [Windows.Media.VisualTreeHelper]::GetDpi($window).DpiScaleX
        if ($scale -le 0) { $scale = 1 }
        $cx = $pt.X / $scale
        $cy = $pt.Y / $scale

        $l = $window.Left; $t = $window.Top
        $r = $l + $window.ActualWidth; $b = $t + $window.ActualHeight
        $pad = 10
        $inside = ($cx -ge ($l - $pad)) -and ($cx -le ($r + $pad)) -and ($cy -ge ($t - $pad)) -and ($cy -le ($b + $pad))

        if ($inside) {
            $script:hideAt = $null
            if ($script:hidden) { Write-Log "show: cursor inside"; Show-Memo }
        } elseif ($null -eq $script:hideAt) {
            $script:hideAt = Get-Date
        } elseif (((Get-Date) - $script:hideAt).TotalMilliseconds -gt 1600) {
            if (-not $script:hidden) { Write-Log ("hide: cursor $([int]$cx),$([int]$cy) win $([int]$l),$([int]$t)-$([int]$r),$([int]$b)"); Hide-Memo }
        }
    } catch { Write-Log ("autohide error: " + $_.Exception.Message) }
})
$script:autoTimer.Start()

# ---- start ----
Apply-State
Update-Live
Start-Sleep -Milliseconds 400
Render-Document
Write-Log "memo started"
$window.ShowDialog() | Out-Null
