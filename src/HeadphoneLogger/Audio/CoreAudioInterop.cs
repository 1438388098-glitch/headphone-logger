using System.Runtime.InteropServices;

namespace HeadphoneLogger.Audio;

/// <summary>Core Audio COM 互操作定义（用于发声进程枚举）。插拔监听走 NAudio 的通知回调。</summary>
internal static class CoreAudioInterop
{
    /// <summary>CLSCTX_ALL = INPROC_SERVER|INPROC_HANDLER|LOCAL_SERVER|REMOTE_SERVER</summary>
    public const int CLSCTX_ALL = 0x17;

    public static readonly Guid IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
}

/// <summary>MMDeviceEnumerator 的 COM 激活器。</summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // 直接 out 目标接口：由 marshaler 做 QI 并只产生一个 RCW，避免 out object + 强转的双 RCW
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IAudioSessionManager2 ppInterface);
    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr propertyStore);
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);
    [PreserveSig]
    int Item(int index, out IMMDevice device);
}

/// <summary>
/// IAudioSessionManager2（含继承的 IAudioSessionManager 两个方法）。
/// vtable 顺序：IUnknown(3) + GetAudioSessionControl + GetSimpleAudioVolume + 5 个扩展方法。
/// </summary>
[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig]
    int RegisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig]
    int UnregisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
    [PreserveSig]
    int UnregisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);
    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl2 session);
}

/// <summary>
/// IAudioSessionControl2（含继承的 IAudioSessionControl 八个方法）。
/// 只用 GetState（槽 4）与 GetProcessId（槽 12），其余占位保证 vtable 对齐。
/// </summary>
[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);
    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig]
    int GetGroupingParam(out Guid grouping);
    [PreserveSig]
    int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig]
    int GetProcessId(out int pid);
    [PreserveSig]
    int IsSystemSoundsSession();
    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

/// <summary>AudioSessionState：Inactive/Active/Expired。</summary>
internal enum ComAudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}
