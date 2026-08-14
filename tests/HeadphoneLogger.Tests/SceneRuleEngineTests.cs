using HeadphoneLogger.Core;

namespace HeadphoneLogger.Tests;

public class SceneRuleEngineTests
{
    private static SceneRuleEngine DefaultEngine() => new(SceneRuleSeed.DefaultRules());

    [Fact]
    public void AppLevelRule_MatchesProcessName()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Game, engine.Map("LeagueClient.exe", "召唤师峡谷 - 排位赛"));
    }

    [Fact]
    public void TitleRule_TakesPriorityOverAppFallback()
    {
        // chrome + bilibili → 视频；chrome 无站点 → 兜底其他
        var engine = DefaultEngine();
        Assert.Equal(Scene.Video, engine.Map("chrome.exe", "哔哩哔哩 (゜-゜)つロ 干杯~-bilibili"));
    }

    [Fact]
    public void AppRule_RequiresTitleMatchWhenSpecified()
    {
        // chrome 命中网易云标题 → 音乐
        var engine = DefaultEngine();
        Assert.Equal(Scene.Music, engine.Map("chrome.exe", "网易云音乐 - 我的歌单"));
    }

    [Fact]
    public void AppPattern_IsCaseInsensitive()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Coding, engine.Map("CODE.EXE", "main.cs - HeadphoneLogger"));
    }

    [Fact]
    public void TitlePattern_IsCaseInsensitive()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Video, engine.Map("chrome.exe", "YouTube - 翻唱"));
    }

    [Fact]
    public void UnknownApp_FallsBackToOther()
    {
        var engine = DefaultEngine();
        Assert.Equal(Scene.Other, engine.Map("explorer.exe", "文件夹"));
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
            new() { Id = 1, AppPattern = "steam.exe", Scene = Scene.Game, Enabled = false },
            new() { Id = 2, AppPattern = "steam.exe", Scene = Scene.Other },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Other, engine.Map("steam.exe", null));
    }

    [Fact]
    public void FirstMatchingEnabledRule_Wins()
    {
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "chrome.exe", TitlePattern = "youtube", Scene = Scene.Video },
            new() { Id = 2, AppPattern = "chrome.exe", Scene = Scene.Coding },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Video, engine.Map("chrome.exe", "YouTube - 首页"));
        Assert.Equal(Scene.Coding, engine.Map("chrome.exe", "Stack Overflow"));
    }

    [Fact]
    public void AppOnlyRule_MatchesAnyTitle()
    {
        var rules = new List<SceneRule>
        {
            new() { Id = 1, AppPattern = "notepad.exe", Scene = Scene.Other },
        };
        var engine = new SceneRuleEngine(rules);
        Assert.Equal(Scene.Other, engine.Map("notepad.exe", "任意标题"));
    }
}
