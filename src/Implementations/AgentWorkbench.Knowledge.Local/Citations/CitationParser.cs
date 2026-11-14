using System.Text.RegularExpressions;

namespace AgentWorkbench.Knowledge.Local.Citations;

/// <summary>
/// 把答案切成句子，并把方括号编号标注归属到句子。
/// </summary>
/// <remarks>
/// <para>
/// 标注归属规则：<b>挂到它前面那一句</b>。模型既可能写 <c>……SD 卡[2]。</c>（标号在句内），
/// 也可能写 <c>……SD 卡。[2]</c>（标号在句号之后）。两种都是"这句有出处"，
/// 若按"标号属于其后第一句"处理，第二种会归到下一句去，整份报告的句级统计就全错位了。
/// </para>
/// <para>
/// 切句只认标点，不做语义判断：中文句末标点与换行都算边界，连续标点（<c>？！</c>）并入同一句。
/// 英文句点不算——工业手册里 <c>192.168.1.88</c>、<c>V4.2</c> 到处都是，按点切会把地址切碎。
/// </para>
/// </remarks>
public static partial class CitationParser
{
    /// <summary>句末标点与换行：只认这些，不认英文句点。</summary>
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '!', '?', ';', '\n'];

    /// <summary>
    /// 标注形态：<c>[2]</c>、<c>[2,3]</c>、<c>[2，3]</c>。
    /// </summary>
    [GeneratedRegex(@"\[\s*(\d+(?:\s*[,，]\s*\d+)*)\s*\]")]
    private static partial Regex CitationPattern();

    /// <summary>切句并抽取标注。空答案返回空结果。</summary>
    public static ParsedAnswer Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new ParsedAnswer([], []);

        var ranges = SentenceRanges(answer);
        var sentences = new List<string>(ranges.Count);
        foreach (var (start, end) in ranges)
            sentences.Add(answer[start..end]);

        var raw = new List<(int, int)>();
        foreach (Match match in CitationPattern().Matches(answer))
        {
            var sentenceIndex = LocateSentence(answer, ranges, match.Index);
            foreach (var part in match.Groups[1].Value.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var no) && no > 0)
                    raw.Add((sentenceIndex, no));
            }
        }

        return new ParsedAnswer(sentences, raw);
    }

    /// <summary>
    /// 抹掉所有编号标注，得到纯正文。
    /// </summary>
    /// <remarks>
    /// 算字符覆盖率前必须调用：<c>[2]</c> 在分词器眼里是一个 ASCII 词 <c>2</c>，
    /// 留在句子里会平白拉低覆盖率，把好引用判成"指向错块"。
    /// </remarks>
    public static string StripCitations(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : CitationPattern().Replace(text, string.Empty);

    /// <summary>
    /// 只保留满足 <paramref name="keep"/> 的编号，重写标注；返回新正文。
    /// </summary>
    /// <remarks>
    /// <c>[2,9]</c> 里若只有 9 越界，结果是 <c>[2]</c> 而不是整段删掉——
    /// 越界的是那个编号，不是这句话的出处。
    /// </remarks>
    public static string KeepCitations(string? text, Func<int, bool> keep)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return CitationPattern().Replace(text, match =>
        {
            var kept = new List<string>();
            foreach (var part in match.Groups[1].Value.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var no) && no > 0 && keep(no))
                    kept.Add(no.ToString());
            }

            return kept.Count == 0 ? string.Empty : "[" + string.Join(",", kept) + "]";
        });
    }

    /// <summary>按句末标点切出区间。<see cref="Parse"/> 与覆盖率统计共用同一套边界。</summary>
    private static List<(int Start, int End)> SentenceRanges(string text)
    {
        var ranges = new List<(int, int)>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnds, text[i]) < 0)
                continue;

            // 连续标点算同一句的结尾（"？！"、"。。。"）：边界推到这一串之后。
            var end = i + 1;
            while (end < text.Length && Array.IndexOf(SentenceEnds, text[end]) >= 0)
                end++;

            if (!IsBlank(text, start, end))
                ranges.Add((start, end));

            start = end;
            i = end - 1;
        }

        if (start < text.Length && !IsBlank(text, start, text.Length))
            ranges.Add((start, text.Length));

        return ranges;
    }

    /// <summary>
    /// 标注落在哪一句：句内就是本句；紧跟在句末标点之后（中间只有空白）仍归前一句。
    /// </summary>
    /// <remarks>
    /// 后一条是这个函数存在的理由。模型常写 <c>……没有插好。[1] 请重新插拔。</c>——
    /// 按位置归属会把它算成"第二句有出处、第一句是结论没依据"，整整错一位，
    /// 于是每个句级统计都反着来。
    /// </remarks>
    private static int LocateSentence(string text, List<(int Start, int End)> ranges, int position)
    {
        if (ranges.Count == 0)
            return 0;

        // 往回跳过空白，看是否紧贴着句末标点。
        var previous = position - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous]))
            previous--;

        if (previous >= 0 && Array.IndexOf(SentenceEnds, text[previous]) >= 0)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                if (ranges[i].Start <= previous && previous < ranges[i].End)
                    return i;
            }
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var (_, end) = ranges[i];
            if (position < end)
                return i; // 在句内
            if (i + 1 < ranges.Count && position < ranges[i + 1].Start)
                return i; // 落在两句之间
        }

        return ranges.Count - 1; // 尾部的标注归最后一句
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
    }
}
