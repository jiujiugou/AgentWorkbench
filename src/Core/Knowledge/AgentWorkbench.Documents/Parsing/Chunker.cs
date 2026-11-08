#pragma warning disable SKEXP0050 // TextChunker 仍是实验性 API
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Text;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>切分产物（尚未入库）。</summary>
public sealed record ChunkDraft(int Seq, int PageFrom, int PageTo, string? Heading, string Content);

/// <summary>按标题/条款预切分，再对超长段落做定长切分（带重叠）。</summary>
public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedPage> pages);
}

/// <summary>
/// 按页递归切块，切分本身交给 <see cref="TextChunker"/>（Semantic Kernel 官方实现）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用自己写的按行累积：</b>PDF 抽出来的"行"不可信——实测单个"行"可以长达 1128 字
/// （换行位置由版面决定，不在句子边界）。按行累积时，一行就超过目标长度，块会长到 2154 字，
/// 而块边界仍落在句子中间。递归切分先按段落、再按句末标点、最后才按字符硬切，
/// 边界因此落在语义单元上，不落在 PDF 的排版换行上。
/// </para>
/// <para>
/// <b>为什么按页切而不是整篇切：</b>TextChunker 只处理纯文本，不返回字符偏移，
/// 切完就无法把块映射回页码——而"回原文"要显示页码。按页调用则页码天然保留。
/// 代价是跨页的句子拿不到前页重叠，对 PDF 分页来说这个损失本来就存在。
/// </para>
/// <para>
/// <b>必须传 CharCounter：</b>TextChunker 默认按空白数 token，中文整段没有空白，
/// 会被算成 1 个 token，长度上限形同虚设。这里按字符计数，与原来 800/120 的语义一致。
/// </para>
/// </remarks>
public sealed class Chunker : IChunker
{
    /// <summary>单行上限（字符）。超过就继续按句末标点切。</summary>
    private const int MaxCharsPerLine = 120;

    /// <summary>目标块长（字符）。</summary>
    private const int TargetChars = 800;

    /// <summary>相邻块的重叠长度（字符）。</summary>
    private const int OverlapChars = 120;

    private static readonly TextChunker.TokenCounter CharCounter = text => text.Length;

    private readonly ILogger<Chunker> _logger;

    public Chunker(ILogger<Chunker> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedPage> pages)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Chunk started: PageCount={PageCount}", pages.Count);

        var chunks = new List<ChunkDraft>();
        var seq = 0;

        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text))
                continue;

            // 先把超长行按句末标点拆到 120 字以内
            var lines = TextChunker.SplitPlainTextLines(page.Text, MaxCharsPerLine, CharCounter);
            if (lines.Count == 0)
                continue;

            // 再把行合并成不超过 800 字、相邻重叠 120 字的块
            var paragraphs = TextChunker.SplitPlainTextParagraphs(
                lines, TargetChars, OverlapChars, chunkHeader: null, tokenCounter: CharCounter);

            var pageHeading = lines.FirstOrDefault(TextPatterns.IsHeading);

            foreach (var paragraph in paragraphs)
            {
                var content = paragraph.Trim();
                if (content.Length == 0)
                    continue;

                var firstLine = content.Split('\n')[0].Trim();
                var heading = TextPatterns.IsHeading(firstLine) ? firstLine : pageHeading;

                chunks.Add(new ChunkDraft(seq++, page.PageNumber, page.PageNumber, heading, content));
            }
        }

        _logger.LogInformation(
            "Chunk completed: PageCount={PageCount} ChunkCount={ChunkCount} ElapsedMs={ElapsedMs}",
            pages.Count, chunks.Count, sw.ElapsedMilliseconds);

        return chunks;
    }
}
