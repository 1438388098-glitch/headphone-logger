using HeadphoneLogger.Core;

namespace HeadphoneLogger.Tests;

public class SceneRuleEngineTests
{
    private static SceneRuleEngine DefaultEngine() => new(SceneRuleSeed.DefaultRules());

    [Fact]
    public void AppLevelRule_MatchesProcessName()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Game, engine.Map("LeagueClient", "召唤师峡谷 - 排位赛"));
    }

    [Fact]
    public void TitleRule_TakesPriorityOverAppFallback()
    {
        // chrome + bilibili → 视频；chrome 无站点 → 兜底其他
        var engine = DefaultEngine();
        Assert.Equal(Scene.Video, engine.Map("chrome", "哔哩哔哩 (゜-゜)つロ 干杯~-bilibili"));
    }

    [Fact]
    public void AppRule_RequiresTitleMatchWhenSpecified()
    {
        // chrome 命中网易云标题 → 音乐
        var engine = DefaultEngine();
        Assert.Equal(Scene.Music, engine.Map("chrome", "网易云音乐 - 我的歌单"));
    }

    [Fact]
    public void AppPattern_IsCaseInsensitive()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Coding, engine.Map("CODE", "main.cs - HeadphoneLogger"));
    }

    [Fact]
    public void TitlePattern_IsCaseInsensitive()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Video, engine.Map("chrome", "YouTube - 翻唱"));
    }

    [Fact]
    public void UnknownApp_FallsBackToOther()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Other, engine.Map("explorer", "文件夹"));
    }

    [Fact]
    public void NullInput_FallsBackToOther_WithoutThrowing()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Other, engine.Map(null, null));
    }

    [Fact]
    public void DisabledRule_IsSkipped()
    {
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "steam", Scene = Scene.Game, Enabled = false },
            new() { Id = 2, AppPattern = "steam", Scene = Scene.Other },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Other, engine.Map("steam", null));
    }

    [Fact]
    public void FirstMatchingEnabledRule_Wins()
    {
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "chrome", TitlePattern = "youtube", Scene = Scene.Video },
            new() { Id = 2, AppPattern = "chrome", Scene = Scene.Coding },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Video, engine.Map("chrome", "YouTube - 首页"));
        Assert.Equal(Scene.Coding, engine.Map("chrome", "Stack Overflow"));
    }

    [Fact]
    public void AppOnlyRule_MatchesAnyTitle()
    {
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "notepad", Scene = Scene.Other },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Other, engine.Map("notepad", "任意标题"));
    }

    [Fact]
    public void BareProcessName_MatchesSeedRules()
    {
        // 生产口径：Process.ProcessName 返回裸进程名（无 .exe）——回归真实运行时场景识别
        var engine = DefaultEngine();
        Assert.Equal(Scene.Video, engine.Map("chrome", "哔哩哔哩 - bilibili"));
        Assert.Equal(Scene.Game, engine.Map("steam", "CS2"));
        Assert.Equal(Scene.Coding, engine.Map("Code", "Program.cs"));
        Assert.Equal(Scene.Music, engine.Map("cloudmusic", null));
    }

    [Fact]
    public void ExeSuffixedInput_IsNormalized()
    {
        // 兼容旧库规则/带后缀输入：.exe 后缀应被归一化后命中
        var engine = DefaultEngine();
        Assert.Equal(Scene.Game, engine.Map("steam.exe", "CS2"));
        Assert.Equal(Scene.Video, engine.Map("chrome.exe", "哔哩哔哩 - bilibili"));
    }

    [Fact]
    public void ExeSuffixedRule_IsNormalized()
    {
        // 旧库可能存的是 "chrome.exe" 式规则：引擎加载时应归一化后再匹配
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "chrome.exe", TitlePattern = "bilibili", Scene = Scene.Video },
            new() { Id = 2, AppPattern = "steam.exe", Scene = Scene.Game },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Video, engine.Map("chrome", "哔哩哔哩 - bilibili"));
        Assert.Equal(Scene.Game, engine.Map("steam", "CS2"));
    }

    [Fact]
    public void UpdateRules_HotReloads()
    {
        // 明细「固定场景」写入规则后引擎热更新，无需重启立即生效
        var engine = DefaultEngine();
        Assert.Equal(Scene.Other, engine.Map("myapp", "窗口标题"));

        var rules = SceneRuleSeed.DefaultRules().ToList();
        rules.Add(new SceneRule { Id = 999, AppPattern = "myapp", Scene = Scene.Coding });
        engine.UpdateRules(rules);

        Assert.Equal(Scene.Coding, engine.Map("myapp", "窗口标题"));
    }
}
