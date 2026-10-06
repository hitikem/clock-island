using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PomodoroIsland;

/// <summary>
/// 岛上的视图，照着 iOS 灵动岛的样式做：
///
///   收起态：  ⌛ 29:49
///   展开态：  ⌛ 29:22      ( ⏸ )  ( ✕ )
///                倒计时
///
/// 沙漏是 <see cref="HourglassView"/>（矢量 + 会漏沙 + 换段翻转），不是 emoji。
///
/// 动画规则（不可违反）：
///   1. 所有元素一开始就常驻可视树；隐藏用尺寸/透明度归零，不用 Visibility = Collapsed；
///   2. 形态动画逐帧直接赋值，禁止 Storyboard（会抛 0x800F1001 并让岛卡死）；
///   3. 缓动与宿主一致：BackEase(EaseOut, Amplitude: 0.45)；
///   4. 进度在视图里自己累计，反转时从当前位置接着走。
/// </summary>
public sealed class PomodoroIslandView : UserControl, IMorphView
{
    private const double CompactIconWidth = 18;
    private const double ExpandedIconWidth = 30;
    private const double CompactTimeSize = 21;
    private const double ExpandedTimeSize = 36;
    private const double CompactLabelSize = 11;
    private const double ExpandedLabelSize = 14;
    private const double PauseButtonSize = 42;
    private const double CloseButtonSize = 34;
    private const double BackAmplitude = 0.45;

    /// <summary>收起态根元素的最小宽度：没备注时贴近参照图，有备注时给它腾地方。</summary>
    private const double CompactWidthNoNote = 150;
    private const double CompactWidthWithNote = 216;

    private readonly IIslandTheme _theme;
    private readonly HourglassView _hourglass;

    private readonly TextBlock _time;
    private readonly TextBlock _label;
    private readonly TextBlock _note;          // 收起态：图标旁边的备注 / 阶段名
    private readonly StackPanel _textStack;
    private readonly StackPanel _root;
    private readonly Button _pauseButton;
    private readonly FontIcon _pauseGlyph;
    private readonly Button _closeButton;

    // 共享画刷：换主题时只改颜色
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _transparentBrush = new() { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
    private readonly SolidColorBrush _pauseHoverBrush = new();
    private readonly SolidColorBrush _circleFillBrush = new();
    private readonly SolidColorBrush _circleHoverBrush = new();

    private PomodoroEngine _engine;
    private CountdownTimer _countdown;
    private bool _isCountdown;
    private bool _hasNote;
    private PomodoroPhase _accentPhase = PomodoroPhase.Focus;
    private bool _themeHooked;

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progressValue;

    /// <summary>诊断用：宿主调用展开/收起动画时记一条（由插件接上 Log）。</summary>
    public static Action<string>? Trace { get; set; }

    public PomodoroIslandView(PluginManifest manifest, IIslandTheme theme, PomodoroEngine engine, CountdownTimer countdown)
    {
        _engine = engine;
        _countdown = countdown;
        _theme = theme;
        FontFamily = PomodoroUi.UiFont;
        ApplyThemeColors();

        _hourglass = new HourglassView
        {
            BoxWidth = CompactIconWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _time = new TextBlock
        {
            Text = "25:00",
            FontSize = CompactTimeSize,
            FontWeight = FontWeights.Bold,
            FontFamily = PomodoroUi.NumberFont,
            Foreground = _textBrush,
            // 收起态里让时间靠右（跟参照图一样：图标在左、时间在右）；
            // 展开态字号变大后自然撑满，就和下面的标签左对齐了。
            MinWidth = 80,
            TextAlignment = TextAlignment.Right,
        };

        _label = new TextBlock
        {
            Text = "倒计时",
            FontSize = CompactLabelSize,
            Height = 0,
            Opacity = 0,
            Margin = new Thickness(0, 1, 0, 0),
            Foreground = _mutedBrush,
        };

        _textStack = new StackPanel { Spacing = 0, Margin = new Thickness(11, 0, 0, 0) };
        _textStack.Children.Add(_time);
        _textStack.Children.Add(_label);

        // 收起态也要能看见备注（「看书」这种），放在图标和时间中间；
        // 没有备注（比如番茄钟的专注段）时宽度归零，胶囊就和参照图一模一样。
        _note = new TextBlock
        {
            Text = "",
            FontSize = 12,
            MaxWidth = 0,
            Opacity = 0,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = _mutedBrush,
        };

        _pauseGlyph = new FontIcon
        {
            Glyph = "\uE769",                 // Pause
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
            Foreground = _accentBrush,
        };

        _pauseButton = MakeCircleButton(_pauseGlyph, PauseButtonSize, _transparentBrush, _pauseHoverBrush, _accentBrush, borderThickness: 2);
        _pauseButton.Click += (_, _) => ActiveToggle();

        var closeGlyph = new FontIcon
        {
            Glyph = "\uE711",                 // Cancel（叉）
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = _textBrush,
        };

        _closeButton = MakeCircleButton(closeGlyph, CloseButtonSize, _circleFillBrush, _circleHoverBrush, _transparentBrush, borderThickness: 0);
        _closeButton.Click += (_, _) => ActiveReset();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_hourglass);
        row.Children.Add(_note);
        row.Children.Add(_textStack);
        row.Children.Add(_pauseButton);
        row.Children.Add(_closeButton);

        _root = new StackPanel
        {
            Padding = new Thickness(13, 7, 13, 7),
            VerticalAlignment = VerticalAlignment.Center,
            // 收起态：有备注时主动要宽一点，否则宿主按旧宽度摆窗口会把时间裁掉
            MinWidth = CompactWidthNoNote,
        };
        _root.Children.Add(row);
        Content = _root;   // 视图根元素保持透明，岛体材质由宿主绘制

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        ApplyMorph(0);

        Loaded += (_, _) =>
        {
            HookTheme();
            ApplyThemeColors();
        };
        Unloaded += (_, _) =>
        {
            _morphTimer?.Stop();
            UnhookTheme();
        };
    }

    public UIElement View => this;

    /// <summary>插件重新启用时会新建引擎，这里把视图指过去（视图实例复用，不重复建树）。</summary>
    public void Attach(PomodoroEngine engine, CountdownTimer countdown)
    {
        _engine = engine;
        _countdown = countdown;
    }

    /// <summary>按数据模型刷新界面（插件每秒调一次）。</summary>
    public void Apply(IslandViewModel vm)
    {
        _isCountdown = vm.IsCountdown;
        _accentPhase = vm.AccentPhase;

        _time.Text = vm.TimeText;
        _label.Text = vm.PhaseLabel;

        // 收起态也要看得出这次倒计时是干什么的：把备注（「看书」）摆在图标旁边。
        // 番茄钟没有备注，就什么都不显示 —— 胶囊保持和参照图一致。
        var note = vm.IsCountdown ? vm.PhaseLabel : string.Empty;
        if (string.Equals(note, "倒计时", StringComparison.Ordinal)) note = string.Empty;

        var hasNote = !string.IsNullOrWhiteSpace(note);
        if (hasNote != _hasNote)
        {
            _hasNote = hasNote;
            _root.MinWidth = hasNote ? CompactWidthWithNote : CompactWidthNoNote;
        }

        _note.Text = note;

        _pauseGlyph.Glyph = vm.IsRunning ? "\uE769" : "\uE768";   // 暂停 / 播放

        _accentBrush.Color = AccentColor;
        _hourglass.Accent = AccentColor;
        _hourglass.SetState(vm.ElapsedRatio, vm.IsRunning);
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        Trace?.Invoke($"宿主请求展开（{duration.TotalMilliseconds:0}ms）");
        StartMorph(expanded: true, duration);
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        Trace?.Invoke($"宿主请求收起（{duration.TotalMilliseconds:0}ms）");
        StartMorph(expanded: false, duration);
    }

    /// <summary>一级卡片（岛）恒用参照图那个蓝，不随阶段变，保证和原图一致。</summary>
    private Windows.UI.Color AccentColor => PomodoroUi.IslandAccent(_theme.IsLight);

    private void ActiveToggle()
    {
        if (_isCountdown) _countdown.Toggle();
        else _engine.Toggle();
    }

    private void ActiveReset()
    {
        if (_isCountdown) _countdown.Reset();
        else _engine.Reset();
    }

    /// <summary>圆形图标按钮：尺寸/描边/配色一次配好，运行期只改颜色，不碰资源字典。</summary>
    private static Button MakeCircleButton(
        FontIcon glyph, double size, SolidColorBrush fill, SolidColorBrush hoverFill, SolidColorBrush border, double borderThickness)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 0,
            Height = 0,
            Opacity = 0,
            Padding = new Thickness(0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(size / 2),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(borderThickness),
            BorderBrush = border,
            Background = fill,
        };

        button.Resources["ButtonBackground"] = fill;
        button.Resources["ButtonBackgroundPointerOver"] = hoverFill;
        button.Resources["ButtonBackgroundPressed"] = hoverFill;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        return button;
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

    /// <summary>中性色：岛体深色时是白色系，浅色时是黑色系。</summary>
    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(170);

        var accent = AccentColor;
        _accentBrush.Color = accent;
        _pauseHoverBrush.Color = PomodoroUi.WithAlpha(accent, 38);
        _circleFillBrush.Color = Neutral(28);
        _circleHoverBrush.Color = Neutral(52);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>
    /// 形态动画走「逐帧属性赋值」，刻意不用 Storyboard（原因见 sdk-api §6.1）：
    /// 动态加载的插件程序集里属性路径动画解析不出类型，会在 tick 上抛 0x800F1001，
    /// 调用点的 try/catch 拦不住 —— 表现就是「大岛变小之后卡死」。
    /// </summary>
    private void StartMorph(bool expanded, TimeSpan duration)
    {
        var target = expanded ? 1d : 0d;

        _morphFrom = _progressValue;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);   // 收尾时锁定终态，避免残留中间值
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与 XAML 那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    /// <summary>
    /// 把 0~1 的形态进度铺到各元素上。收起态只有「沙漏 + 时间」，
    /// 展开时标签和两个圆钮才长出来（分开错峰，看着是一层层展开的）。
    /// </summary>
    private void ApplyMorph(double progress)
    {
        _progressValue = progress;
        var p = Math.Clamp(progress, 0, 1);
        var late = Math.Clamp((progress - 0.25) / 0.75, 0, 1);   // 按钮晚一点出现

        _hourglass.BoxWidth = CompactIconWidth + (ExpandedIconWidth - CompactIconWidth) * p;

        _time.FontSize = CompactTimeSize + (ExpandedTimeSize - CompactTimeSize) * p;

        _label.Height = Math.Max(0, 20 * p);   // 14px 字的行高约 18.6，给到 20 才不会被裁
        _label.Opacity = p;
        _label.FontSize = CompactLabelSize + (ExpandedLabelSize - CompactLabelSize) * p;

        // 备注只在收起态显示（展开后由时间下面那行标签接管），所以跟着形态反向进退
        _note.Opacity = _hasNote ? Math.Clamp(1 - p * 1.8, 0, 1) : 0;
        _note.MaxWidth = _hasNote ? Math.Max(0, 76 * (1 - p)) : 0;

        var pause = PauseButtonSize * late;
        _pauseButton.Width = Math.Max(0, pause);
        _pauseButton.Height = Math.Max(0, pause);
        _pauseButton.Opacity = late;
        _pauseButton.Margin = new Thickness(22 * late, 0, 0, 0);

        var close = CloseButtonSize * late;
        _closeButton.Width = Math.Max(0, close);
        _closeButton.Height = Math.Max(0, close);
        _closeButton.Opacity = late;
        _closeButton.Margin = new Thickness(10 * late, 0, 0, 0);
    }
}
