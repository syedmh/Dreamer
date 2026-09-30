namespace Husaynia.BaselineCapture;

public sealed class RobotsPolicy
{
    internal const int MaximumTextCharacters = 256 * 1024;
    internal const int MaximumRuleCount = 256;
    internal const int MaximumPatternCharacters = 512;

    private readonly List<Rule> _rules = [];

    public static RobotsPolicy Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextCharacters)
        {
            throw new CaptureSafetyException("robots-text-too-large");
        }

        var policy = new RobotsPolicy();
        var currentAgents = new List<string>();
        var groupHasRules = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                if (groupHasRules)
                {
                    currentAgents.Clear();
                    groupHasRules = false;
                }

                continue;
            }

            if (line.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            {
                if (groupHasRules)
                {
                    currentAgents.Clear();
                    groupHasRules = false;
                }

                currentAgents.Add(line[("User-agent:".Length)..].Trim());
            }
            else if (currentAgents.Contains("*", StringComparer.OrdinalIgnoreCase) &&
                (line.StartsWith("Allow:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase)))
            {
                var allow = line.StartsWith("Allow:", StringComparison.OrdinalIgnoreCase);
                var separator = line.IndexOf(':');
                var pattern = line[(separator + 1)..].Trim();
                groupHasRules = true;
                if (pattern.Length > 0)
                {
                    if (pattern.Length > MaximumPatternCharacters)
                    {
                        throw new CaptureSafetyException("robots-pattern-too-long");
                    }

                    if (policy._rules.Count >= MaximumRuleCount)
                    {
                        throw new CaptureSafetyException("robots-rule-limit-exceeded");
                    }

                    policy._rules.Add(new Rule(pattern, allow));
                }
            }
        }

        return policy;
    }

    public bool IsAllowed(Uri uri)
    {
        var candidate = $"{uri.AbsolutePath}{uri.Query}";
        var matching = _rules
            .Where(rule => rule.IsMatch(candidate))
            .OrderByDescending(rule => rule.Specificity)
            .ThenByDescending(rule => rule.Allow)
            .FirstOrDefault();
        return matching?.Allow ?? true;
    }

    private sealed record Rule(string Pattern, bool Allow)
    {
        public int Specificity => Pattern.Count(character => character is not '*' and not '$');

        public bool IsMatch(string candidate)
        {
            var endAnchored = Pattern.EndsWith('$');
            var source = endAnchored ? Pattern[..^1] : Pattern;
            var wildcardPattern = endAnchored ? source : $"{source}*";
            var patternIndex = 0;
            var candidateIndex = 0;
            var wildcardIndex = -1;
            var wildcardCandidateIndex = 0;
            while (candidateIndex < candidate.Length)
            {
                if (patternIndex < wildcardPattern.Length
                    && wildcardPattern[patternIndex] != '*'
                    && char.ToUpperInvariant(wildcardPattern[patternIndex])
                        == char.ToUpperInvariant(candidate[candidateIndex]))
                {
                    patternIndex++;
                    candidateIndex++;
                }
                else if (patternIndex < wildcardPattern.Length
                         && wildcardPattern[patternIndex] == '*')
                {
                    wildcardIndex = patternIndex++;
                    wildcardCandidateIndex = candidateIndex;
                }
                else if (wildcardIndex >= 0)
                {
                    patternIndex = wildcardIndex + 1;
                    candidateIndex = ++wildcardCandidateIndex;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < wildcardPattern.Length
                   && wildcardPattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == wildcardPattern.Length;
        }
    }
}
