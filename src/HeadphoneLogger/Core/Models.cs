namespace HeadphoneLogger.Core;

/// <summary>一次耳机插拔 = 一个会话。记录具体是哪个耳机（默认渲染端点）。</summary>
public sealed class Session
{
    public long Id { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public long? DurationSec { get; set; }
}

/// <summary>音频设备标识：端点 ID（稳定分组键）+ 友好名（展示）。</summary>
public sealed record DeviceInfo(string DeviceId, string DeviceName);

/// <summary>会话内的场景段。主场景存 <see cref="Scene"/>，并发标签在 segment_scenes 表。</summary>
public sealed class Segment
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public long? DurationSec { get; set; }
    public string? AppName { get; set; }
    public string? WindowTitle { get; set; }
    public Scene Scene { get; set; } = Scene.Unmarked;
    public bool Confirmed { get; set; }
    public string? Note { get; set; }
}

/// <summary>气泡确认时展示的预填草案。</summary>
public sealed class SceneDraft
{
    public required Scene MainScene { get; init; }
    public required IReadOnlySet<Scene> ConcurrentScenes { get; init; }
    public required string? AppName { get; init; }
    public required string? WindowTitle { get; init; }
}

/// <summary>前台窗口信息。</summary>
public sealed record ForegroundInfo(string? ProcessName, string? WindowTitle);
