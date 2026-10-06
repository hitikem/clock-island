namespace PomodoroIsland;

/// <summary>插件当前在跑哪一套计时。</summary>
public enum TimerMode
{
    Pomodoro,
    Countdown,
}

/// <summary>
/// 界面只认这一个数据模型：番茄钟和倒计时都先转成它，视图就不必关心背后是谁。
/// 一次构建、两处使用（小岛视图 + 聚光卡）。
/// </summary>
public sealed class IslandViewModel
{
    public TimerMode Mode { get; init; } = TimerMode.Pomodoro;

    /// <summary>阶段名：专注中 / 短休息 / 长休息 / 倒计时。</summary>
    public string PhaseLabel { get; init; } = "";

    public string TimeText { get; init; } = "--:--";

    public string PercentText { get; init; } = "0%";

    /// <summary>小岛第二行：今天完成几个番茄。</summary>
    public string TodayText { get; init; } = "";

    /// <summary>小岛第二行：当前时间。</summary>
    public string ClockText { get; init; } = "";

    /// <summary>小岛第二行：第几个（番茄钟）或共多长时间（倒计时）。</summary>
    public string CycleText { get; init; } = "";

    /// <summary>小岛第二行：这一段已经过去多久。</summary>
    public string ElapsedText { get; init; } = "";

    /// <summary>展开态那一行说明。</summary>
    public string SummaryText { get; init; } = "";

    public string PrimaryButtonText { get; init; } = "开始";

    public bool IsRunning { get; init; }

    public bool HasStarted { get; init; }

    public double ElapsedRatio { get; init; }

    /// <summary>番茄钟模式下当前阶段的颜色依据。</summary>
    public PomodoroPhase AccentPhase { get; init; } = PomodoroPhase.Focus;

    public bool IsCountdown => Mode == TimerMode.Countdown;

    // ---- 聚光卡需要的 ----

    public int CompletedToday { get; init; }

    public int FocusMinutesToday { get; init; }

    public int CyclePosition { get; init; }

    public int LongBreakEvery { get; init; }

    public int CountdownMinutes { get; init; } = CountdownTimer.DefaultMinutes;

    public int FocusMinutes { get; init; } = PomodoroEngine.DefaultFocusMin;

    public int ShortBreakMinutes { get; init; } = PomodoroEngine.DefaultShortBreakMin;

    public IReadOnlyList<PomodoroRecord> Recent { get; init; } = Array.Empty<PomodoroRecord>();

    public string ModeKey => Mode == TimerMode.Countdown ? "countdown" : "pomodoro";
}
