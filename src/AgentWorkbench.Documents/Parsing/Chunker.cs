using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>切分产物（尚未入库）。</summary>
public sealed record ChunkDraft(int Seq, int PageFrom, int PageTo, string? Heading, string Content);

/// <summary>按标题/条款预切分，再对超长段落做定长切分（带重叠）。</summary>
public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedPage> pages);
}

public sealed class Chunker : IChunker
{
    private const int TargetChars = 800;
    private const int OverlapChars = 120;
    private readonly ILogger<Chunker> _logger;

    public Chunker(ILogger<Chunker> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedPage> pages)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Chunk started: PageCount={PageCount}", pages.Count);

        var lines = new List<(string Text, int Page)>();
        foreach (var page in pages)
        {
            foreach (var raw in page.Text.Split('\n'))
            {
                var text = raw.Trim();
                if (text.Length > 0)
                    lines.Add((text, page.PageNumber));
            }
        }

        var chunks = new List<ChunkDraft>();
        var buffer = new List<(string Text, int Page)>();
        string? heading = null;
        var headingSet = false;
        var seq = 0;
        var lastTailLength = 0;

        void Flush(bool isFinal)
        {
            if (buffer.Count == 0) return;

            var content = string.Join("\n", buffer.Select(x => x.Text)).Trim();
            if (content.Length == 0)
            {
                buffer.Clear();
                return;
            }

            // 末尾仅剩重叠尾巴时不重复出块
            if (isFinal && lastTailLength > 0 && content.Length <= lastTailLength)
            {
                buffer.Clear();
                return;
            }

            chunks.Add(new ChunkDraft(seq++, buffer[0].Page, buffer[^1].Page, heading, content));
            heading = null;
            headingSet = false;

            if (isFinal)
            {
                buffer.Clear();
                return;
            }

            // 保留尾部作为下一块的重叠上下文
            var tail = new List<(string Text, int Page)>();
            var length = 0;
            for (var i = buffer.Count - 1; i >= 0 && length < OverlapChars; i--)
            {
                tail.Insert(0, buffer[i]);
                length += buffer[i].Text.Length;
            }

            lastTailLength = length;
            buffer.Clear();
            buffer.AddRange(tail);
        }

        foreach (var (text, page) in lines)
        {
            var isHeading = TextPatterns.IsHeading(text);

            // 已有足够内容时，在新标题处断开，尽量保持条款完整
            if (isHeading && buffer.Count > 0 && BufferLength(buffer) >= TargetChars)
                Flush(false);

            // 记录本块遇到的第一个标题
            if (isHeading && !headingSet)
            {
                heading = text;
                headingSet = true;
            }

            buffer.Add((text, page));

            if (BufferLength(buffer) >= TargetChars)
                Flush(false);
        }

        Flush(true);
        _logger.LogInformation("Chunk completed: PageCount={PageCount} ChunkCount={ChunkCount} ElapsedMs={ElapsedMs}", pages.Count, chunks.Count, sw.ElapsedMilliseconds);
        return chunks;
    }

    private static int BufferLength(List<(string Text, int Page)> buffer) =>
        buffer.Sum(x => x.Text.Length + 1);
}
