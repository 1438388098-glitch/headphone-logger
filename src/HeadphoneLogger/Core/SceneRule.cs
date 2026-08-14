namespace HeadphoneLogger.Core;

/// <summary>场景映射规则：app 命中 + title 可选命中 → scene。</summary>
public sealed class SceneRule
{
    public long Id { get; set; }
    public required string AppPattern { get; set; }
    public string? TitlePattern { get; set; }
    public Scene Scene { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>初始规则种子。顺序即优先级：站点级（带标题）先于 app 级，最后无规则命中 → 其他。</summary>
/// <remarks>进程名一律用**裸进程名**（不带 .exe，与 <c>Process.ProcessName</c> 口径一致）。
/// 引擎加载时会归一化（兼容旧库中带 .exe 的规则），故两种写法都能命中。</remarks>
public static class SceneRuleSeed
{
    public static IReadOnlyList<SceneRule> DefaultRules() =>
    [
        // Chrome/Edge 站点级（标题命中优先于 app 级）
        new() { Id = 1, AppPattern = "chrome", TitlePattern = "哔哩哔哩", Scene = Scene.Video },
        new() { Id = 2, AppPattern = "chrome", TitlePattern = "bilibili", Scene = Scene.Video },
        new() { Id = 3, AppPattern = "chrome", TitlePattern = "youtube", Scene = Scene.Video },
        new() { Id = 4, AppPattern = "chrome", TitlePattern = "网易云音乐", Scene = Scene.Music },
        new() { Id = 5, AppPattern = "chrome", TitlePattern = "爱奇艺", Scene = Scene.Video },
        new() { Id = 6, AppPattern = "chrome", TitlePattern = "腾讯视频", Scene = Scene.Video },
        new() { Id = 7, AppPattern = "msedge", TitlePattern = "哔哩哔哩", Scene = Scene.Video },
        new() { Id = 8, AppPattern = "msedge", TitlePattern = "bilibili", Scene = Scene.Video },
        new() { Id = 9, AppPattern = "msedge", TitlePattern = "youtube", Scene = Scene.Video },
        // 音乐客户端
        new() { Id = 10, AppPattern = "cloudmusic", Scene = Scene.Music },
        new() { Id = 11, AppPattern = "qqmusic", Scene = Scene.Music },
        new() { Id = 12, AppPattern = "spotify", Scene = Scene.Music },
        // 本地/网页视频播放器
        new() { Id = 13, AppPattern = "potplayer", Scene = Scene.Video },
        new() { Id = 14, AppPattern = "vlc", Scene = Scene.Video },
        // 游戏
        new() { Id = 15, AppPattern = "steam", Scene = Scene.Game },
        new() { Id = 16, AppPattern = "leagueclient", Scene = Scene.Game },
        new() { Id = 17, AppPattern = "valorant", Scene = Scene.Game },
        // 编程
        new() { Id = 18, AppPattern = "code", Scene = Scene.Coding },
        new() { Id = 19, AppPattern = "idea64", Scene = Scene.Coding },
        new() { Id = 20, AppPattern = "pycharm64", Scene = Scene.Coding },
        new() { Id = 21, AppPattern = "rider64", Scene = Scene.Coding },
        // 会议
        new() { Id = 22, AppPattern = "wechat", Scene = Scene.Meeting },
        new() { Id = 23, AppPattern = "wemeet", Scene = Scene.Meeting },
        new() { Id = 24, AppPattern = "zoom", Scene = Scene.Meeting },
        // 网课
        new() { Id = 25, AppPattern = "学习通", Scene = Scene.Course },
        new() { Id = 26, AppPattern = "zhihuishu", Scene = Scene.Course },
    ];
}
