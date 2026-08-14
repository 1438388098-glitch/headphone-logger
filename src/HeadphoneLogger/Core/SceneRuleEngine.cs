namespace HeadphoneLogger.Core;

public interface ISceneRuleEngine
{
    /// <summary>按规则把前台进程/标题映射为场景。无规则命中 → 其他。绝不抛异常。</summary>
    Scene Map(string? appName, string? windowTitle);

    /// <summary>热更新规则集（明细「固定场景」写入后即时生效，无需重启）。</summary>
    void UpdateRules(IEnumerable<SceneRule> rules);
}

/// <summary>
/// 规则匹配：按规则顺序匹配第一条 enabled 规则。app_pattern 与 title_pattern 均为
/// 大小写不敏感的子串匹配；title_pattern 为空则只校验 app。无命中 → <see cref="Scene.Other"/>。
/// 进程名口径统一为**裸进程名**（无 .exe 后缀）：规则模式与输入在加载/比对时归一化，
/// 兼容旧库中的 "chrome.exe" 式规则。
/// </summary>
public sealed class SceneRuleEngine : ISceneRuleEngine
{
    private readonly object _gate = new();
    private IReadOnlyList<SceneRule> _rules;

    public SceneRuleEngine(IEnumerable<SceneRule> rules)
    {
        // 先按 Id 排序保证优先级，同 Id 时保持输入顺序（确定性）；再归一化规则模式。
        _rules = Normalize(rules);
    }

    public void UpdateRules(IEnumerable<SceneRule> rules)
    {
        var next = Normalize(rules);
        lock (_gate)
        {
            _rules = next;
        }
    }

    private static IReadOnlyList<SceneRule> Normalize(IEnumerable<SceneRule> rules)
    {
        return rules
            .Select((rule, index) => (rule, index))
            .OrderBy(x => x.rule.Id)
            .ThenBy(x => x.index)
            .Select(x => new SceneRule
            {
                Id = x.rule.Id,
                AppPattern = NormalizeProcessName(x.rule.AppPattern),
                TitlePattern = x.rule.TitlePattern,
                Scene = x.rule.Scene,
                Enabled = x.rule.Enabled,
            })
            .ToList();
    }

    public Scene Map(string? appName, string? windowTitle)
    {
        IReadOnlyList<SceneRule> rules;
        lock (_gate)
        {
            rules = _rules;
        }
        var app = appName is null ? null : NormalizeProcessName(appName);
        var title = windowTitle?.ToLowerInvariant();

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;

            var appOk = string.IsNullOrEmpty(rule.AppPattern)
                || (app?.Contains(rule.AppPattern, StringComparison.Ordinal) ?? false);

            var titleOk = string.IsNullOrEmpty(rule.TitlePattern)
                || (title?.Contains(rule.TitlePattern, StringComparison.OrdinalIgnoreCase) ?? false);

            if (appOk && titleOk)
                return rule.Scene;
        }

        return Scene.Other;
    }

    /// <summary>进程名归一化：去空白、转小写、去尾部 .exe 后缀。</summary>
    private static string NormalizeProcessName(string name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }
}
