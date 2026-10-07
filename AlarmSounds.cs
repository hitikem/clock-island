using System;
using System.Collections.Generic;
using System.IO;

namespace PomodoroIsland;

/// <summary>
/// 到点提醒音：内置了几家手机厂商的默认闹铃风格（小米「白日梦」、vivo / OPPO「新世界」、苹果「雷达」），
/// 还能让用户自己放音频文件进来。设置里选一个即可。
///
/// 播放策略（从好到坏依次退）：
///   1. Windows.Media.Playback.MediaPlayer —— 能播 .ogg / .mp3 / .wav / .m4a，音质和时长都完整；
///   2. 退回 MessageBeep —— 系统提示音，永远能响（至少不会一点声音都没有）。
/// </summary>
internal static class AlarmSounds
{
    /// <summary>用户自己放进来的音频文件夹（插件目录下）。</summary>
    public const string UserFolderName = "alarms";

    /// <summary>一个可选闹铃：Id 存进设置，文件是实际播放的音频。</summary>
    public sealed record Alarm(string Id, string Title, string FileName, string Note);

    /// <summary>
    /// 内置闹铃清单。文件随插件带（放插件目录根下）。
    ///
    /// 说明：各厂商的默认闹铃音频属于各自的版权素材，仓库里不附带、也不从网上抓；
    /// 想用哪家的铃，把音频文件放到插件目录的 alarms\ 里，就会出现在下面的自定义列表里。
    /// </summary>
    public static readonly IReadOnlyList<Alarm> BuiltIn = new List<Alarm>
    {
        new("xiaomi-daydream", "小米 · 白日梦",   "xiaomi-daydream.ogg", "小米手机默认闹铃"),
        new("vivo-alarm",      "vivo · 闹钟铃声", "vivo-alarm.m4a",      "vivo 手机闹钟铃声"),
        new("apple-radar",     "苹果 · 雷达",     "apple-radar.m4a",     "iPhone 默认闹铃"),
        new("system",          "系统提示音",       string.Empty,          "Windows 自带，最轻量"),
    };

    /// <summary>自定义项在设置里的 Id 前缀：custom:&lt;文件名&gt;。</summary>
    public const string CustomPrefix = "custom:";

    public const string DefaultId = "xiaomi-daydream";

    /// <summary>把 Id 翻成标题（设置页和界面提示用）。</summary>
    public static string TitleOf(string? id)
    {
        foreach (var alarm in BuiltIn)
        {
            if (string.Equals(alarm.Id, id, StringComparison.OrdinalIgnoreCase)) return alarm.Title;
        }
        return BuiltIn[0].Title;
    }

    /// <summary>闹铃 Id → 实际音频路径。找不到就返回 null（调用方回退系统提示音）。</summary>
    public static string? Resolve(string pluginDirectory, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        // 自定义：custom:<文件名>，文件放在插件目录的 alarms\ 里
        if (id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = id[CustomPrefix.Length..];
            if (string.IsNullOrWhiteSpace(name)) return null;

            // 只认文件名，防止 ../ 之类的路径跑出目录
            name = Path.GetFileName(name);
            var userPath = Path.Combine(pluginDirectory, UserFolderName, name);
            return File.Exists(userPath) ? userPath : null;
        }

        foreach (var alarm in BuiltIn)
        {
            if (!string.Equals(alarm.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(alarm.FileName)) return null;   // 「系统提示音」故意没有文件

            var path = Path.Combine(pluginDirectory, alarm.FileName);
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    /// <summary>列出所有可选闹铃：内置 + 用户放进 alarms\ 的，Id 可直接存进设置。</summary>
    public static IReadOnlyList<(string Id, string Title, string? Hint)> Options(
        string pluginDirectory, Action<string>? warn = null)
    {
        var list = new List<(string, string, string?)>();
        foreach (var alarm in BuiltIn)
        {
            // 内置铃的文件不在就别列出来（比如还没放 vivo 的音频），免得选了没声音
            var ready = string.IsNullOrEmpty(alarm.FileName) || File.Exists(Path.Combine(pluginDirectory, alarm.FileName));
            if (ready) list.Add((alarm.Id, alarm.Title, alarm.Note));
        }

        foreach (var path in UserFiles(pluginDirectory, warn))
        {
            var name = Path.GetFileName(path);
            list.Add((CustomPrefix + name, $"自定义 · {Path.GetFileNameWithoutExtension(name)}", name));
        }
        return list;
    }

    /// <summary>用户放进 alarms\ 的自定义音频（.wav/.mp3/.ogg/.m4a/.wma）。</summary>
    public static IReadOnlyList<string> UserFiles(string pluginDirectory, Action<string>? warn = null)
    {
        var list = new List<string>();
        try
        {
            var dir = Path.Combine(pluginDirectory, UserFolderName);
            if (!Directory.Exists(dir)) return list;

            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".wav" or ".mp3" or ".ogg" or ".m4a" or ".wma" or ".aac" or ".flac")
                {
                    list.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"扫描自定义闹铃失败（已忽略）：{ex.Message}");
        }
        return list;
    }
}
