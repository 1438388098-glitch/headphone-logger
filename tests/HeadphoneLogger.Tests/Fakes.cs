using HeadphoneLogger.Audio;
using HeadphoneLogger.Core;

namespace HeadphoneLogger.Tests;

/// <summary>假音频设备监听：手动触发插拔，可注入发声进程。</summary>
public sealed class FakeMonitor : IAudioDeviceMonitor
{
    public bool IsHeadphonesPresent { get; set; }
    public DeviceInfo? CurrentDevice { get; set; }
    public List<string> SoundingProcesses { get; } = [];

    public event EventHandler<DeviceInfo>? DeviceInserted;
    public event EventHandler? DeviceRemoved;

    public IReadOnlyList<string> GetSoundingProcessNames() => SoundingProcesses;

    public void Start() { }
    public void Dispose() { }
}

/// <summary>假前台扫描器：手动触发前台变化。</summary>
public sealed class FakeScanner : IForegroundScanner
{
    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;
    public ForegroundInfo Current { get; set; } = new(null, null);

    public void Start() { }
    public void Dispose() { }
}

/// <summary>可拨动的时钟，让状态机测试时间确定。</summary>
public sealed class MutableClock
{
    public DateTimeOffset Now { get; set; } = new(new DateTime(2026, 8, 14, 9, 0, 0));

    public void Advance(TimeSpan d) => Now += d;
}
