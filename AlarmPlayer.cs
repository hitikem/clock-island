using System;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PomodoroIsland;

/// <summary>
/// 放提醒音。优先用 Windows 的 MediaPlayer 播真实音频文件（ogg / mp3 / wav 都行），
/// 没有文件或播放失败就退回系统提示音 —— 保证「到点一定有声音」。
/// </summary>
internal sealed class AlarmPlayer : IDisposable
{
    private readonly Action<string>? _warn;
    private MediaPlayer? _player;
    private string? _loadedPath;
    private bool _disposed;

    public AlarmPlayer(Action<string>? warn = null) => _warn = warn;

    /// <summary>正在响铃（用来决定岛上要不要显示「关闭铃声」按钮）。</summary>
    public bool IsRinging { get; private set; }

    /// <summary>响铃变化通知（开始响 / 停下来），界面据此显示或隐藏关闭按钮。</summary>
    public event Action? RingingChanged;

    /// <summary>最长响多久：到点没人管也会自己停，免得响一整夜。</summary>
    private static readonly TimeSpan MaxRing = TimeSpan.FromSeconds(60);

    /// <summary>立刻停止响铃（用户点「关闭铃声」时调用）。</summary>
    public void StopRinging()
    {
        if (!IsRinging) return;

        try
        {
            _player?.Pause();
            _player!.PlaybackSession.Position = TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"停止闹铃失败（已忽略）：{ex.Message}");
        }

        SetRinging(false);
    }

    private void SetRinging(bool value)
    {
        if (IsRinging == value) return;
        IsRinging = value;
        try
        {
            RingingChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"通知响铃状态失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>播放指定音频；path 为 null 时放系统提示音。会一直响到用户点「关闭铃声」或超过上限。</summary>
    public async Task PlayAsync(string? path)
    {
        if (_disposed) return;

        if (string.IsNullOrEmpty(path))
        {
            Beep();
            await RingForSystemBeepAsync();
            return;
        }

        try
        {
            var player = EnsurePlayer();

            // 换文件才重建音源；同一个文件反复响不用重新加载（省时间）
            if (!string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                player.Source = MediaSource.CreateFromUri(new Uri(path));
                _loadedPath = path;
            }

            // 循环播放：闹钟要一直响到人来关
            player.IsLoopingEnabled = true;
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();

            // 等一小会儿确认真的出声了；没出声（解码失败之类）就退回系统提示音
            await Task.Delay(900);
            if (_disposed) return;

            if (player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                _warn?.Invoke($"闹铃没有播放起来，改用系统提示音：{path}");
                _loadedPath = null;
                Beep();
                await RingForSystemBeepAsync();
                return;
            }

            SetRinging(true);

            // 最长响 MaxRing，到点自动停（用户中途点「关闭铃声」会提前跳出）
            var deadline = DateTimeOffset.UtcNow + MaxRing;
            while (!_disposed && IsRinging && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            if (IsRinging) StopRinging();
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"播放闹铃失败，改用系统提示音：{ex.Message}");
            Beep();
            await RingForSystemBeepAsync();
        }
    }

    /// <summary>系统提示音是一声就完，这里让「响铃中」状态维持几秒，好让关闭按钮有机会出现。</summary>
    private async Task RingForSystemBeepAsync()
    {
        SetRinging(true);
        await Task.Delay(TimeSpan.FromSeconds(3));
        SetRinging(false);
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is not null) return _player;

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Alerts,   // 当提醒音处理，别的应用在放歌时也能听到
            Volume = 1.0,
        };
        return _player;
    }

    /// <summary>系统「提示」音：不需要带音频文件，永远可用。</summary>
    private void Beep()
    {
        try
        {
            MessageBeep(0x00000040);   // MB_ICONASTERISK
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"系统提示音失败（已忽略）：{ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false)]
    private static extern bool MessageBeep(uint uType);

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _player?.Dispose();
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"释放闹铃播放器失败（已忽略）：{ex.Message}");
        }
        _player = null;
    }
}
