namespace AgentWorkbench.ReviewAdvisor.Evidence;

/// <summary>证据定位（程序、确定性）：引用是否存在于真实 chunk、证据片段是否命中。</summary>
public static class EvidenceLocator
{
    public static ChunkRecord? FindChunk(IReadOnlyList<ChunkRecord> chunks, long? chunkId, int? seq)
    {
        if (chunkId is long id)
        {
            var byId = chunks.FirstOrDefault(c => c.Id == id);
            if (byId is not null)
                return byId;
        }
        if (seq is int s)
            return chunks.FirstOrDefault(c => c.Seq == s);
        return null;
    }

    public static bool IsGrounded(IReadOnlyList<ChunkRecord> chunks, long? chunkId, int? seq, string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
            return false;
        var chunk = FindChunk(chunks, chunkId, seq);
        if (chunk is null)
            return false;
        return chunk.Content.Contains(evidence.Trim(), StringComparison.Ordinal);
    }

    public static IReadOnlyList<(ChunkRecord Chunk, string Evidence)> Locate(
        IReadOnlyList<ChunkRecord> chunks, string? keyword, int max = 5)
    {
        var list = new List<(ChunkRecord, string)>();
        if (string.IsNullOrWhiteSpace(keyword))
            return list;

        foreach (var chunk in chunks)
        {
            var idx = chunk.Content.IndexOf(keyword, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var start = Math.Max(0, idx - 24);
            var length = Math.Min(chunk.Content.Length - start, keyword.Length + 48);
            list.Add((chunk, chunk.Content.Substring(start, length).Trim()));
            if (list.Count >= max)
                break;
        }
        return list;
    }
}
