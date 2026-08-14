namespace HeadphoneLogger.Core;

/// <summary>使用场景枚举。数据库中以枚举名（英文）存储，展示用中文。</summary>
public enum Scene
{
    Music = 0,   // 音乐
    Video = 1,   // 视频
    Game = 2,    // 游戏
    Course = 3,  // 网课
    Meeting = 4, // 会议
    Coding = 5,  // 编程
    Other = 6,   // 其他
    Unmarked = 7 // 未标注
}

public static class SceneExtensions
{
    public static string DisplayName(this Scene scene) => scene switch
    {
        Scene.Music => "音乐",
        Scene.Video => "视频",
        Scene.Game => "游戏",
        Scene.Course => "网课",
        Scene.Meeting => "会议",
        Scene.Coding => "编程",
        Scene.Other => "其他",
        Scene.Unmarked => "未标注",
        _ => scene.ToString(),
    };
}
