using WinIsland.Core;

namespace PomodoroIsland;

/// <summary>番茄钟的三个阶段。</summary>
public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

/// <summary>给界面用的只读快照：界面只读它，不直接碰引擎内部状态。</summary>
public sealed class PomodoroSnapshot
{
    public PomodoroPhase Phase { get; init; }
    public bool IsRunning { get; init; }
    public bool HasStarted { get; init; }
    public TimeSpan Remaining { get; init; }
    public TimeSpan Total { get; init; }
    public int CompletedToday { get; init; }
    public int FocusMinutesToday { get; init; }
    public int FocusesInCycle { get; init; }
    public int LongBreakEvery { get; init; }
    public bool AutoStart { get; init; }
    public bool SoundEnabled { get; init; }
    public bool NotifyEnabled { get; init; }
    public IReadOnlyList<PomodoroRecord> Recent { get; init; } = Array.Empty<PomodoroRecord>();

    public string PhaseName => Phase switch
    {
        PomodoroPhase.Focus => "专注中",
        PomodoroPhase.ShortBreak => "短休息",
        _ => "长休息",
    };

    /// <summary>
    /// 大号时间文字：还没开始时**显示这一段的完整时长**（而不是 --:--）。
    /// 之前用 --:-- 占位，几个短横线在大号字体下看着就像"字体没渲染出来"。
    /// </summary>
    public string TimeText => Format(HasStarted ? Remaining : Total);

    /// <summary>当前阶段已过去的比例（0~1）。</summary>
    public double ElapsedRatio => Total.TotalSeconds <= 0
        ? 0
        : Math.Clamp(1 - Remaining.TotalSeconds / Total.TotalSeconds, 0, 1);

    /// <summary>展开态那一行说明。</summary>
    public string SummaryText => Phase switch
    {
        PomodoroPhase.Focus => $"今天完成 {CompletedToday} 个 · 第 {FocusesInCycle + 1} 个进行中",
        PomodoroPhase.ShortBreak => $"今天完成 {CompletedToday} 个 · 短休息，接下来第 {FocusesInCycle + 1} 个",
        _ => $"今天完成 {CompletedToday} 个 · 长休息，本轮已完成 {FocusesInCycle} 个",
    };

    public string PrimaryButtonText => IsRunning ? "暂停" : "开始";

    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(value.TotalSeconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
}

/// <summary>
/// 倒计时核心：纯逻辑，不碰界面。计时本身由插件每秒钟调一次 <see cref="Tick"/> 驱动
/// （宿主定时器是 UI 线程的，停用时会自动停，见 references/sdk-api.md §8）。
///
/// 用「截止时刻」而不是「每 tick 减一秒」来算剩余时间：tick 漏掉几次（界面卡顿、系统睡眠）
/// 也不会让倒计时走慢。
/// </summary>
public sealed class PomodoroEngine
{
    public const string KeyFocusMin = "focusMin";
    public const string KeyShortBreakMin = "shortBreakMin";
    public const string KeyLongBreakMin = "longBreakMin";
    public const string KeyLongBreakEvery = "longBreakEvery";
    public const string KeyAutoStart = "autoStart";
    public const string KeySound = "sound";
    public const string KeyNotify = "notify";

    public const int DefaultFocusMin = 25;
    public const int DefaultShortBreakMin = 5;
    public const int DefaultLongBreakMin = 15;
    public const int DefaultLongBreakEvery = 4;

    private const int MaxRecent = 8;

    private readonly IPluginContext _context;
    private readonly PomodoroStore _store;
    private readonly PomodoroData _data;
    private readonly List<PomodoroRecord> _recent;

    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private bool _running;
    private bool _stopped;
    private bool _hasStarted;
    private TimeSpan _remaining;
    private TimeSpan _total;
    private DateTimeOffset _deadline;
    private DateTimeOffset _lastTick;
    private double _phaseSeconds;

    /// <summary>某个阶段结束（自然走完或「完成一个」）时触发：(结束的阶段, 是否已自动开始下一段)。</summary>
    public event Action<PomodoroPhase, bool>? PhaseCompleted;

    /// <summary>状态有任何变化（含每秒跳秒）时触发，界面据此刷新。</summary>
    public event Action? Changed;

    public PomodoroEngine(IPluginContext context, PomodoroStore store)
    {
        _context = context;
        _store = store;
        _data = store.Load();
        _recent = _data.Recent;
        _total = Duration(_phase);
        _remaining = _total;
    }

    // ---- 设置（每次都现读，不用初始化时缓存的字段，见 §15.1）----

    public int FocusMinutes => Clamp(_context.Settings.Get(KeyFocusMin, DefaultFocusMin), 1, 180);

    public int ShortBreakMinutes => Clamp(_context.Settings.Get(KeyShortBreakMin, DefaultShortBreakMin), 1, 180);

    public int LongBreakMinutes => Clamp(_context.Settings.Get(KeyLongBreakMin, DefaultLongBreakMin), 1, 180);

    public int LongBreakEvery => Clamp(_context.Settings.Get(KeyLongBreakEvery, DefaultLongBreakEvery), 1, 12);

    public bool AutoStart => _context.Settings.Get(KeyAutoStart, true);

    public bool SoundEnabled => _context.Settings.Get(KeySound, true);

    public bool NotifyEnabled => _context.Settings.Get(KeyNotify, true);

    public PomodoroSnapshot Snapshot() => new()
    {
        Phase = _phase,
        IsRunning = _running,
        HasStarted = _hasStarted,
        Remaining = _remaining,
        Total = _total,
        CompletedToday = _data.CompletedToday,
        FocusMinutesToday = (int)Math.Round(_data.FocusSecondsToday / 60.0),
        FocusesInCycle = _data.FocusesInCycle,
        LongBreakEvery = LongBreakEvery,
        AutoStart = AutoStart,
        SoundEnabled = SoundEnabled,
        NotifyEnabled = NotifyEnabled,
        Recent = _recent.ToArray(),
    };

    // ---- 控制 ----

    public void Tick()
    {
        if (_stopped || !_running) return;

        var now = DateTimeOffset.Now;
        var delta = now - _lastTick;
        if (delta > TimeSpan.Zero) _phaseSeconds += delta.TotalSeconds;
        _lastTick = now;

        var left = _deadline - now;
        if (left <= TimeSpan.Zero)
        {
            CompletePhase(natural: true);
            return;
        }

        _remaining = left;
        RaiseChanged();
    }

    public void Toggle()
    {
        if (_running) Pause();
        else Start();
    }

    public void Start()
    {
        if (_stopped || _running) return;
        if (_remaining <= TimeSpan.Zero) _remaining = Duration(_phase);

        _hasStarted = true;
        _running = true;
        _lastTick = DateTimeOffset.Now;
        _deadline = _lastTick + _remaining;
        RaiseChanged();
    }

    public void Pause()
    {
        if (_stopped || !_running) return;

        var left = _deadline - DateTimeOffset.Now;
        _remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        _running = false;
        RaiseChanged();
    }

    /// <summary>重置本段：只把当前这一段的倒计时还原，不动今日统计。</summary>
    public void Reset()
    {
        _running = false;
        _phaseSeconds = 0;
        _total = Duration(_phase);
        _remaining = _total;
        _hasStarted = true;      // 让紧凑态显示时长而不是 --:--
        RaiseChanged();
    }

    /// <summary>跳过当前这一段：不计入统计，直接进下一段。</summary>
    public void Skip()
    {
        var finished = _phase;
        switch (finished)
        {
            case PomodoroPhase.Focus:
                _phase = PomodoroPhase.ShortBreak;
                break;
            case PomodoroPhase.ShortBreak:
                _phase = PomodoroPhase.Focus;
                break;
            default:
                _phase = PomodoroPhase.Focus;
                _data.FocusesInCycle = 0;   // 长休息被跳过，本轮算翻篇
                break;
        }

        EnterNewPhase(autoStart: AutoStart);
        Save();
        RaiseChanged();
    }

    /// <summary>手动「完成一个」：按实际走过的时间记一笔，然后进下一段（只对专注段有意义）。</summary>
    public void CompleteOne()
    {
        if (_phase != PomodoroPhase.Focus)
        {
            Skip();
            return;
        }

        CompletePhase(natural: false);
    }

    /// <summary>聚光卡里拖动滑块调整本段剩余时间（拖动时不能打断正在跑的计时）。</summary>
    public void SetRemaining(TimeSpan value)
    {
        var total = _total > TimeSpan.Zero ? _total : Duration(_phase);
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value > total) value = total;

        _remaining = value;
        _hasStarted = true;

        if (_running)
        {
            _lastTick = DateTimeOffset.Now;
            _deadline = _lastTick + _remaining;
        }

        RaiseChanged();
    }

    /// <summary>设置页 / 聚光卡改了时长后，把新的时长立刻应用到当前这一段。</summary>
    public void ApplyDurationsNow()
    {
        _phaseSeconds = 0;
        _total = Duration(_phase);
        _remaining = _total;
        if (_running)
        {
            _lastTick = DateTimeOffset.Now;
            _deadline = _lastTick + _remaining;
        }

        RaiseChanged();
    }

    /// <summary>写一个时长设置（会触发 OnSettingsChanged，插件据此重新读取）。</summary>
    public void SetDuration(string key, int minutes) => _context.Settings.Set(key, Clamp(minutes, 1, 180));

    /// <summary>设置变化后重新读取：还没开始的这一段直接换成长度，进行中的不动（下一段生效）。</summary>
    public void Configure()
    {
        if (!_running && !_hasStarted)
        {
            _total = Duration(_phase);
            _remaining = _total;
        }

        RaiseChanged();
    }

    public void Stop()
    {
        _stopped = true;
        _running = false;
        Save();
    }

    // ---- 内部 ----

    private TimeSpan Duration(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => TimeSpan.FromMinutes(FocusMinutes),
        PomodoroPhase.ShortBreak => TimeSpan.FromMinutes(ShortBreakMinutes),
        _ => TimeSpan.FromMinutes(LongBreakMinutes),
    };

    private void CompletePhase(bool natural)
    {
        var finished = _phase;
        var elapsedSeconds = _phaseSeconds;

        if (finished == PomodoroPhase.Focus)
        {
            var minutes = (int)Math.Round(elapsedSeconds / 60.0);
            if (minutes < 1) minutes = Math.Min(FocusMinutes, 1);
            if (minutes > 180) minutes = 180;

            _data.CompletedToday++;
            _data.FocusSecondsToday += (int)Math.Round(elapsedSeconds);
            _data.FocusesInCycle++;
            AddRecent(new PomodoroRecord(DateTimeOffset.Now, minutes, natural));
        }

        var every = LongBreakEvery;
        var goLongBreak = finished == PomodoroPhase.Focus && _data.FocusesInCycle >= every;

        if (finished == PomodoroPhase.LongBreak)
        {
            _data.FocusesInCycle = 0;   // 长休息结束，新的一轮
        }

        _phase = finished switch
        {
            PomodoroPhase.Focus => goLongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak,
            _ => PomodoroPhase.Focus,
        };

        var auto = AutoStart;
        EnterNewPhase(auto);
        Save();
        RaiseChanged();

        try
        {
            PhaseCompleted?.Invoke(finished, auto);
        }
        catch (Exception ex)
        {
            // 每帧/回调里的异常会进宿主的未处理异常计数，自己兜住
            _context.Log.Warn($"阶段结束回调出错（已忽略）：{ex.Message}");
        }
    }

    private void EnterNewPhase(bool autoStart)
    {
        _running = false;
        _phaseSeconds = 0;
        _hasStarted = false;
        _total = Duration(_phase);
        _remaining = _total;
        if (autoStart) Start();
    }

    private void AddRecent(PomodoroRecord record)
    {
        _recent.Insert(0, record);
        while (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
    }

    private void Save()
    {
        _data.Recent = _recent;
        _store.Save(_data);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"界面刷新出错（已忽略）：{ex.Message}");
        }
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
