namespace HeadphoneLogger.Core;

public interface ISceneRuleEngine
{
    /// <summary>按规则把前台进程/标题映射为场景。无规则命中 → 其他。绝不抛异常。</summary>
    Scene Map(string? appName, string? windowTitle);
}

/// <summary>
/// 规则匹配：按规则顺序匹配第一条 enabled 规则。app_pattern 与 title_pattern 均为
/// 大小写不敏感的子串匹配；title_pattern 为空则只校验 app。无命中 → <see cref="Scene.Other"/>。
/// </summary>
public sealed class SceneRuleEngine : ISceneRuleEngine
{
    private readonly IReadOnlyList<SceneRule> _rules;

    public SceneRuleEngine(IEnumerable<SceneRule> rules)
    {
        _rules = rules.OrderBy(r => r.Id).ToList();
    }

    public Scene Map(string? appName, string? windowTitle)
    {
        var app = appName?.ToLowerInvariant();
        var title = windowTitle?.ToLowerInvariant();

        foreach (var rule in _rules)
        {
            if (!rule.Enabled)
                continue;

            var appOk = string.IsNullOrEmpty(rule.AppPattern)
                || (app?.Contains(rule.AppPattern, StringComparison.OrdinalIgnoreCase) ?? false);

            var titleOk = string.IsNullOrEmpty(rule.TitlePattern)
                || (title?.Contains(rule.TitlePattern, StringComparison.OrdinalIgnoreCase) ?? false);

            if (appOk && titleOk)
                return rule.Scene;
        }

        return Scene.Other;
    }
}
