using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PomodoroIsland;

/// <summary>
/// 插件入口：装配引擎与视图、每秒驱动计时、处理设置变更、阶段 / 倒计时结束时提示。
///
/// 两套计时并存：
///   · 番茄钟（PomodoroEngine）—— 专注 / 短休 / 长休的循环
///   · 任意时长倒计时（CountdownTimer）—— 「3 分钟后去煮面」这种随手计时，可带备注
/// 岛上显示哪一套由设置键 <see cref="KeyMode"/> 决定，聚光卡（点岛体）里的页签就能切。
///
/// 设置页：全部集中在「设置 → 插件 → 番茄小岛」这一个分区里，按 4 个分类分组。
/// </summary>
public sealed class PomodoroPlugin : IslandPluginBase
{
    private const string KeyMode = "mode";
    private const string ModePomodoro = "pomodoro";
    private const string ModeCountdown = "countdown";

    /// <summary>
    /// 岛上的显示优先级。多个插件同时注册内容时，数值大的占主岛、其余进展开后的队列。
    /// 宿主内置媒体模块是 200，所以这里做成设置项（0~200 滑动条）。
    /// </summary>
    private const string KeyPriority = "priority";
    private const int DefaultPriority = 60;

    /// <summary>闹铃音：内置小米「白日梦」/ vivo / OPPO「新世界」/ 苹果「雷达」，以及系统提示音。</summary>
    private const string KeyAlarm = "alarm";

    private static readonly string[] SettingKeys =
    {
        PomodoroEngine.KeyFocusMin,
        PomodoroEngine.KeyShortBreakMin,
        PomodoroEngine.KeyLongBreakMin,
        PomodoroEngine.KeyLongBreakEvery,
        PomodoroEngine.KeyAutoStart,
        PomodoroEngine.KeySound,
        PomodoroEngine.KeyNotify,
        CountdownTimer.KeyMinutes,
        CountdownTimer.KeyLabel,
        KeyAlarm,
    };

    private PomodoroStore _store = null!;
    private PomodoroEngine _engine = null!;
    private CountdownTimer _countdown = null!;
    private PomodoroIslandView? _view;
    private PomodoroSpotlightView? _spotlight;
    private AlarmPlayer? _alarmPlayer;
    private bool _wired;
    private bool _stopped;
    private bool _lastCountdownRunning;

    protected override Task OnInitializeAsync()
    {
        _alarmPlayer ??= new AlarmPlayer(message => Log.Warn(message));

        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");
        _stopped = false;
        _store = new PomodoroStore(PluginDirectory, message => Log.Warn(message));
        _engine = new PomodoroEngine(Context, _store);
        _countdown = new CountdownTimer(Context);

        // 视图实例复用（InitializeAsync 可能被多次调用），但每次都要指向新引擎
        PomodoroIslandView.Trace = message => Log.Info(message);
        _view ??= new PomodoroIslandView(Manifest, Theme, _engine, _countdown);
        _view.Attach(_engine, _countdown);
        _view.AlarmDismissed -= OnAlarmDismissed;
        _view.AlarmDismissed += OnAlarmDismissed;
        _spotlight?.Attach(_engine, _countdown);

        _engine.Changed -= RefreshViews;
        _engine.Changed += RefreshViews;
        _engine.PhaseCompleted -= OnPhaseCompleted;
        _engine.PhaseCompleted += OnPhaseCompleted;

        _countdown.Changed -= RefreshViews;
        _countdown.Changed += RefreshViews;
        _countdown.Finished -= OnCountdownFinished;
        _countdown.Finished += OnCountdownFinished;

        // 闹铃响起来 / 停下来时，让岛上那颗「关闭铃声」按钮跟着出现或消失
        _alarmPlayer ??= new AlarmPlayer(message => Log.Warn(message));
        _alarmPlayer.RingingChanged -= OnRingingChanged;
        _alarmPlayer.RingingChanged += OnRingingChanged;

        if (!_wired)
        {
            // 设置改了就重新读一次（值没变的写入会被设置页挡掉，见 §15.1）
            foreach (var key in SettingKeys)
            {
                Context.Register(Context.OnSettingsChanged(key, () =>
                {
                    _engine.Configure();
                    RefreshViews();
                }));
            }

            Context.Register(Context.OnSettingsChanged(KeyMode, RefreshViews));

            // 优先级改了就重新注册一次常驻内容
            Context.Register(Context.OnSettingsChanged(KeyPriority, ApplyContent));

            _wired = true;
        }

        ApplyContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE916", BuildSettingsPage, order: 100));

        // UI 线程定时器：插件停用时宿主会自动停止它。
        // 即使什么都没在跑也要每秒刷一次 —— 小岛上那行「现在 23:45」得跟着走。
        Context.CreateTimer(TimeSpan.FromSeconds(1), repeat: true, OnTick);

        RefreshViews();
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        Log.Info("已停用");
        // 先立起停止标记：宿主拆界面时，每秒的刷新和回调还在跑，会往已经拆掉的控件上写东西
        _stopped = true;
        _engine.PhaseCompleted -= OnPhaseCompleted;
        _engine.Changed -= RefreshViews;
        _countdown.Finished -= OnCountdownFinished;
        _countdown.Changed -= RefreshViews;
        if (_alarmPlayer is not null) _alarmPlayer.RingingChanged -= OnRingingChanged;

        _engine.Stop();          // 顺手把统计落盘
        _countdown.Stop();
        _alarmPlayer?.Dispose();  // 停用时把闹铃播放器放掉，别留后台声音
        _wired = false;
        SetContent(null);
        return Task.CompletedTask;
    }

    private void OnTick()
    {
        if (_stopped) return;
        _engine.Tick();
        _countdown.Tick();

        // 诊断用：倒计时的起 / 停各记一条，不管是从卡片、岛上还是别处点的按钮都能看出来
        if (_countdown.IsRunning != _lastCountdownRunning)
        {
            _lastCountdownRunning = _countdown.IsRunning;
            Log.Info($"倒计时{(_countdown.IsRunning ? "开始" : "暂停")}：设定 {_countdown.Minutes} 分钟，"
                     + $"剩 {PomodoroSnapshot.Format(_countdown.Remaining)}");
        }

        RefreshViews();
    }

    /// <summary>注册（或重新注册）常驻内容：小岛 / 大岛的尺寸与变形由宿主统一管理。</summary>
    private void ApplyContent()
    {
        if (_view is null) return;

        SetContent(new IslandLiveContent
        {
            Priority = Settings.Get(KeyPriority, DefaultPriority),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = PomodoroUi.Accent(Theme.IsLight, _engine.Snapshot().Phase),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(230, 46),
            // 展开态内容实高约 84（沙漏 48 / 文字块 68 / 圆钮 42 + 上下留白），声明小了会被裁
            ExpandedSize = new Windows.Foundation.Size(340, 92),
            OnTap = OpenSpotlight,      // 点击岛体 = 打开「超级展开」聚光卡
        });
    }

    private bool IsCountdownMode =>
        string.Equals(Settings.Get(KeyMode, ModePomodoro), ModeCountdown, StringComparison.OrdinalIgnoreCase);

    /// <summary>把两套计时统一转成界面认的数据模型，再刷两块界面。一切都在 UI 线程上。</summary>
    private void RefreshViews()
    {
        if (_stopped) return;
        try
        {
            var vm = BuildViewModel();
            _view?.Apply(vm);
            _spotlight?.Apply(vm);

            // 闹铃响着的时候，岛上给一颗「关闭铃声」按钮
            if (_view is not null) _view.IsRinging = _alarmPlayer?.IsRinging == true;
        }
        catch (Exception ex)
        {
            // 打出完整异常（类型 + 堆栈）：WinRT 的 COM 错误 Message 常常是空的
            Log.Warn($"界面刷新失败（已忽略）：{ex.GetType().Name}｜{ex.Message}｜{ex.StackTrace}");
        }
    }

    private IslandViewModel BuildViewModel()
    {
        var snapshot = _engine.Snapshot();
        var clock = $"现在 {DateTime.Now:HH:mm:ss}";

        if (IsCountdownMode)
        {
            var ratio = _countdown.ElapsedRatio;
            var label = _countdown.DisplayLabel;

            return new IslandViewModel
            {
                Mode = TimerMode.Countdown,
                PhaseLabel = label,                     // 有备注就显示备注：「去煮面」
                TimeText = _countdown.TimeText,
                PercentText = Percent(ratio),
                TodayText = $"今天 {snapshot.CompletedToday} 个",
                ClockText = clock,
                CycleText = "",
                ElapsedText = $"已 {PomodoroSnapshot.Format(_countdown.Total - _countdown.Remaining)}",
                SummaryText = _countdown.IsFinished
                    ? $"{label} · 时间到"
                    : string.IsNullOrWhiteSpace(_countdown.Label)
                        ? $"设定 {_countdown.Minutes} 分钟 · 已过 {Percent(ratio)}"
                        : $"「{label}」设定 {_countdown.Minutes} 分钟 · 剩 {PomodoroSnapshot.Format(_countdown.Remaining)}",
                PrimaryButtonText = _countdown.IsRunning ? "暂停" : "开始",
                IsRunning = _countdown.IsRunning,
                HasStarted = _countdown.HasStarted,
                ElapsedRatio = ratio,
                CompletedToday = snapshot.CompletedToday,
                FocusMinutesToday = snapshot.FocusMinutesToday,
                CyclePosition = snapshot.FocusesInCycle,
                LongBreakEvery = snapshot.LongBreakEvery,
                FocusMinutes = _engine.FocusMinutes,
                ShortBreakMinutes = _engine.ShortBreakMinutes,
                CountdownMinutes = _countdown.Minutes,
                Recent = snapshot.Recent,
            };
        }

        return new IslandViewModel
        {
            Mode = TimerMode.Pomodoro,
            PhaseLabel = snapshot.PhaseName,
            TimeText = snapshot.TimeText,
            PercentText = Percent(snapshot.ElapsedRatio),
            TodayText = $"今天 {snapshot.CompletedToday} 个",
            ClockText = clock,
            CycleText = $"第 {Math.Min(snapshot.FocusesInCycle + 1, Math.Max(1, snapshot.LongBreakEvery))}/{Math.Max(1, snapshot.LongBreakEvery)} 个",
            ElapsedText = $"已 {PomodoroSnapshot.Format(snapshot.Total - snapshot.Remaining)}",
            SummaryText = snapshot.SummaryText,
            PrimaryButtonText = snapshot.PrimaryButtonText,
            IsRunning = snapshot.IsRunning,
            HasStarted = snapshot.HasStarted,
            ElapsedRatio = snapshot.ElapsedRatio,
            AccentPhase = snapshot.Phase,
            CompletedToday = snapshot.CompletedToday,
            FocusMinutesToday = snapshot.FocusMinutesToday,
            CyclePosition = snapshot.FocusesInCycle,
            LongBreakEvery = snapshot.LongBreakEvery,
            FocusMinutes = _engine.FocusMinutes,
            ShortBreakMinutes = _engine.ShortBreakMinutes,
            CountdownMinutes = _countdown.Minutes,
            Recent = snapshot.Recent,
        };
    }

    private static string Percent(double ratio) => $"{(int)Math.Round(Math.Clamp(ratio, 0, 1) * 100)}%";

    /// <summary>一个番茄阶段结束：放提示音 + 弹岛上消息。</summary>
    private void OnPhaseCompleted(PomodoroPhase finished, bool autoStarted)
    {
        Notify(() =>
        {
            var (title, text) = finished switch
            {
                PomodoroPhase.Focus => (
                    "专注结束",
                    _engine.Snapshot().Phase == PomodoroPhase.LongBreak
                        ? $"该长休息了 · {_engine.LongBreakMinutes} 分钟"
                        : $"休息一下 · {_engine.ShortBreakMinutes} 分钟"),
                PomodoroPhase.ShortBreak => ("短休息结束", $"开始新的专注 · {_engine.FocusMinutes} 分钟"),
                _ => ("长休息结束", $"开始新的专注 · {_engine.FocusMinutes} 分钟"),
            };

            if (!autoStarted) text += " · 点「开始」继续";
            return (title, text);
        });
    }

    private void OnCountdownFinished()
    {
        Notify(() =>
        {
            var label = _countdown.DisplayLabel;
            return (string.IsNullOrWhiteSpace(_countdown.Label) ? "倒计时结束" : $"{label} · 时间到",
                    $"设定的 {_countdown.Minutes} 分钟到了");
        });
    }

    /// <summary>用户点了岛上的「关闭铃声」：立刻静音并刷新界面。</summary>
    private void OnAlarmDismissed()
    {
        try
        {
            _alarmPlayer?.StopRinging();
            RefreshViews();
        }
        catch (Exception ex)
        {
            Log.Warn($"关闭铃声失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>闹铃开始响 / 停下来：刷新岛上那颗「关闭铃声」按钮。</summary>
    private void OnRingingChanged()
    {
        if (_stopped) return;
        try
        {
            RefreshViews();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新响铃状态失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>提示音 + 岛上消息（两处结束回调共用）。</summary>
    private void Notify(Func<(string Title, string Text)> build)
    {
        try
        {
            if (_engine.SoundEnabled) PlayChime();
            if (!_engine.NotifyEnabled) return;

            var (title, text) = build();
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = title,
                Text = text,
                Glyph = Manifest.IconGlyph ?? "\uE916",
                AccentColor = PomodoroUi.CountdownAccent(Theme.IsLight),
                Duration = TimeSpan.FromSeconds(4),
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"结束提示失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>打开聚光卡：卡片尺寸、内容、何时打开都由插件决定，飞入飞回与遮罩由宿主负责。</summary>
    private void OpenSpotlight()
    {
        _spotlight ??= new PomodoroSpotlightView(Manifest, Theme, _engine, _countdown, SetMode);

        // 卡片高度会被宿主夹到工作区的 92% 以内；内容外面有 ScrollViewer，不怕被裁
        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = new Windows.Foundation.Size(940, 662),
            OnClosed = () => _spotlight?.Apply(BuildViewModel()),
        });
    }

    private void SetMode(TimerMode mode)
    {
        Settings.Set(KeyMode, mode == TimerMode.Countdown ? ModeCountdown : ModePomodoro);
        RefreshViews();
    }

    // MB_ICONASTERISK：播放系统「提示」音效，不需要随插件带音频文件
    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool MessageBeep(uint uType);

    // 文件选择框要一个父窗口句柄（非打包应用的要求）
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 到点响铃：播设置在用的那个闹铃（小米「白日梦」/ vivo / OPPO「新世界」/ 苹果「雷达」）。
    /// 音频文件找不到或播不出来时，<see cref="AlarmPlayer"/> 会自动退回系统提示音。
    /// </summary>
    private void PlayChime()
    {
        try
        {
            var alarmId = Settings.Get(KeyAlarm, AlarmSounds.DefaultId);
            var path = AlarmSounds.Resolve(PluginDirectory, alarmId);

            // 不 await：这里是 UI 线程的结束回调，响铃不该把界面卡住
            _ = _alarmPlayer?.PlayAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"提示音播放失败（已忽略）：{ex.Message}");
            try { MessageBeep(0x00000040); } catch { /* 连系统音都失败就算了 */ }
        }
    }

    // ---- 设置页（全部集中在「设置 → 插件 → 番茄小岛」，按 4 个分类分组）----

    private bool _loading = true;

    /// <summary>设置页工厂：每次进页面都会调用，必须返回新实例。</summary>
    private UIElement BuildSettingsPage()
    {
        _loading = true;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            FontFamily = PomodoroUi.UiFont,
            Style = ThemeStyle("TitleTextBlockStyle"),
        });
        panel.Children.Add(new TextBlock
        {
            FontFamily = PomodoroUi.UiFont,
            Text = "改动即时生效。点岛体打开的聚光卡里也能直接操控，并随时切换番茄钟 / 倒计时。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(Section("专注时间"));
        panel.Children.Add(Card(Stack(
            SliderRow("专注", "每轮专注多久", PomodoroEngine.KeyFocusMin, PomodoroEngine.DefaultFocusMin, 1, 120, 5, "分钟"),
            SliderRow("短休息", null, PomodoroEngine.KeyShortBreakMin, PomodoroEngine.DefaultShortBreakMin, 1, 60, 1, "分钟"),
            SliderRow("长休息", null, PomodoroEngine.KeyLongBreakMin, PomodoroEngine.DefaultLongBreakMin, 1, 60, 5, "分钟"),
            SliderRow("长休息间隔", "每几个番茄进一次长休息", PomodoroEngine.KeyLongBreakEvery, PomodoroEngine.DefaultLongBreakEvery, 1, 12, 1, "个"))));

        var labelBox = new TextBox
        {
            FontFamily = PomodoroUi.UiFont,
            FontSize = 13,
            Text = Settings.Get(CountdownTimer.KeyLabel, string.Empty),
            PlaceholderText = "例如：去煮面",
            MinWidth = 240,
            VerticalAlignment = VerticalAlignment.Center,
        };
        labelBox.LostFocus += (_, _) => CommitLabel(labelBox);
        labelBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitLabel(labelBox);
        };

        panel.Children.Add(Section("倒计时"));
        panel.Children.Add(Card(Stack(
            SliderRow("默认时长", "随手定个时长的默认值", CountdownTimer.KeyMinutes, CountdownTimer.DefaultMinutes, 1, 120, 5, "分钟"),
            Row("默认备注", "写了会显示在岛上，可留空", labelBox))));

        panel.Children.Add(Section("提醒"));
        panel.Children.Add(Card(Stack(
            ToggleRow("自动开始下一段", "专注结束直接进休息，不用手动点", PomodoroEngine.KeyAutoStart, true),
            ToggleRow("结束提示音", "到点响一声", PomodoroEngine.KeySound, true),
            AlarmRow(),
            ToggleRow("结束时在岛上弹提示", "岛会临时显示一条提醒", PomodoroEngine.KeyNotify, true))));

        panel.Children.Add(Section("显示"));
        panel.Children.Add(Card(Stack(
            SliderRow("显示优先级", "越大越容易占主岛（宿主媒体模块是 200）", KeyPriority, DefaultPriority, 0, 200, 1, string.Empty))));

        var applyButton = new Button { Content = "把新时长用到当前这一段", FontFamily = PomodoroUi.UiFont };
        applyButton.Click += (_, _) =>
        {
            // 滑块的值是实时写进设置的；这里只要提交备注框（按钮点击早于 LostFocus）+ 立即生效
            CommitLabel(labelBox);
            _engine.ApplyDurationsNow();
            RefreshViews();

            Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"已应用 · 专注 {_engine.FocusMinutes} 分钟 / 休息 {_engine.ShortBreakMinutes} 分钟 / 倒计时 {_countdown.Minutes} 分钟",
                Glyph = Manifest.IconGlyph ?? "\uE916",
                Duration = TimeSpan.FromSeconds(3),
            });
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(applyButton);
        panel.Children.Add(actions);

        _loading = false;
        return panel;
    }

    /// <summary>简约的分类小标题：专注时间 / 倒计时 / 提醒 / 显示。</summary>
    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontFamily = PomodoroUi.UiFont,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 12, 0, -2),
    };

    /// <summary>竖直排布（设置卡片里的若干行）。</summary>
    private static StackPanel Stack(params FrameworkElement[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    /// <summary>
    /// 一行设置：左边标题（可带一句说明），右边控件。
    /// 跟 Windows 自带设置页一个路子 —— 干净、对齐、没有多余的边框。
    /// </summary>
    private static Grid Row(string title, string? hint, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
        });

        if (!string.IsNullOrEmpty(hint))
        {
            text.Children.Add(new TextBlock
            {
                Text = hint,
                FontFamily = PomodoroUi.UiFont,
                FontSize = 11.5,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>一行时长设置：滑块为主，± 号做单步微调，右边显示当前值。</summary>
    private FrameworkElement SliderRow(string title, string? hint, string key, int fallback, int min, int max, int step, string unit)
    {
        var current = Math.Clamp(Settings.Get(key, fallback), min, max);

        var valueText = new TextBlock
        {
            Text = $"{current}",
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 30,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = current,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var accent = new SolidColorBrush(PomodoroUi.Accent(Theme.IsLight, PomodoroPhase.Focus));
        foreach (var brushKey in new[]
                 {
                     "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                     "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                 })
        {
            slider.Resources[brushKey] = accent;
        }

        slider.ValueChanged += (_, e) =>
        {
            var value = (int)Math.Round(e.NewValue);
            valueText.Text = $"{value}";
            if (_loading) return;
            if (value == Settings.Get(key, fallback)) return;   // 值没变就别写
            Settings.Set(key, value);
        };

        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        control.Children.Add(StepButton("−", () => slider.Value = Math.Max(min, slider.Value - step)));
        control.Children.Add(slider);
        control.Children.Add(StepButton("+", () => slider.Value = Math.Min(max, slider.Value + step)));
        control.Children.Add(valueText);

        if (!string.IsNullOrEmpty(unit))
        {
            control.Children.Add(new TextBlock
            {
                Text = unit,
                FontFamily = PomodoroUi.UiFont,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6,
            });
        }

        return Row(title, hint, control);
    }

    /// <summary>一行开关：标题在左、开关在右。</summary>
    private FrameworkElement ToggleRow(string title, string? hint, string key, bool fallback)
    {
        var toggle = new ToggleSwitch
        {
            FontFamily = PomodoroUi.UiFont,
            IsOn = Settings.Get(key, fallback),
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
        };

        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            if (toggle.IsOn == Settings.Get(key, fallback)) return;   // 值没变就别写
            Settings.Set(key, toggle.IsOn);
        };

        return Row(title, hint, toggle);
    }

    /// <summary>细边框的小圆角按钮（只放 ± 号）。</summary>
    private static Button StepButton(string glyph, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
            Padding = new Thickness(0),
            MinWidth = 0,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// 一行闹铃选择：下拉挑内置闹铃（各厂商默认铃），旁边「试听」按钮立刻放一遍。
    /// 具体播放交给 <see cref="AlarmPlayer"/>，播不出来会自动退回系统提示音。
    /// </summary>
    private FrameworkElement AlarmRow()
    {
        var combo = new ComboBox
        {
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
            MinWidth = 190,
        };

        var ids = new List<string>();
        foreach (var (id, title, _) in AlarmSounds.Options(PluginDirectory, message => Log.Warn(message)))
        {
            ids.Add(id);
            combo.Items.Add(new ComboBoxItem
            {
                Content = title,
                FontFamily = PomodoroUi.UiFont,
                FontSize = 14,
            });
        }

        var current = Settings.Get(KeyAlarm, AlarmSounds.DefaultId);
        var index = ids.FindIndex(id => string.Equals(id, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var i = combo.SelectedIndex;
            if (i < 0 || i >= ids.Count) return;
            if (string.Equals(Settings.Get(KeyAlarm, AlarmSounds.DefaultId), ids[i], StringComparison.Ordinal)) return;
            Settings.Set(KeyAlarm, ids[i]);
        };

        var preview = new Button
        {
            Content = "试听",
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
            Padding = new Thickness(14, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        preview.Click += (_, _) =>
        {
            var i = combo.SelectedIndex;
            var id = i >= 0 && i < ids.Count ? ids[i] : AlarmSounds.DefaultId;
            _ = _alarmPlayer?.PlayAsync(AlarmSounds.Resolve(PluginDirectory, id));
        };

        // 「添加音频」：把选中的音频复制进插件目录的 alarms\，然后刷新下拉列表
        var add = new Button
        {
            Content = "添加音频…",
            FontFamily = PomodoroUi.UiFont,
            FontSize = 14,
            Padding = new Thickness(14, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hint = new TextBlock
        {
            FontFamily = PomodoroUi.UiFont,
            FontSize = 11.5,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Text = string.Empty,
        };
        add.Click += async (_, _) => await PickAlarmFileAsync(hint);

        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        control.Children.Add(combo);
        control.Children.Add(preview);
        control.Children.Add(add);

        var outer = new StackPanel { Spacing = 4 };
        outer.Children.Add(Row("闹铃", "到点响这个（点「试听」马上听；响的时候岛上会出现关闭按钮）", control));
        outer.Children.Add(hint);
        return outer;
    }

    /// <summary>
    /// 「添加音频…」：弹出系统文件选择框，把用户选的音频复制到插件目录 alarms\ 并设为当前闹铃。
    /// 各厂商铃声属于版权素材，插件不附带，让用户自己导入。
    /// </summary>
    private async Task PickAlarmFileAsync(TextBlock hint)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            foreach (var ext in new[] { ".mp3", ".wav", ".ogg", ".m4a", ".wma", ".aac", ".flac" })
            {
                picker.FileTypeFilter.Add(ext);
            }

            // 非打包应用必须先把窗口句柄交给选择器，否则弹不出来。
            // 插件跑在宿主进程里，拿前台窗口的句柄即可（也就是用户正在操作的 WinIsland 设置窗口）。
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var dir = Path.Combine(PluginDirectory, AlarmSounds.UserFolderName);
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, file.Name);
            File.Copy(file.Path, target, overwrite: true);

            Settings.Set(KeyAlarm, AlarmSounds.CustomPrefix + file.Name);
            hint.Text = $"已添加：{file.Name}（已设为当前闹铃）";
            Log.Info($"已导入自定义闹铃：{target}");
        }
        catch (Exception ex)
        {
            hint.Text = $"添加失败：{ex.Message}";
            Log.Warn($"添加自定义闹铃失败：{ex.Message}");
        }
    }

    private static Border Card(UIElement child) => new()
    {
        Style = ThemeStyle("SettingsCardStyle"),
        Padding = new Thickness(16, 12, 16, 12),
        Child = child,
    };

    /// <summary>取宿主资源里的样式；取不到就返回 null（没有样式也不会让设置页崩掉）。</summary>
    private static Style? ThemeStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    /// <summary>把备注框里的值写进设置（值没变就不写）。</summary>
    private void CommitLabel(TextBox box) => _countdown.SetLabel(box.Text ?? string.Empty);
}
