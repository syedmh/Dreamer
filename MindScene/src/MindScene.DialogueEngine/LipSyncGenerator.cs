using MindScene.Core.Models;

namespace MindScene.DialogueEngine;

/// <summary>
/// Generates viseme (mouth shape) sequences from text using a phoneme-to-viseme mapping.
/// MVP: rule-based phoneme approximation from text — no external phoneme engine required.
/// </summary>
public static class LipSyncGenerator
{
    // Preston Blair viseme set mapping common phoneme patterns
    private static readonly Dictionary<string, string[]> PhonemeGroups = new()
    {
        ["rest"] = ["_", " "],
        ["mbp"] = ["m", "b", "p"],
        ["fv"] = ["f", "v"],
        ["th"] = ["th"],
        ["dtn"] = ["d", "t", "n", "l"],
        ["kng"] = ["k", "g", "ng"],
        ["ch"] = ["ch", "sh", "zh"],
        ["ss"] = ["s", "z"],
        ["ee"] = ["ee", "e", "i"],
        ["ih"] = ["ih", "y"],
        ["oh"] = ["oh", "o"],
        ["oo"] = ["oo", "u", "w"],
        ["aa"] = ["aa", "a"],
        ["ae"] = ["ae"],
        ["er"] = ["er", "r"]
    };

    private static readonly Dictionary<char, string> CharToViseme = BuildCharToViseme();

    private static Dictionary<char, string> BuildCharToViseme()
    {
        var map = new Dictionary<char, string>();
        foreach (var (viseme, chars) in PhonemeGroups)
            foreach (var c in chars.Where(s => s.Length == 1))
                if (!map.ContainsKey(c[0])) map[c[0]] = viseme;
        return map;
    }

    public static List<Viseme> Generate(string text, float startSeconds, float durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var visemes = new List<Viseme>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float timePerWord = durationSeconds / (words.Length + 1);
        float t = startSeconds;

        foreach (var word in words)
        {
            var wordVisemes = GetWordVisemes(word);
            float timePerViseme = timePerWord / Math.Max(wordVisemes.Count, 1);

            foreach (var shape in wordVisemes)
            {
                visemes.Add(new Viseme(shape, t, timePerViseme));
                t += timePerViseme;
            }

            // Brief rest between words
            visemes.Add(new Viseme("rest", t, timePerWord * 0.15f));
            t += timePerWord * 0.15f;
        }

        return visemes;
    }

    private static List<string> GetWordVisemes(string word)
    {
        var visemes = new List<string>();
        var lower = word.ToLowerInvariant().Trim('.', ',', '!', '?', '"', '\'');

        for (int i = 0; i < lower.Length; i++)
        {
            // Check for digraphs first
            if (i + 1 < lower.Length)
            {
                var digraph = $"{lower[i]}{lower[i + 1]}";
                if (digraph is "th" or "ch" or "sh" or "zh" or "ng")
                {
                    visemes.Add(PhonemeGroups.First(kv => kv.Value.Contains(digraph)).Key);
                    i++;
                    continue;
                }
            }

            if (CharToViseme.TryGetValue(lower[i], out var viseme))
                visemes.Add(viseme);
        }

        return visemes.Count == 0 ? ["rest"] : visemes;
    }
}
