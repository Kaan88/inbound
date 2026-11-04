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

        content = content.TrimStart('﻿');
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

                if (value.Length > 0)
                {
                    agents.Add(value.ToLowerInvariant());
                }

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
                rules.Add(new Rule(NormalizeEncoding(value), key == "allow"));
            }
        }

        // RFC 9309 2.2.1: a group naming our product token (case-insensitive, exact) wins
        // over "*"; several groups naming it are merged.
        var specific = groups.Where(g => g.Agents.Contains(token)).ToList();
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

        pathAndQuery = NormalizeEncoding(pathAndQuery);
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

    /// <summary>
    /// Canonical percent-encoding (RFC 9309 2.2.2): decode escapes except reserved
    /// '/', '?', '%' and '#', then re-encode space, controls and non-ASCII as uppercase
    /// UTF-8 escapes, so "/my folder/", "/my%20folder/" and "/%6Dy%20folder/" compare equal.
    /// </summary>
    public static string NormalizeEncoding(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length &&
                Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                var hex = value.Substring(i + 1, 2).ToUpperInvariant();
                var b = Convert.ToByte(hex, 16);
                if (b is (byte)'/' or (byte)'?' or (byte)'%' or (byte)'#')
                {
                    bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("%" + hex));
                }
                else
                {
                    bytes.Add(b);
                }

                i += 2;
                continue;
            }

            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value[i].ToString()));
        }

        var builder = new System.Text.StringBuilder(bytes.Count);
        foreach (var b in bytes)
        {
            // Reserved escapes were re-inserted as ASCII ("%2F"), so they pass through as-is.
            if (b <= 0x20 || b >= 0x7F)
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.ToString();
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
            _regex = new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }

        public string Pattern { get; }

        public bool Allow { get; }

        public bool Matches(string path) => _regex.IsMatch(path);
    }
}
