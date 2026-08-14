using System.Diagnostics;
using System.Runtime.InteropServices;
using HeadphoneLogger.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HeadphoneLogger.Audio;

/// <summary>
/// NAudio 实现：监听默认渲染端点变化（耳机插入/拔出/换设备），
/// Core Audio COM 枚举当前发声会话。
///
/// 设备识别口径（见 spec）：默认渲染端点变化即视为「换耳机」。
/// 事件在线程池线程（COM 回调）触发，装配层负责串行化到 UI 线程。
/// </summary>
public sealed class NAudioDeviceMonitor : IAudioDeviceMonitor
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private NotificationClient? _client;
    private string? _currentDeviceId;
    private DeviceInfo? _currentDevice;
    private int _refreshing;
    private bool _disposed;

    public event EventHandler<DeviceInfo>? DeviceInserted;
    public event EventHandler? DeviceRemoved;

    public bool IsHeadphonesPresent => _currentDevice is not null;
    public DeviceInfo? CurrentDevice => _currentDevice;

    public void Start()
    {
        _client = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
        RefreshDefaultDevice(); // 记录基准状态，不产生虚假会话
    }

    public IReadOnlyList<string> GetSoundingProcessNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint((int)DataFlow.Render, (int)Role.Multimedia, out var device);
            if (hr != 0 || device is null)
                return result.ToList();

            try
            {
                var iid = CoreAudioInterop.IAudioSessionManager2;
                hr = device.Activate(ref iid, CoreAudioInterop.CLSCTX_ALL, IntPtr.Zero, out var managerObj);
                if (hr != 0 || managerObj is null)
                    return result.ToList();
                var manager = (IAudioSessionManager2)managerObj;
                try
                {
                    hr = manager.GetSessionEnumerator(out var sessionEnum);
                    if (hr != 0 || sessionEnum is null)
                        return result.ToList();
                    try
                    {
                        sessionEnum.GetCount(out var count);
                        for (var i = 0; i < count; i++)
                        {
                            if (sessionEnum.GetSession(i, out var session) != 0 || session is null)
                                continue;
                            try
                            {
                                if (session.GetState(out var state) == 0 &&
                                    state == (int)ComAudioSessionState.Active &&
                                    session.GetProcessId(out var pid) == 0 && pid > 0)
                                {
                                    var name = SafeProcessName(pid);
                                    if (!string.IsNullOrEmpty(name))
                                        result.Add(name);
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(session);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessionEnum);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            // 音频子系统异常绝不外抛：本次采样返回空集合即可
        }
        return result.ToList();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_client is not null)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(_client);
            }
            catch
            {
                // 反注册失败不影响退出
            }
            _client = null;
        }
        _enumerator.Dispose();
    }

    private void OnAudioNotification()
    {
        // 高频通知去抖：进行中则跳过，下一次通知会补上（以设备 ID 比较收敛）
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            RefreshDefaultDevice();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void RefreshDefaultDevice()
    {
        DeviceInfo? next;
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var id = device.ID;
            next = new DeviceInfo(id, ReadFriendlyName(device) ?? id);
        }
        catch
        {
            // E_NOTFOUND（无默认渲染端点）等情况 → 视为无设备
            next = null;
        }

        if (next?.DeviceId == _currentDeviceId)
            return; // 无变化（含都为空）

        var old = _currentDevice;
        _currentDevice = next;
        _currentDeviceId = next?.DeviceId;

        if (old is not null)
            DeviceRemoved?.Invoke(this, EventArgs.Empty);
        if (next is not null)
            DeviceInserted?.Invoke(this, next);
    }

    private static string? ReadFriendlyName(MMDevice device)
    {
        try
        {
            if (device.Properties[PropertyKeys.PKEY_Device_FriendlyName].Value is string name && name.Length > 0)
                return name;
        }
        catch
        {
            // 属性缺失走兜底
        }
        try
        {
            return device.DeviceFriendlyName;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null; // 权限不足或进程已退出
        }
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly NAudioDeviceMonitor _owner;

        public NotificationClient(NAudioDeviceMonitor owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnAudioNotification();
        public void OnDeviceAdded(string deviceId) => _owner.OnAudioNotification();
        public void OnDeviceRemoved(string deviceId) => _owner.OnAudioNotification();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.OnAudioNotification();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => _owner.OnAudioNotification();
    }
}
