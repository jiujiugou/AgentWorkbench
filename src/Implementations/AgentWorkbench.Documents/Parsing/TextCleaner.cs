using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>文本清洗：去页眉/页脚/页码、合并断行、规范化空白。</summary>
public interface ITextCleaner
{
    IReadOnlyList<ExtractedPage> Clean(IReadOnlyList<ExtractedPage> pages);
}

public sealed class TextCleaner : ITextCleaner
{
    /// <summary>纯页码行（如 "1"、"- 3 -"、"第 2 页"、"3 / 10"）。</summary>
    private static readonly Regex PageNumberLine = new(
        @"^\s*[-—－]?\s*(第\s*)?\d+\s*(页|/\s*\d+|\s*共\s*\d+\s*页)?\s*[-—－]?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"[ \t\u00A0\u3000]+", RegexOptions.Compiled);

    /// <summary>段落合并的最小前文长度，避免把短标题并进正文。</summary>
    private const int MinMergeLength = 12;

    private readonly ILogger<TextCleaner> _logger;

    public TextCleaner(ILogger<TextCleaner> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ExtractedPage> Clean(IReadOnlyList<ExtractedPage> pages)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Text cleaning started: PageCount={PageCount}", pages.Count);

        var repeated = FindRepeatedLines(pages);

        var result = new List<ExtractedPage>(pages.Count);
        foreach (var page in pages)
        {
            var lines = new List<string>();
            foreach (var raw in SplitLines(page.Text))
            {
                var line = MultiSpace.Replace(raw, " ").Trim();
                if (line.Length == 0) continue;
                if (PageNumberLine.IsMatch(line)) continue;
                if (repeated.Contains(Normalize(line))) continue;
                lines.Add(line);
            }

            result.Add(new ExtractedPage(page.PageNumber, string.Join("\n", MergeParagraphs(lines))));
        }

        var removedChars = pages.Sum(p => p.Text.Length) - result.Sum(p => p.Text.Length);
        _logger.LogInformation("Text cleaning completed: PageCount={PageCount} RemovedChars={RemovedChars} RepeatedLines={RepeatedCount} ElapsedMs={ElapsedMs}",
            pages.Count, removedChars, repeated.Count, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>跨页重复出现的短行（页眉/页脚）。</summary>
    private static HashSet<string> FindRepeatedLines(IReadOnlyList<ExtractedPage> pages)
    {
        var repeated = new HashSet<string>();
        if (pages.Count < 4) return repeated;

        var linePages = new Dictionary<string, HashSet<int>>();
        foreach (var page in pages)
        {
            foreach (var raw in SplitLines(page.Text))
            {
                var key = Normalize(raw);
                if (key.Length == 0 || key.Length > 40) continue;
                if (!linePages.TryGetValue(key, out var set))
                    linePages[key] = set = new HashSet<int>();
                set.Add(page.PageNumber);
            }
        }

        var threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * 0.6));
        foreach (var pair in linePages)
            if (pair.Value.Count >= threshold)
                repeated.Add(pair.Key);

        return repeated;
    }

    /// <summary>把被 PDF 视觉换行打断的句子重新拼成段落（中文直接拼接）。</summary>
    private static List<string> MergeParagraphs(List<string> lines)
    {
        var merged = new List<string>();
        foreach (var line in lines)
        {
            if (merged.Count == 0)
            {
                merged.Add(line);
                continue;
            }

            var prev = merged[^1];
            var canMerge = prev.Length >= MinMergeLength
                           && !TextPatterns.EndsSentence(prev)
                           && !TextPatterns.IsHeading(prev)
                           && !TextPatterns.IsHeading(line);

            if (canMerge)
                merged[^1] = prev + line;
            else
                merged.Add(line);
        }

        return merged;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Normalize(string line) => MultiSpace.Replace(line, " ").Trim();
}
