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
    private bool _baselineDone; // 基准状态已记录：之后的事件才对外派发
    private bool _disposed;

    // 发声枚举器复用：每次 new 会产生 RCW + GC 压力，2s 采样下改为复用单例
    private IMMDeviceEnumerator? _soundingEnumerator;
    private readonly Dictionary<int, (string Name, DateTime Until)> _pidCache = [];
    private static readonly TimeSpan PidCacheTtl = TimeSpan.FromSeconds(10);

    // 拔除去抖：瞬时音频故障（休眠唤醒、驱动重置）会让默认端点短暂跳到扬声器再跳回耳机。
    // 若在窗口期内回到同一设备 ID，视为抖动取消，不切开会话。Timer 在 UI 线程（构造于主线程）创建。
    private static readonly TimeSpan RemovalDebounce = TimeSpan.FromSeconds(2.5);
    private readonly System.Windows.Forms.Timer _removalDebounceTimer;
    private string? _debouncedDeviceId;   // 等待确认的旧设备 ID（尚未派发 DeviceRemoved）

    public event EventHandler<DeviceInfo>? DeviceInserted;
    public event EventHandler? DeviceRemoved;

    public bool IsHeadphonesPresent => _currentDevice is not null;
    public DeviceInfo? CurrentDevice => _currentDevice;

    public NAudioDeviceMonitor()
    {
        _removalDebounceTimer = new System.Windows.Forms.Timer { Interval = (int)RemovalDebounce.TotalMilliseconds };
        _removalDebounceTimer.Tick += (_, _) => ConfirmRemovalDebounce();
        // 构造函数在 UI 线程（Program.Main）执行：主动触发一次创建本机句柄，
        // 之后 COM 线程 Start/Stop 才会投递到 UI 线程的消息泵（否则句柄建在无泵线程，Tick 永不触发）
        _removalDebounceTimer.Start();
        _removalDebounceTimer.Stop();
    }

    public void Start()
    {
        _client = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
        // 基准状态静默记录：不产生虚假 DeviceInserted 会话
        RefreshDefaultDevice();
        _baselineDone = true;
    }

    public IReadOnlyList<string> GetSoundingProcessNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 复用单例枚举器，避免每 2 秒新建 COM 对象（RCW 由 GC 延迟回收，产生 Gen2 压力）
            _soundingEnumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = _soundingEnumerator.GetDefaultAudioEndpoint((int)DataFlow.Render, (int)Role.Multimedia, out var device);
            if (hr != 0 || device is null)
                return result.ToList();

            try
            {
                var iid = CoreAudioInterop.IAudioSessionManager2;
                // Activate 直接 out IAudioSessionManager2，避免 out object + 强转产生双 RCW
                hr = device.Activate(ref iid, CoreAudioInterop.CLSCTX_ALL, IntPtr.Zero, out var manager);
                if (hr != 0 || manager is null)
                    return result.ToList();
                try
                {
                    hr = manager.GetSessionEnumerator(out var sessionEnum);
                    if (hr != 0 || sessionEnum is null)
                        return result.ToList();
                    try
                    {
                        if (sessionEnum.GetCount(out var count) != 0)
                            return result.ToList();
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
                                    // 排除自身进程（避免把本程序的声音会话算进并发场景）
                                    if (pid == Environment.ProcessId)
                                        continue;
                                    var name = SafeProcessName(pid);
                                    if (!string.IsNullOrEmpty(name))
                                        result.Add(name);
                                }
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(session);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(sessionEnum);
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(manager);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }
        catch
        {
            // 音频子系统异常绝不外抛：本次采样返回空集合即可
            // 枚举器可能因设备刷新而失效，重建一次
            if (_soundingEnumerator is not null)
            {
                Marshal.FinalReleaseComObject(_soundingEnumerator);
                _soundingEnumerator = null;
            }
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
        _removalDebounceTimer.Stop();
        _removalDebounceTimer.Dispose();
        _enumerator.Dispose();
        if (_soundingEnumerator is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_soundingEnumerator);
            }
            catch
            {
                // 枚举器释放失败不影响退出
            }
            _soundingEnumerator = null;
        }
    }

    private void OnAudioNotification()
    {
        if (_disposed)
            return;
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
        if (_disposed)
            return;

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

        // 基准态（Start 首次刷新）：静默记录状态，不派发事件、不进入去抖
        if (!_baselineDone)
        {
            _currentDevice = next;
            _currentDeviceId = next?.DeviceId;
            return;
        }

        // 运行中设备变化：不立即切换，进入去抖窗口。窗口内回到同一设备 ID → 取消（休眠唤醒/驱动重置抖动）。
        // _currentDevice 保持原状，确认后才更新并派发事件。
        _debouncedDeviceId = _currentDeviceId;
        _removalDebounceTimer.Stop();
        _removalDebounceTimer.Start();
    }

    /// <summary>去抖窗口结束：重新采样当前默认端点，确认设备是否真的变了。</summary>
    private void ConfirmRemovalDebounce()
    {
        _removalDebounceTimer.Stop();
        if (_disposed)
            return;

        // 用独立临时枚举器复查（不触碰通知用的共享枚举器，避免跨线程 COM 并发）
        DeviceInfo? current = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint((int)DataFlow.Render, (int)Role.Multimedia, out var device);
                if (hr == 0 && device is not null)
                {
                    try
                    {
                        if (device.GetId(out var id) == 0)
                            current = new DeviceInfo(id, id);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }
        catch
        {
            // 音频子系统不可用：视为无设备
        }

        // 回到原设备：抖动，取消，不派发任何事件
        if (_debouncedDeviceId is not null && current?.DeviceId == _debouncedDeviceId)
        {
            _debouncedDeviceId = null;
            return;
        }

        var old = _currentDevice;
        _currentDevice = current;
        _currentDeviceId = current?.DeviceId;
        _debouncedDeviceId = null;

        if (!_baselineDone)
            return;
        if (old is not null)
            DeviceRemoved?.Invoke(this, EventArgs.Empty);
        if (current is not null)
            DeviceInserted?.Invoke(this, current);
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

    /// <summary>进程名小写（与接口契约一致：GetSoundingProcessNames 返回小写）。带 10s TTL 缓存，
    /// 避免每 2 秒采样对每个活跃会话做一次 Process.GetProcessById（开销最大的环节）。</summary>
    private string? SafeProcessName(int pid)
    {
        var now = DateTime.UtcNow;
        if (_pidCache.Count > 256)
        {
            // 缓存膨胀防护：清理过期项（短 TTL 下很少触发）
            foreach (var key in _pidCache.Keys.Where(k => _pidCache[k].Until <= now).ToList())
                _pidCache.Remove(key);
        }
        if (_pidCache.TryGetValue(pid, out var entry) && entry.Until > now)
            return entry.Name;
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName.ToLowerInvariant();
            _pidCache[pid] = (name, now + PidCacheTtl);
            return name;
        }
        catch
        {
            _pidCache[pid] = (null!, now + PidCacheTtl); // 进程已退出/权限不足：短缓存失败结果，避免反复重试
            return null;
        }
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly NAudioDeviceMonitor _owner;

        public NotificationClient(NAudioDeviceMonitor owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnAudioNotification();
        public void OnDeviceAdded(string deviceId) => _owner.OnAudioNotification();
        public void OnDeviceRemoved(string deviceId) => _owner.OnAudioNotification();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // 只关心渲染端点的默认切换（含通信角色耳机接入）；其余 flow 不影响本应用状态。
            // RefreshDefaultDevice 内部按设备 ID 去重，多余触发只是空转。
            if (flow == DataFlow.Render)
                _owner.OnAudioNotification();
        }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => _owner.OnAudioNotification();
    }
}
