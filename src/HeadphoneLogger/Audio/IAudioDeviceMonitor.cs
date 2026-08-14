using HeadphoneLogger.Core;

namespace HeadphoneLogger.Audio;

/// <summary>
/// 音频设备监听抽象：插拔事件 + 当前设备 + 发声进程集合。
/// NAudio 为实现之一；测试用假实现驱动状态机。
/// </summary>
public interface IAudioDeviceMonitor : IDisposable
{
    event EventHandler<DeviceInfo>? DeviceInserted;
    event EventHandler? DeviceRemoved;

    /// <summary>启动时的基准状态：耳机当前是否在场。</summary>
    bool IsHeadphonesPresent { get; }

    /// <summary>当前默认渲染设备（用于会话归属到具体耳机）。</summary>
    DeviceInfo? CurrentDevice { get; }

    /// <summary>当前正在发声的进程名集合（小写，供并发场景采样）。</summary>
    IReadOnlyList<string> GetSoundingProcessNames();

    void Start();
}
