using WinIsland.Core;

namespace PomodoroIsland;

/// <summary>
/// 「任意时长倒计时」：和番茄钟并存，各自独立计时。
/// 番茄钟管的是「专注 → 休息」的循环；这个是「我要 3 分钟后提醒我」的随手计时器。
/// </summary>
public sealed class CountdownTimer
{
    public const string KeyMinutes = "countdownMin";
    public const string KeyLabel = "countdownLabel";
    public const int DefaultMinutes = 10;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 600;

    private readonly IPluginContext _context;

    private bool _running;
    private bool _stopped;
    private bool _hasStarted;
    private bool _finished;
    private TimeSpan _remaining;
    private TimeSpan _total;
    private DateTimeOffset _deadline;
    private DateTimeOffset _lastTick;

    public event Action? Changed;
    public event Action? Finished;

    public CountdownTimer(IPluginContext context)
    {
        _context = context;
        _total = TimeSpan.FromMinutes(Minutes);
        _remaining = _total;
    }

    public int Minutes => Math.Clamp(_context.Settings.Get(KeyMinutes, DefaultMinutes), MinMinutes, MaxMinutes);

    /// <summary>备注：这次倒计时是干什么用的（「去煮面」「泡茶」「开会」），显示在岛上。</summary>
    public string Label => (_context.Settings.Get(KeyLabel, string.Empty) ?? string.Empty).Trim();

    /// <summary>岛上那行标题：有备注就用备注，没有就写「倒计时」。</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? "倒计时" : Label;

    public bool IsRunning => _running;

    public bool HasStarted => _hasStarted;

    public bool IsFinished => _finished;

    public TimeSpan Remaining => _remaining;

    public TimeSpan Total => _total;

    /// <summary>没开始过就显示设定的时长（比 --:-- 更有用：一眼看到「点了会跑多久」）。</summary>
    public string TimeText => _hasStarted ? PomodoroSnapshot.Format(_remaining) : PomodoroSnapshot.Format(_total);

    public double ElapsedRatio => _total.TotalSeconds <= 0
        ? 0
        : Math.Clamp(1 - _remaining.TotalSeconds / _total.TotalSeconds, 0, 1);

    public void Tick()
    {
        if (_stopped || !_running) return;

        var now = DateTimeOffset.Now;
        _lastTick = now;
        var left = _deadline - now;

        if (left <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _running = false;
            _hasStarted = true;
            _finished = true;
            RaiseChanged();

            try
            {
                Finished?.Invoke();
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"倒计时结束回调出错（已忽略）：{ex.Message}");
            }
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

        if (_remaining <= TimeSpan.Zero)
        {
            _total = TimeSpan.FromMinutes(Minutes);
            _remaining = _total;
            _finished = false;
        }

        _hasStarted = true;
        _finished = false;
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

    /// <summary>回到设定时长的起点，停住。</summary>
    public void Reset()
    {
        _running = false;
        _finished = false;
        _hasStarted = false;
        _total = TimeSpan.FromMinutes(Minutes);
        _remaining = _total;
        RaiseChanged();
    }

    /// <summary>改设定时长（写设置）。正在跑的时候不动当前这一轮，只影响下一次开始。</summary>
    public void SetMinutes(int minutes)
    {
        var value = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        if (value != Minutes) _context.Settings.Set(KeyMinutes, value);

        if (!_running)
        {
            _total = TimeSpan.FromMinutes(value);
            _remaining = _total;
            _hasStarted = false;
            _finished = false;
        }

        RaiseChanged();
    }

    /// <summary>改备注（写设置）。</summary>
    public void SetLabel(string label)
    {
        var value = (label ?? string.Empty).Trim();
        if (value.Length > 20) value = value[..20];
        if (string.Equals(value, Label, StringComparison.Ordinal)) return;

        _context.Settings.Set(KeyLabel, value);
        RaiseChanged();
    }

    /// <summary>聚光卡里拖动滑块调整剩余时间。</summary>
    public void SetRemaining(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (_total > TimeSpan.Zero && value > _total) value = _total;

        _remaining = value;
        _hasStarted = true;
        if (_remaining > TimeSpan.Zero) _finished = false;

        if (_running)
        {
            _lastTick = DateTimeOffset.Now;
            _deadline = _lastTick + _remaining;
        }

        RaiseChanged();
    }

    public void Stop()
    {
        _stopped = true;
        _running = false;
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
}
