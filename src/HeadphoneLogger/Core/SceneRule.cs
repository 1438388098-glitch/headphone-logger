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
public static class SceneRuleSeed
{
    public static IReadOnlyList<SceneRule> DefaultRules() =>
    [
        // Chrome/Edge 站点级（标题命中优先于 app 级）
        new() { Id = 1, AppPattern = "chrome.exe", TitlePattern = "哔哩哔哩", Scene = Scene.Video },
        new() { Id = 2, AppPattern = "chrome.exe", TitlePattern = "bilibili", Scene = Scene.Video },
        new() { Id = 3, AppPattern = "chrome.exe", TitlePattern = "youtube", Scene = Scene.Video },
        new() { Id = 4, AppPattern = "chrome.exe", TitlePattern = "网易云音乐", Scene = Scene.Music },
        new() { Id = 5, AppPattern = "chrome.exe", TitlePattern = "爱奇艺", Scene = Scene.Video },
        new() { Id = 6, AppPattern = "chrome.exe", TitlePattern = "腾讯视频", Scene = Scene.Video },
        new() { Id = 7, AppPattern = "msedge.exe", TitlePattern = "哔哩哔哩", Scene = Scene.Video },
        new() { Id = 8, AppPattern = "msedge.exe", TitlePattern = "bilibili", Scene = Scene.Video },
        new() { Id = 9, AppPattern = "msedge.exe", TitlePattern = "youtube", Scene = Scene.Video },
        // 音乐客户端
        new() { Id = 10, AppPattern = "cloudmusic.exe", Scene = Scene.Music },
        new() { Id = 11, AppPattern = "qqmusic.exe", Scene = Scene.Music },
        new() { Id = 12, AppPattern = "spotify.exe", Scene = Scene.Music },
        // 本地/网页视频播放器
        new() { Id = 13, AppPattern = "potplayer.exe", Scene = Scene.Video },
        new() { Id = 14, AppPattern = "vlc.exe", Scene = Scene.Video },
        // 游戏
        new() { Id = 15, AppPattern = "steam.exe", Scene = Scene.Game },
        new() { Id = 16, AppPattern = "leagueclient.exe", Scene = Scene.Game },
        new() { Id = 17, AppPattern = "valorant.exe", Scene = Scene.Game },
        // 编程
        new() { Id = 18, AppPattern = "code.exe", Scene = Scene.Coding },
        new() { Id = 19, AppPattern = "idea64.exe", Scene = Scene.Coding },
        new() { Id = 20, AppPattern = "pycharm64.exe", Scene = Scene.Coding },
        new() { Id = 21, AppPattern = "rider64.exe", Scene = Scene.Coding },
        // 会议
        new() { Id = 22, AppPattern = "wechat.exe", Scene = Scene.Meeting },
        new() { Id = 23, AppPattern = "wemeet.exe", Scene = Scene.Meeting },
        new() { Id = 24, AppPattern = "zoom.exe", Scene = Scene.Meeting },
        // 网课
        new() { Id = 25, AppPattern = "学习通", Scene = Scene.Course },
        new() { Id = 26, AppPattern = "zhihuishu", Scene = Scene.Course },
    ];
}
