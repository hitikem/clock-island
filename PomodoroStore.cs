using System.IO;
using System.Text.Json;

namespace PomodoroIsland;

/// <summary>一次专注的记录，用于聚光卡里的「最近记录」。</summary>
public sealed record PomodoroRecord(DateTimeOffset FinishedAt, int Minutes, bool Completed);

/// <summary>落盘的数据：今日统计 + 周期进度 + 最近记录。</summary>
public sealed class PomodoroData
{
    public string Date { get; set; } = "";
    public int CompletedToday { get; set; }
    public int FocusSecondsToday { get; set; }
    public int FocusesInCycle { get; set; }
    public List<PomodoroRecord> Recent { get; set; } = new();
}

/// <summary>
/// 统计持久化：写插件自己目录下的 stats.json，重启 WinIsland 后今日数据不丢。
/// 跨天（存的日期 != 今天）时今日数据与最近记录一起清零。
/// </summary>
public sealed class PomodoroStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Action<string> _warn;

    public PomodoroStore(string pluginDirectory, Action<string> warn)
    {
        _path = Path.Combine(pluginDirectory, "stats.json");
        _warn = warn;
    }

    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    public PomodoroData Load()
    {
        PomodoroData? data = null;
        try
        {
            if (File.Exists(_path))
            {
                data = JsonSerializer.Deserialize<PomodoroData>(File.ReadAllText(_path), Options);
            }
        }
        catch (Exception ex)
        {
            _warn($"读取统计文件失败（按空数据继续）：{ex.Message}");
        }

        if (data is null || !string.Equals(data.Date, Today(), StringComparison.Ordinal))
        {
            // 跨天或第一次运行：今日数据清零（最近记录也一起清，保持「今天的记录」语义）
            return new PomodoroData { Date = Today() };
        }

        data.Recent ??= new List<PomodoroRecord>();
        return data;
    }

    public void Save(PomodoroData data)
    {
        try
        {
            data.Date = Today();
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(data, Options));
        }
        catch (Exception ex)
        {
            _warn($"保存统计失败：{ex.Message}");
        }
    }
}
