namespace HeadphoneLogger.Core;

public interface IForegroundScanner : IDisposable
{
    /// <summary>前台进程/窗口变化（去抖后）。是否算场景切换由 SessionManager 判定。</summary>
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    /// <summary>最近一次采样的前台信息（插入时用于首段预填）。</summary>
    ForegroundInfo Current { get; }

    void Start();
}

public sealed class ForegroundChangedEventArgs : EventArgs
{
    public required ForegroundInfo Foreground { get; init; }
}
