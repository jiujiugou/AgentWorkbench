using System.Text.RegularExpressions;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>文本结构识别的共享规则。</summary>
public static class TextPatterns
{
    public const int MaxHeadingLength = 40;

    /// <summary>
    /// 条款/标题起始：第X条/章/节/款/部分/编、"一、"、（一）、1.1（多级）等。
    /// 注意：不把单独的 "1." 列表项当作标题，否则会误判并阻断段落合并。
    /// </summary>
    public static readonly Regex Heading = new(
        @"^(第\s*[0-9零一二三四五六七八九十百千]+\s*(章|节|条|款|部分|编)|[一二三四五六七八九十]+\s*[、．.]|（[一二三四五六七八九十]+）|\([一二三四五六七八九十]+\)|\d+(\.\d+)+\s*[、．.]?|\d+\s*、)",
        RegexOptions.Compiled);

    public static bool IsHeading(string line) =>
        line.Length <= MaxHeadingLength && Heading.IsMatch(line);

    /// <summary>是否以句末标点结尾（用于判断换行是否属于同一段）。</summary>
    public static bool EndsSentence(string line)
    {
        if (line.Length == 0) return true;
        return line[^1] switch
        {
            '。' or '．' or '.' or '！' or '!' or '？' or '?' or '；' or ';'
                or '”' or '”' or '）' or ')' or '》' or '】' => true,
            _ => false
        };
    }
}
