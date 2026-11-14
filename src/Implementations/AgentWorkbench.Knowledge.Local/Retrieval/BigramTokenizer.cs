using System.Text.RegularExpressions;

namespace AgentWorkbench.Knowledge.Local.Retrieval;

/// <summary>
/// 中文无词典分词：连续汉字滑窗取 bigram，ASCII 型号/错误码原样保留。
/// </summary>
/// <remarks>
/// <para>
/// <b>索引与查询必须调这同一个类</b>——两边切法不一致时结果是 0 命中，且不会报错。
/// 这是本模块唯一不能分叉的地方。
/// </para>
/// <para>
/// 为什么不用 jieba：词典是一个隐藏状态，换一版词典检索结果和评测数字一起漂；
/// bigram 的输出完全由输入决定，可复算。代价是索引体积约 ×1.8、偶有误命中。
/// </para>
/// <para>
/// 单字不成 bigram（<c>泵</c>）→ 不进索引，查询时由 <see cref="SingleHanChars"/> 交给 LIKE 兜底。
/// 不索引单字是有意的：<c>的/是/在</c> 这类字几乎命中所有块，只会把 bm25 稀释掉。
/// </para>
/// </remarks>
public static partial class BigramTokenizer
{
    /// <summary>
    /// ASCII 词（型号 <c>H5U</c>、错误码 <c>0x02</c>、地址 <c>192.168.1.88</c>、变量 <c>_McMst.Busy</c>）
    /// 或连续汉字段。ASCII 词必须以字母数字结尾，避免把句末的 <c>.</c> 吞进去。
    /// </summary>
    [GeneratedRegex(@"[A-Za-z0-9_#](?:[A-Za-z0-9_#.\-/]*[A-Za-z0-9_#])?|[\u4e00-\u9fff]+")]
    private static partial Regex SegmentPattern();

    /// <summary>
    /// 切出全部索引 token：ASCII 词原样、连续汉字取 bigram。用于写入 FTS 与构建 MATCH。
    /// </summary>
    /// <example>
    /// <c>H5U故障代码</c> → <c>H5U 故障 障代 代码</c>
    /// </example>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<string>();
        foreach (Match match in SegmentPattern().Matches(text))
        {
            var run = match.Value;
            if (run[0] < '\u0080')
            {
                tokens.Add(run);
                continue;
            }

            if (run.Length == 1)
                continue; // 单字不成 bigram

            for (var i = 0; i < run.Length - 1; i++)
                tokens.Add(run.Substring(i, 2));
        }

        return tokens;
    }

    /// <summary>切好后用空格连接，写入 <c>chunks_fts.content</c> 的形态。</summary>
    public static string ToIndexText(string? text) => string.Join(' ', Tokens(text));

    /// <summary>
    /// 连续汉字长度为 1 的字符（如 <c>泵</c>），用于 LIKE 兜底。
    /// 这类查询极少，单独走一条路比给索引塞 unigram 便宜。
    /// </summary>
    public static IReadOnlyList<string> SingleHanChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var chars = new List<string>();
        foreach (Match match in SegmentPattern().Matches(text))
        {
            var run = match.Value;
            if (run[0] >= '\u0080' && run.Length == 1)
                chars.Add(run);
        }

        return chars;
    }
}
