using System.Text.RegularExpressions;

namespace DownloadManager.Core;

/// <summary>
/// Minimal robots.txt evaluator (RFC 9309 subset): picks the most specific matching
/// user-agent group, falls back to <c>*</c>, and applies longest-match Allow/Disallow
/// with <c>*</c> wildcards and a <c>$</c> end anchor. Ties go to Allow.
/// </summary>
public sealed class RobotsRules
{
    public static readonly RobotsRules AllowAll = new(Array.Empty<Rule>());

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsRules(IReadOnlyList<Rule> rules) => _rules = rules;

    public int RuleCount => _rules.Count;

    public static RobotsRules Parse(string? content, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AllowAll;
        }

        var token = ProductToken(userAgent);
        var groups = new List<(List<string> Agents, List<Rule> Rules)>();
        List<string>? agents = null;
        List<Rule>? rules = null;
        var lastWasAgent = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || agents is null)
                {
                    agents = new List<string>();
                    rules = new List<Rule>();
                    groups.Add((agents, rules));
                }

                agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (rules is null || (key != "allow" && key != "disallow"))
            {
                continue;
            }

            // An empty Disallow means "allow everything" and adds no rule.
            if (value.Length > 0)
            {
                rules.Add(new Rule(value, key == "allow"));
            }
        }

        // Groups naming our product token win over "*"; all matching groups are merged.
        var specific = groups
            .Where(g => g.Agents.Any(a => a != "*" && token.StartsWith(a, StringComparison.Ordinal)))
            .ToList();
        var chosen = specific.Count > 0 ? specific : groups.Where(g => g.Agents.Contains("*")).ToList();
        var selected = chosen.SelectMany(g => g.Rules).ToList();

        return selected.Count == 0 ? AllowAll : new RobotsRules(selected);
    }

    /// <summary>Path plus query, e.g. <c>/a/b.bin?x=1</c>.</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery))
        {
            pathAndQuery = "/";
        }

        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(pathAndQuery))
            {
                continue;
            }

            if (best is null || rule.Pattern.Length > best.Pattern.Length ||
                (rule.Pattern.Length == best.Pattern.Length && rule.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static string ProductToken(string userAgent)
    {
        var token = userAgent.Split('/', ' ')[0].Trim().ToLowerInvariant();
        return token.Length == 0 ? "*" : token;
    }

    private sealed class Rule
    {
        private readonly Regex _regex;

        public Rule(string pattern, bool allow)
        {
            Pattern = pattern;
            Allow = allow;
            var anchored = pattern.EndsWith('$');
            var body = anchored ? pattern[..^1] : pattern;
            var regex = "^" + Regex.Escape(body).Replace("\\*", ".*") + (anchored ? "$" : string.Empty);
            _regex = new Regex(regex, RegexOptions.CultureInvariant);
        }

        public string Pattern { get; }

        public bool Allow { get; }

        public bool Matches(string path) => _regex.IsMatch(path);
    }
}
