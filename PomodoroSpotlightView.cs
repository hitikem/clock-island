using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PomodoroIsland;

/// <summary>
/// 「超级展开」聚光卡：点击岛体后，宿主让这张卡片从岛体位置带倾角飞入、居中放大，
/// 点卡片外区域或按 Esc 收起。
///
/// 卡片是这个插件的**完整控制台**：
///   · 番茄钟 / 倒计时 两个页签，随时切换
///   · 拖动滑块直接改本段剩余时间
///   · 开始 / 暂停 / 完成一个 / 跳过 / 重置本段
///   · 今日完成、专注总时长、本轮进度、最近记录
///   · 快捷改时长
///
/// 要点：
///   1. 必须是**独立于岛视图**的另一棵可视树（每个窗口一棵树，共用会白屏）；
///   2. 不需要实现 IMorphView：飞入飞回、遮罩、圆角、层级全由宿主负责；
///   3. 卡片高度受宿主限制（工作区 92%），所以外面套一层 ScrollViewer，宁可滚动不要被裁；
///   4. 配色跟着岛体主题走 —— 白字在浅色卡片上同样看不见。
/// </summary>
public sealed class PomodoroSpotlightView : UserControl
{
    private readonly IIslandTheme _theme;
    private readonly Action<TimerMode> _setMode;
    private PomodoroEngine _engine;
    private CountdownTimer _countdown;

    private readonly Button _tabPomodoro;
    private readonly Button _tabCountdown;
    private readonly StackPanel _pomodoroPanel;
    private readonly StackPanel _countdownPanel;

    // 番茄钟面板
    private readonly TextBlock _phaseName;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _summaryText;
    private readonly HourglassView _ring;
    private readonly TextBlock _ringPercent;
    private readonly TextBlock _todayDone;
    private readonly TextBlock _todayFocus;
    private readonly TextBlock _cycleText;
    private readonly TextBlock _recentHeader;
    private readonly StackPanel _recentList;
    private readonly Button _primaryButton;
    private readonly Slider _remainSlider;
    private readonly TextBlock _remainLabel;
    private readonly TextBlock _focusMinutesText;
    private readonly TextBlock _breakMinutesText;
    private readonly Slider _focusSlider;
    private readonly Slider _breakSlider;

    // 倒计时面板
    private readonly TextBlock _cdTime;
    private readonly TextBlock _cdSummary;
    private readonly HourglassView _cdRing;
    private readonly TextBlock _cdRingPercent;
    private readonly Slider _cdSlider;
    private readonly Slider _cdMinutesSlider;
    private readonly TextBlock _cdSliderLabel;
    private readonly Button _cdPrimary;
    private readonly TextBlock _cdMinutesText;
    private readonly TextBox _cdLabelBox;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _chipFillBrush = new();
    private readonly SolidColorBrush _chipBorderBrush = new();
    private readonly SolidColorBrush _cardFillBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _surfaceBrush = new();   // 卡片自己的白色底
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _accentFillBrush = new();

    // 页签画刷：构造时绑好、之后只改颜色。
    // 不能每次刷新都往 Button.Resources 里写 —— 元素进可视树后 ResourceDictionary.Insert
    // 会抛 COMException（实测踩到过），所以资源只在构造阶段设置一次。
    private readonly SolidColorBrush _tabPomodoroFill = new();
    private readonly SolidColorBrush _tabPomodoroHover = new();
    private readonly SolidColorBrush _tabPomodoroText = new();
    private readonly SolidColorBrush _tabCountdownFill = new();
    private readonly SolidColorBrush _tabCountdownHover = new();
    private readonly SolidColorBrush _tabCountdownText = new();

    // 三张统计卡各自的配色（值 / 底 / 描边）
    private readonly StatVisual _statDone = new();
    private readonly StatVisual _statFocus = new();
    private readonly StatVisual _statCycle = new();

    private Windows.UI.Color _accentColor = Windows.UI.Color.FromArgb(255, 255, 107, 99);

    private string _recentSignature = "";
    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private bool _isCountdown;
    private bool _themeHooked;
    private bool _updatingSlider;
    private bool _draggingPomodoro;
    private bool _draggingCountdown;

    public PomodoroSpotlightView(
        PluginManifest manifest,
        IIslandTheme theme,
        PomodoroEngine engine,
        CountdownTimer countdown,
        Action<TimerMode> setMode)
    {
        _theme = theme;
        _engine = engine;
        _countdown = countdown;
        _setMode = setMode;
        FontFamily = PomodoroUi.UiFont;      // 圆角字体：子元素自动继承
        ApplyThemeColors();

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        var hint = new TextBlock
        {
            Text = "点卡片外区域或按 Esc 收起",
            FontSize = 12,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 4),
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(title);
        titleRow.Children.Add(hint);

        // ---- 页签 ----
        _tabPomodoro = PomodoroUi.Chip("番茄钟", _chipFillBrush, _mutedBrush, _chipBorderBrush, 13);
        _tabPomodoro.Padding = new Thickness(18, 6, 18, 6);
        _tabPomodoro.Click += (_, _) => _setMode(TimerMode.Pomodoro);

        _tabCountdown = PomodoroUi.Chip("倒计时", _chipFillBrush, _mutedBrush, _chipBorderBrush, 13);
        _tabCountdown.Padding = new Thickness(18, 6, 18, 6);
        _tabCountdown.Click += (_, _) => _setMode(TimerMode.Countdown);

        // 页签配色只在构造阶段绑一次（之后只改颜色，避免运行期写 ResourceDictionary 抛异常）
        WireTabStyles(_tabPomodoro, _tabPomodoroFill, _tabPomodoroHover, _tabPomodoroText);
        WireTabStyles(_tabCountdown, _tabCountdownFill, _tabCountdownHover, _tabCountdownText);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tabs.Children.Add(_tabPomodoro);
        tabs.Children.Add(_tabCountdown);

        // ---- 番茄钟面板 ----
        // 主角换成一个**会漏沙的大沙漏**（比进度环更直观，也跟参照图呼应）
        _ring = new HourglassView
        {
            BoxWidth = 78,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _phaseName = new TextBlock { Text = "专注中", FontSize = 16, Foreground = _mutedBrush };

        _countdownText = new TextBlock
        {
            Text = "25:00",
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            FontFamily = PomodoroUi.NumberFont,
            Foreground = _textBrush,      // 白卡上：近黑大字
        };

        _summaryText = new TextBlock
        {
            Text = "还没开始",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _mutedBrush,
        };

        var clockColumn = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        clockColumn.Children.Add(_phaseName);
        clockColumn.Children.Add(_countdownText);
        clockColumn.Children.Add(_summaryText);

        // 沙漏下面挂一个百分比（用主色，卡片里颜色就丰富起来了）
        _ringPercent = new TextBlock
        {
            Text = "0%",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            FontFamily = PomodoroUi.NumberFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = _accentBrush,
        };

        var ringStack = new StackPanel
        {
            Spacing = 4,
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ringStack.Children.Add(_ring);
        ringStack.Children.Add(_ringPercent);

        var mainGrid = new Grid { ColumnSpacing = 24 };
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(ringStack, 0);
        Grid.SetColumn(clockColumn, 1);
        mainGrid.Children.Add(ringStack);
        mainGrid.Children.Add(clockColumn);

        // 拖动滑块 = 直接改本段剩余时间（卡片上就能调，不用等）
        _remainLabel = new TextBlock
        {
            Text = "本段剩余 25:00",
            FontSize = 13,
            Foreground = _mutedBrush,
        };

        _remainSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1500,
            Value = 1500,
            StepFrequency = 30,
            Margin = new Thickness(0, 2, 0, 0),
        };
        WireSlider(_remainSlider, () => _draggingPomodoro = true, () => _draggingPomodoro = false,
            value => _engine.SetRemaining(TimeSpan.FromSeconds(value)));

        _todayDone = StatValue(_statDone);
        _todayFocus = StatValue(_statFocus);
        _cycleText = StatValue(_statCycle);
        var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        statsRow.Children.Add(StatCard("今日完成", _todayDone, _statDone));
        statsRow.Children.Add(StatCard("今日专注", _todayFocus, _statFocus));
        statsRow.Children.Add(StatCard("本轮进度", _cycleText, _statCycle));

        _recentHeader = new TextBlock
        {
            Text = "最近记录",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        _recentList = new StackPanel { Spacing = 4 };

        _focusMinutesText = new TextBlock
        {
            Text = "25",
            FontSize = 14,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };
        _breakMinutesText = new TextBlock
        {
            Text = "5",
            FontSize = 14,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        // 调时长一律用滑块（± 号留着做微调）：指针操作，卡片不抢焦点也能用
        _focusSlider = MakeSlider(1, 120, 1, _engine.FocusMinutes);
        _focusSlider.ValueChanged += (_, e) =>
        {
            if (_updatingSlider) return;
            SetPomodoroMinutes(PomodoroEngine.KeyFocusMin, (int)Math.Round(e.NewValue));
        };

        _breakSlider = MakeSlider(1, 60, 1, _engine.ShortBreakMinutes);
        _breakSlider.ValueChanged += (_, e) =>
        {
            if (_updatingSlider) return;
            SetPomodoroMinutes(PomodoroEngine.KeyShortBreakMin, (int)Math.Round(e.NewValue));
        };

        var quickRow = new StackPanel { Spacing = 8 };
        quickRow.Children.Add(SliderRow(
            "专注时长", _focusSlider, _focusMinutesText,
            () => SetPomodoroMinutes(PomodoroEngine.KeyFocusMin, _engine.FocusMinutes - 5),
            () => SetPomodoroMinutes(PomodoroEngine.KeyFocusMin, _engine.FocusMinutes + 5)));
        quickRow.Children.Add(SliderRow(
            "休息时长", _breakSlider, _breakMinutesText,
            () => SetPomodoroMinutes(PomodoroEngine.KeyShortBreakMin, _engine.ShortBreakMinutes - 1),
            () => SetPomodoroMinutes(PomodoroEngine.KeyShortBreakMin, _engine.ShortBreakMinutes + 1)));

        _primaryButton = PomodoroUi.Chip("开始", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        _primaryButton.Padding = new Thickness(18, 6, 18, 6);
        _primaryButton.Click += (_, _) => _engine.Toggle();

        var completeButton = PomodoroUi.Chip("完成一个", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        completeButton.Padding = new Thickness(18, 6, 18, 6);
        completeButton.Click += (_, _) => _engine.CompleteOne();

        var skipButton = PomodoroUi.Chip("跳过", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        skipButton.Padding = new Thickness(18, 6, 18, 6);
        skipButton.Click += (_, _) => _engine.Skip();

        var resetButton = PomodoroUi.Chip("重置本段", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        resetButton.Padding = new Thickness(18, 6, 18, 6);
        resetButton.Click += (_, _) => _engine.Reset();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_primaryButton);
        buttons.Children.Add(completeButton);
        buttons.Children.Add(skipButton);
        buttons.Children.Add(resetButton);

        _pomodoroPanel = new StackPanel { Spacing = 12 };
        _pomodoroPanel.Children.Add(mainGrid);
        _pomodoroPanel.Children.Add(_remainLabel);
        _pomodoroPanel.Children.Add(_remainSlider);
        _pomodoroPanel.Children.Add(PomodoroUi.Divider(_dividerBrush));
        _pomodoroPanel.Children.Add(statsRow);
        _pomodoroPanel.Children.Add(PomodoroUi.Divider(_dividerBrush));
        _pomodoroPanel.Children.Add(_recentHeader);
        _pomodoroPanel.Children.Add(_recentList);
        _pomodoroPanel.Children.Add(PomodoroUi.Divider(_dividerBrush));
        _pomodoroPanel.Children.Add(quickRow);
        _pomodoroPanel.Children.Add(buttons);

        // ---- 倒计时面板 ----
        _cdRing = new HourglassView
        {
            BoxWidth = 78,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _cdTime = new TextBlock
        {
            Text = "10:00",
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            FontFamily = PomodoroUi.NumberFont,
            Foreground = _textBrush,
        };

        _cdSummary = new TextBlock
        {
            Text = "随便设个时长，到点提醒我",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _mutedBrush,
        };

        var cdColumn = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        cdColumn.Children.Add(new TextBlock { Text = "倒计时", FontSize = 16, Foreground = _mutedBrush });
        cdColumn.Children.Add(_cdTime);
        cdColumn.Children.Add(_cdSummary);

        _cdRingPercent = new TextBlock
        {
            Text = "0%",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            FontFamily = PomodoroUi.NumberFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = _accentBrush,
        };

        var cdRingStack = new StackPanel
        {
            Spacing = 4,
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cdRingStack.Children.Add(_cdRing);
        cdRingStack.Children.Add(_cdRingPercent);

        var cdGrid = new Grid { ColumnSpacing = 24 };
        cdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(cdRingStack, 0);
        Grid.SetColumn(cdColumn, 1);
        cdGrid.Children.Add(cdRingStack);
        cdGrid.Children.Add(cdColumn);

        _cdSliderLabel = new TextBlock { Text = "剩余 10:00", FontSize = 13, Foreground = _mutedBrush };
        _cdSlider = new Slider
        {
            Minimum = 0,
            Maximum = 600,
            Value = 600,
            StepFrequency = 30,
            Margin = new Thickness(0, 2, 0, 0),
        };
        WireSlider(_cdSlider, () => _draggingCountdown = true, () => _draggingCountdown = false,
            value => _countdown.SetRemaining(TimeSpan.FromSeconds(value)));

        // 聚光卡窗口不抢键盘焦点（宿主的刻意设计），所以卡片里**不放任何需要打字的东西**：
        // 时长用「− / +」按钮 + 预设，备注用点选标签。要自定义备注去设置页（那儿是正常窗口）。
        _cdMinutesText = new TextBlock
        {
            Text = "10 分钟",
            FontSize = 16,
            MinWidth = 74,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        // 倒计时时长：滑块为主，预设 + ± 辅助
        _cdMinutesSlider = MakeSlider(1, 120, 1, _countdown.Minutes);
        _cdMinutesSlider.ValueChanged += (_, e) =>
        {
            if (_updatingSlider) return;
            _countdown.SetMinutes((int)Math.Round(e.NewValue));
            RefreshCountdown();
        };

        var minutesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        minutesRow.Children.Add(Label("设定时长"));
        minutesRow.Children.Add(StepButton("−", () => NudgeCountdown(-1)));
        minutesRow.Children.Add(_cdMinutesSlider);
        minutesRow.Children.Add(StepButton("+", () => NudgeCountdown(1)));
        minutesRow.Children.Add(_cdMinutesText);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        presets.Children.Add(Label("常用"));
        foreach (var minutes in new[] { 1, 3, 5, 10, 15, 25, 45 })
        {
            var preset = PomodoroUi.Chip($"{minutes} 分", _chipFillBrush, _textBrush, _chipBorderBrush, 12);
            var value = minutes;
            preset.Click += (_, _) =>
            {
                _countdown.SetMinutes(value);
                RefreshCountdown();
            };
            presets.Children.Add(preset);
        }

        // 备注：这次倒计时是干什么用的（「去煮面」），点一下就用，会显示在岛上
        var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        labels.Children.Add(Label("备注"));
        foreach (var text in new[] { "去煮面", "泡茶", "开会", "休息一下", "清空" })
        {
            var chip = PomodoroUi.Chip(text, _chipFillBrush, _textBrush, _chipBorderBrush, 12);
            var value = text == "清空" ? string.Empty : text;
            chip.Click += (_, _) =>
            {
                _countdown.SetLabel(value);
                RefreshCountdown();
            };
            labels.Children.Add(chip);
        }

        _cdPrimary = PomodoroUi.Chip("开始", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        _cdPrimary.Padding = new Thickness(18, 6, 18, 6);
        _cdPrimary.Click += (_, _) =>
        {
            _countdown.Toggle();
            RefreshCountdown();
        };

        var cdReset = PomodoroUi.Chip("重置", _chipFillBrush, _textBrush, _chipBorderBrush, 13);
        cdReset.Padding = new Thickness(18, 6, 18, 6);
        cdReset.Click += (_, _) => _countdown.Reset();

        var cdButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cdButtons.Children.Add(_cdPrimary);
        cdButtons.Children.Add(cdReset);

        _countdownPanel = new StackPanel { Spacing = 12 };
        _countdownPanel.Children.Add(cdGrid);
        _countdownPanel.Children.Add(_cdSliderLabel);
        _countdownPanel.Children.Add(_cdSlider);
        _countdownPanel.Children.Add(PomodoroUi.Divider(_dividerBrush));
        _countdownPanel.Children.Add(presets);
        _countdownPanel.Children.Add(minutesRow);
        // 自定义备注：卡片窗口平时不抢焦点，点这个框时插件主动抢一次，键盘输入才进得来
        _cdLabelBox = new TextBox
        {
            Text = _countdown.Label,
            PlaceholderText = "自定义备注，例如：去煮面",
            FontSize = 13,
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        StyleInput(_cdLabelBox);
        _cdLabelBox.LostFocus += (_, _) => CommitLabel();
        _cdLabelBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitLabel();
        };
        _cdLabelBox.AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ActivateCardWindow()),
            handledEventsToo: true);
        _cdLabelBox.GotFocus += (_, _) => ActivateCardWindow();

        var labelStack = new StackPanel { Spacing = 6 };
        labelStack.Children.Add(labels);
        labelStack.Children.Add(_cdLabelBox);
        labelStack.Children.Add(new TextBlock
        {
            Text = "点标签一键设置；想自己打字就点上面的输入框（插件会临时把键盘焦点给卡片）。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _faintBrush,
        });
        _countdownPanel.Children.Add(labelStack);
        _countdownPanel.Children.Add(cdButtons);

        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(titleRow);
        root.Children.Add(tabs);
        root.Children.Add(PomodoroUi.Divider(_dividerBrush));
        root.Children.Add(_pomodoroPanel);
        root.Children.Add(_countdownPanel);

        // 卡片自己铺一层近白底 + 圆角：看起来就是一张白色卡片，不跟随岛体的深色外观
        Content = new Border
        {
            Background = _surfaceBrush,
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(6),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(30, 22, 30, 22),
                Content = root,
            },
        };

        Loaded += (_, _) =>
        {
            HookTheme();
            ApplyThemeColors();
            Refresh();
        };
        Unloaded += (_, _) => UnhookTheme();
    }

    /// <summary>插件重新启用时会新建引擎，这里把视图指过去。</summary>
    public void Attach(PomodoroEngine engine, CountdownTimer countdown)
    {
        _engine = engine;
        _countdown = countdown;
    }

    /// <summary>页签由外部模式驱动；面板内容自己从引擎 / 倒计时读，切页签不会有一帧错数据。</summary>
    public void Apply(IslandViewModel vm) => Apply(vm.Mode == TimerMode.Countdown);

    private void Apply(bool isCountdown)
    {
        _isCountdown = isCountdown;
        Refresh();
    }

    private void Refresh()
    {
        // 卡片没打开时不用刷（插件每秒都会调 Apply，只在可视树里时才干活）
        if (!IsLoaded) return;

        UpdateAccent();   // 切页签后主色要跟着换（否则倒计时还是番茄红）

        _pomodoroPanel.Visibility = _isCountdown ? Visibility.Collapsed : Visibility.Visible;
        _countdownPanel.Visibility = _isCountdown ? Visibility.Visible : Visibility.Collapsed;

        StyleTab(_tabPomodoro, _tabPomodoroFill, _tabPomodoroHover, _tabPomodoroText, selected: !_isCountdown);
        StyleTab(_tabCountdown, _tabCountdownFill, _tabCountdownHover, _tabCountdownText, selected: _isCountdown);

        RefreshPomodoro();
        RefreshCountdown();
    }

    private void RefreshPomodoro()
    {
        var snapshot = _engine.Snapshot();
        _phase = snapshot.Phase;

        _phaseName.Text = snapshot.PhaseName;
        _countdownText.Text = snapshot.TimeText;
        _summaryText.Text = snapshot.SummaryText;
        _primaryButton.Content = snapshot.PrimaryButtonText;
        _ring.SetState(snapshot.ElapsedRatio, snapshot.IsRunning);
        _ringPercent.Text = $"{(int)Math.Round(snapshot.ElapsedRatio * 100)}%";

        _todayDone.Text = $"{snapshot.CompletedToday} 个";
        _todayFocus.Text = $"{snapshot.FocusMinutesToday} 分钟";
        _cycleText.Text = $"{Math.Min(snapshot.FocusesInCycle, snapshot.LongBreakEvery)} / {snapshot.LongBreakEvery}";

        _focusMinutesText.Text = _engine.FocusMinutes.ToString();
        _breakMinutesText.Text = _engine.ShortBreakMinutes.ToString();

        RefreshRecent(snapshot);

        var totalSeconds = Math.Max(1, snapshot.Total.TotalSeconds);
        _updatingSlider = true;
        try
        {
            // 滑块上限跟着设置走（万一设置里的值超过常规范围，也不会把滑块顶死在边界）
            if (_focusSlider.Maximum < _engine.FocusMinutes) _focusSlider.Maximum = _engine.FocusMinutes;
            if (_breakSlider.Maximum < _engine.ShortBreakMinutes) _breakSlider.Maximum = _engine.ShortBreakMinutes;
            if (Math.Abs(_focusSlider.Value - _engine.FocusMinutes) > 0.5) _focusSlider.Value = _engine.FocusMinutes;
            if (Math.Abs(_breakSlider.Value - _engine.ShortBreakMinutes) > 0.5) _breakSlider.Value = _engine.ShortBreakMinutes;

            _remainSlider.Maximum = totalSeconds;
            _remainSlider.StepFrequency = Math.Max(1, Math.Round(totalSeconds / 60));
            if (!_draggingPomodoro) _remainSlider.Value = Math.Clamp(snapshot.Remaining.TotalSeconds, 0, totalSeconds);
        }
        finally
        {
            _updatingSlider = false;
        }

        _remainLabel.Text = $"本段剩余 {PomodoroSnapshot.Format(snapshot.Remaining)}（拖滑块直接改）";
    }

    private void RefreshCountdown()
    {
        var total = _countdown.Total;
        var totalSeconds = Math.Max(1, total.TotalSeconds);

        _cdTime.Text = _countdown.TimeText;
        _cdRing.SetState(_countdown.ElapsedRatio, _countdown.IsRunning);
        _cdRingPercent.Text = $"{(int)Math.Round(_countdown.ElapsedRatio * 100)}%";
        _cdPrimary.Content = _countdown.IsRunning ? "暂停" : "开始";

        _cdSummary.Text = _countdown.IsFinished
            ? "倒计时结束 · 点「开始」重新计时"
            : string.IsNullOrWhiteSpace(_countdown.Label)
                ? $"设定 {_countdown.Minutes} 分钟 · 剩 {PomodoroSnapshot.Format(_countdown.Remaining)}"
                : $"「{_countdown.Label}」· 设定 {_countdown.Minutes} 分钟 · 剩 {PomodoroSnapshot.Format(_countdown.Remaining)}";

        // 备注不用回填：卡片里是点选标签，当前值在 summary 那行已经写着
        _cdMinutesText.Text = $"{_countdown.Minutes} 分钟";

        // 回填备注框，但别把用户正在输入的内容冲掉
        if (_cdLabelBox.FocusState == FocusState.Unfocused &&
            !string.Equals(_cdLabelBox.Text, _countdown.Label, StringComparison.Ordinal))
        {
            _cdLabelBox.Text = _countdown.Label;
        }

        _cdSliderLabel.Text = $"剩余 {PomodoroSnapshot.Format(_countdown.Remaining)}（拖滑块直接改）";

        _updatingSlider = true;
        try
        {
            if (_cdMinutesSlider.Maximum < _countdown.Minutes) _cdMinutesSlider.Maximum = _countdown.Minutes;
            if (Math.Abs(_cdMinutesSlider.Value - _countdown.Minutes) > 0.5) _cdMinutesSlider.Value = _countdown.Minutes;

            _cdSlider.Maximum = totalSeconds;
            _cdSlider.StepFrequency = Math.Max(1, Math.Round(totalSeconds / 60));
            if (!_draggingCountdown) _cdSlider.Value = Math.Clamp(_countdown.Remaining.TotalSeconds, 0, totalSeconds);
        }
        finally
        {
            _updatingSlider = false;
        }
    }

    private void RefreshRecent(PomodoroSnapshot snapshot)
    {
        var signature = string.Join('|', snapshot.Recent.Select(r => $"{r.FinishedAt:HH:mm}:{r.Minutes}:{r.Completed}"));
        if (string.Equals(signature, _recentSignature, StringComparison.Ordinal)) return;
        _recentSignature = signature;

        _recentList.Children.Clear();

        if (snapshot.Recent.Count == 0)
        {
            _recentList.Children.Add(new TextBlock
            {
                Text = "今天还没有完成的番茄。点「开始」，走完一段就会记在这里。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _faintBrush,
            });
            return;
        }

        foreach (var record in snapshot.Recent.Take(5))
        {
            _recentList.Children.Add(new TextBlock
            {
                Text = $"{record.FinishedAt:HH:mm} · 专注 {record.Minutes} 分钟 · {(record.Completed ? "走完" : "提前结束")}",
                FontSize = 13,
                Foreground = _mutedBrush,
            });
        }
    }

    /// <summary>
    /// 滑块要区分「用户拖动」和「每秒刷新回写」：
    /// 拖动时记下状态（期间不让刷新覆盖位置），放手后再把值交给引擎。
    /// </summary>
    private void WireSlider(Slider slider, Action onDragStart, Action onDragEnd, Action<double> onUserValue)
    {
        // 滑块用我们的主色，跟插件整体配色统一（默认是系统强调色）
        ApplySliderAccent(slider);

        slider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => onDragStart()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => onDragEnd()), true);
        slider.PointerCaptureLost += (_, _) => onDragEnd();
        slider.ValueChanged += (_, e) =>
        {
            if (_updatingSlider) return;
            onDragStart();
            onUserValue(e.NewValue);
        };
    }

    /// <summary>按「− / +」调倒计时时长（步长按当前长度自适应：短的 1 分钟，长的 5 分钟）。</summary>
    private void NudgeCountdown(int delta)
    {
        var step = _countdown.Minutes >= 30 ? 5 : 1;
        _countdown.SetMinutes(_countdown.Minutes + delta * step);
        RefreshCountdown();
    }

    /// <summary>把备注框里的值交给计时器（值没变就不写，避免多余的通知）。</summary>
    private void CommitLabel() => _countdown.SetLabel(_cdLabelBox.Text ?? string.Empty);

    // ---- 让聚光卡能收键盘输入 ----
    //
    // 宿主的聚光卡窗口带 WS_EX_NOACTIVATE：鼠标点它不会成为前台窗口（好处是用别的软件时不被抢焦点），
    // 但代价是**键盘输入送不进来**，里面放 TextBox 也打不了字（实测：能聚焦、有光标，但按键到不了）。
    // 解法：用户在卡片里点输入框时，插件主动把卡片窗口抢到前台 —— 只在这一刻抢，平时依旧不打扰。
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void ActivateCardWindow()
    {
        try
        {
            if (!GetCursorPos(out var point)) return;

            var hwnd = WindowFromPoint(point);
            if (hwnd == IntPtr.Zero) return;

            var root = GetAncestor(hwnd, 2);   // GA_ROOT：取顶层窗口
            if (root == IntPtr.Zero) return;

            // 只抢前台，**不要**再调 SetFocus(窗口) —— 那会把 XAML 内部的输入焦点顶掉，
            // 结果窗口是前台了、按键却没人收。
            SetForegroundWindow(root);

            // 窗口成为前台之后，再把 XAML 的输入焦点正式落到输入框上
            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    _cdLabelBox.Focus(FocusState.Programmatic);
                }
                catch
                {
                    // 输入框已经被收起时忽略
                }
            });
        }
        catch
        {
            // 抢不到焦点也不影响其它功能（顶多是这次打不了字）
        }
    }

    /// <summary>滑块统一用插件主色（默认是系统强调色）。只在构造阶段写资源，运行期不再动。</summary>
    private void ApplySliderAccent(Slider slider)
    {
        slider.Resources["SliderTrackValueFill"] = _accentBrush;
        slider.Resources["SliderTrackValueFillPointerOver"] = _accentBrush;
        slider.Resources["SliderTrackValueFillPressed"] = _accentBrush;
        slider.Resources["SliderThumbBackground"] = _accentBrush;
        slider.Resources["SliderThumbBackgroundPointerOver"] = _accentBrush;
        slider.Resources["SliderThumbBackgroundPressed"] = _accentBrush;
    }

    private Slider MakeSlider(double min, double max, double step, double value)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = value,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplySliderAccent(slider);
        return slider;
    }

    /// <summary>一行「标题 − 滑块 + 数值 分钟」：滑块为主，± 号做微调。</summary>
    private StackPanel SliderRow(string title, Slider slider, TextBlock value, Action minus, Action plus)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Label(title));
        row.Children.Add(StepButton("−", minus));
        row.Children.Add(slider);
        row.Children.Add(StepButton("+", plus));
        row.Children.Add(value);
        row.Children.Add(Label("分钟", 2));
        return row;
    }

    /// <summary>改番茄钟时长：写设置 → 引擎重新读取 → 立刻应用到当前这一段。</summary>
    private void SetPomodoroMinutes(string key, int minutes)
    {
        var current = key == PomodoroEngine.KeyFocusMin ? _engine.FocusMinutes : _engine.ShortBreakMinutes;
        var next = Math.Clamp(minutes, 1, 180);
        if (next == current) return;

        _engine.SetDuration(key, next);
        _engine.ApplyDurationsNow();
        RefreshPomodoro();
    }

    private Button StepButton(string glyph, Action onClick)
    {
        var button = PomodoroUi.Chip(glyph, _chipFillBrush, _textBrush, _chipBorderBrush, 14);
        button.Padding = new Thickness(10, 2, 10, 2);
        button.Click += (_, _) => onClick();
        return button;
    }

    private TextBlock Label(string text, double leftMargin = 0) => new()
    {
        Text = text,
        FontSize = 13,
        Margin = new Thickness(leftMargin, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = _mutedBrush,
    };

    /// <summary>统计卡的三支配色画刷（值 / 底 / 描边）：主题或阶段一变只改颜色，不重建控件。</summary>
    private sealed class StatVisual
    {
        public SolidColorBrush Value { get; } = new();
        public SolidColorBrush Fill { get; } = new();
        public SolidColorBrush Edge { get; } = new();
    }

    private TextBlock StatValue(StatVisual visual) => new()
    {
        Text = "--",
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        FontFamily = PomodoroUi.NumberFont,
        Foreground = visual.Value,
    };

    private Border StatCard(string caption, TextBlock value, StatVisual visual)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = caption, FontSize = 12, Foreground = _mutedBrush });
        stack.Children.Add(value);

        return new Border
        {
            Background = visual.Fill,
            BorderBrush = visual.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 16, 10),
            MinWidth = 148,
            Child = stack,
        };
    }

    /// <summary>
    /// 输入框（TextBox）的底色默认跟「应用主题」，卡片是深色时就会露出一块浅色。
    /// 必须在构造阶段设置：元素进可视树后再写 ResourceDictionary 会抛 COMException（见 StyleTab）。
    /// </summary>
    private void StyleInput(Control control)
    {
        control.Resources["TextControlBackground"] = _chipFillBrush;
        control.Resources["TextControlBackgroundPointerOver"] = _chipFillBrush;
        control.Resources["TextControlBackgroundFocused"] = _chipFillBrush;
        control.Resources["TextControlForeground"] = _textBrush;
        control.Resources["TextControlForegroundPointerOver"] = _textBrush;
        control.Resources["TextControlForegroundFocused"] = _textBrush;
        control.Resources["TextControlBorderBrush"] = _chipBorderBrush;
        control.Resources["TextControlBorderBrushPointerOver"] = _accentBrush;
        control.Resources["TextControlBorderBrushFocused"] = _accentBrush;
        control.Resources["TextControlPlaceholderForeground"] = _faintBrush;
        control.Resources["TextControlPlaceholderForegroundPointerOver"] = _mutedBrush;
        control.Resources["TextControlPlaceholderForegroundFocused"] = _mutedBrush;

        control.Background = _chipFillBrush;
        control.Foreground = _textBrush;
        control.BorderBrush = _chipBorderBrush;
    }

    /// <summary>构造阶段把页签的配色资源绑到专用画刷上（只做一次）。</summary>
    private static void WireTabStyles(Button tab, SolidColorBrush fill, SolidColorBrush hover, SolidColorBrush text)
    {
        tab.Background = fill;
        tab.Foreground = text;
        tab.BorderBrush = fill;

        tab.Resources["ButtonBackground"] = fill;
        tab.Resources["ButtonBackgroundPointerOver"] = hover;
        tab.Resources["ButtonBackgroundPressed"] = hover;
        tab.Resources["ButtonForeground"] = text;
        tab.Resources["ButtonForegroundPointerOver"] = text;
        tab.Resources["ButtonForegroundPressed"] = text;
        tab.Resources["ButtonBorderBrush"] = fill;
        tab.Resources["ButtonBorderBrushPointerOver"] = hover;
        tab.Resources["ButtonBorderBrushPressed"] = hover;
    }

    /// <summary>选中的页签用主色填充，未选中的保持中性；只改颜色，不动资源字典。</summary>
    private void StyleTab(Button tab, SolidColorBrush fill, SolidColorBrush hover, SolidColorBrush text, bool selected)
    {
        if (selected)
        {
            fill.Color = _accentColor;
            hover.Color = Windows.UI.Color.FromArgb(210, _accentColor.R, _accentColor.G, _accentColor.B);
            text.Color = Windows.UI.Color.FromArgb(255, 255, 255, 255);   // 蓝底白字
        }
        else
        {
            fill.Color = Ink(16);
            hover.Color = Ink(32);
            text.Color = Ink(170);
        }

        tab.Background = fill;
        tab.Foreground = text;
        tab.BorderBrush = fill;
    }

    private void HookTheme()
    {
        if (_themeHooked) return;
        _theme.Changed += ApplyThemeColors;
        _themeHooked = true;
    }

    private void UnhookTheme()
    {
        if (!_themeHooked) return;
        _theme.Changed -= ApplyThemeColors;
        _themeHooked = false;
    }

    /// <summary>
    /// 卡片是**独立的浅色卡片**（白底深字），不跟随岛体的明暗 ——
    /// 所以这里用固定的浅色配色，不再看 _theme.IsLight。
    /// </summary>
    private void ApplyThemeColors()
    {
        _surfaceBrush.Color = Windows.UI.Color.FromArgb(255, 247, 247, 249);   // 近白（比纯白柔和一点）
        _textBrush.Color = Ink(255);
        _mutedBrush.Color = Ink(175);
        _faintBrush.Color = Ink(120);
        _dividerBrush.Color = Ink(26);
        _cardFillBrush.Color = Ink(14);
        _chipFillBrush.Color = Ink(18);
        _chipBorderBrush.Color = Ink(34);
        UpdateAccent();

        if (IsLoaded) Refresh();
    }

    /// <summary>
    /// 主色：**番茄钟和倒计时统一用同一个蓝**（不再按阶段变色），
    /// 页面在主色上用的是浅色卡片，所以取浅色版（#007AFF）。
    /// </summary>
    private void UpdateAccent()
    {
        var accent = PomodoroUi.CountdownAccent(isLight: true);

        _accentBrush.Color = accent;
        _accentFillBrush.Color = accent;
        _accentColor = accent;

        // 沙漏和同步的百分比都跟着主色走
        // （构造期间 ApplyThemeColors 会先跑一次，那时这两个控件还没建出来，所以要判空）
        if (_ring is not null) _ring.Accent = accent;
        if (_cdRing is not null) _cdRing.Accent = accent;

        // 白底上三张统计卡各给一个色，看着有层次又不脏
        ApplyStatColors(_statDone, Windows.UI.Color.FromArgb(255, 0x00, 0x7A, 0xFF));   // 蓝
        ApplyStatColors(_statFocus, Windows.UI.Color.FromArgb(255, 0x1F, 0x8C, 0x3F));  // 绿
        ApplyStatColors(_statCycle, Windows.UI.Color.FromArgb(255, 0xC9, 0x6A, 0x00));  // 橙
    }

    private static void ApplyStatColors(StatVisual visual, Windows.UI.Color tint)
    {
        visual.Value.Color = tint;
        visual.Fill.Color = PomodoroUi.WithAlpha(tint, 34);
        visual.Edge.Color = PomodoroUi.WithAlpha(tint, 90);
    }

    /// <summary>卡片是浅底，所以"中性色"恒为黑系墨色。</summary>
    private static Windows.UI.Color Ink(byte alpha) => Windows.UI.Color.FromArgb(alpha, 0, 0, 0);
}
