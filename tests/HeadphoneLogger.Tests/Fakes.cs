using HeadphoneLogger.Audio;
using HeadphoneLogger.Core;

namespace HeadphoneLogger.Tests;

// 假实现的接口事件仅用于满足 IAudioDeviceMonitor/IForegroundScanner 契约；
// 测试通过 SessionManager.Notify* 显式驱动状态机，故事件字段不会被订阅/触发。
#pragma warning disable CS0067 // 事件已声明但从未使用

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
    public bool Active { get; private set; } = true;

    public void Start() => Active = true;
    public void SetActive(bool active) => Active = active;
    public void Dispose() { }
}

#pragma warning restore CS0067

/// <summary>可拨动的时钟，让状态机测试时间确定。</summary>
public sealed class MutableClock
{
    public DateTimeOffset Now { get; set; } = new(new DateTime(2026, 8, 14, 9, 0, 0));

    public void Advance(TimeSpan d) => Now += d;
}
