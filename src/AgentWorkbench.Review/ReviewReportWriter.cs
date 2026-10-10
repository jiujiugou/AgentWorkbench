using System.Text;

namespace AgentWorkbench.Review;

/// <summary>审查报告导出扩展点（Markdown / CSV；Word/Excel 可加实现）。</summary>
public interface IReviewReportWriter
{
    string WriteMarkdown(
        ReviewRunRecord run,
        DocumentRecord document,
        IReadOnlyList<ReviewElementRecord> elements,
        IReadOnlyList<ReviewFindingRecord> findings);

    string WriteCsv(
        ReviewRunRecord run,
        DocumentRecord document,
        IReadOnlyList<ReviewElementRecord> elements,
        IReadOnlyList<ReviewFindingRecord> findings);
}

/// <inheritdoc />
public sealed class ReviewReportWriter : IReviewReportWriter
{
    public string WriteMarkdown(
        ReviewRunRecord run,
        DocumentRecord document,
        IReadOnlyList<ReviewElementRecord> elements,
        IReadOnlyList<ReviewFindingRecord> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 合同审查报告");
        sb.AppendLine();
        sb.AppendLine($"- 文档：{document.FileName}（{document.Id}）");
        sb.AppendLine($"- 模式：{run.Mode}　模板：{run.Template ?? "-"}");
        sb.AppendLine($"- 状态：{run.Status}　耗时：{run.LatencyMs ?? 0} ms　时间：{run.CreatedAt}");
        sb.AppendLine();

        sb.AppendLine("## 要素表");
        sb.AppendLine();
        sb.AppendLine("| 要素 | 值 | 类型 | 来源 | 置信度 | 出处 |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var e in elements)
        {
            var source = e.Seq is null ? "-" : $"片段#{e.Seq} (p{e.PageFrom}-{e.PageTo})";
            sb.AppendLine($"| {e.Label} | {Cell(e.Value)} | {e.ValueType} | {e.Source} | {e.Confidence:0.00} | {source} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 不一致 / 风险清单");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("未发现不一致项。");
        }
        else
        {
            sb.AppendLine("| 严重级 | 结论 | 期望 | 实际 | 出处 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var f in findings)
            {
                var source = f.Seq is null ? "-" : $"片段#{f.Seq} (p{f.PageFrom}-{f.PageTo})";
                sb.AppendLine($"| {f.Severity} | {Cell(f.Title)} | {Cell(f.Expected)} | {Cell(f.Actual)} | {source} |");
            }
        }
        return sb.ToString();
    }

    public string WriteCsv(
        ReviewRunRecord run,
        DocumentRecord document,
        IReadOnlyList<ReviewElementRecord> elements,
        IReadOnlyList<ReviewFindingRecord> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kind,key/severity,title/label,value/expected,actual,source/type,chunkId,seq,evidence");
        foreach (var e in elements)
            sb.AppendLine($"element,{Csv(e.Key)},{Csv(e.Label)},{Csv(e.Value)},,{Csv(e.ValueType)},{e.ChunkId},{e.Seq},{Csv(e.Evidence)}");
        foreach (var f in findings)
            sb.AppendLine($"finding,{Csv(f.Severity)},{Csv(f.Title)},{Csv(f.Expected)},{Csv(f.Actual)},{Csv(f.Category)},{f.ChunkId},{f.Seq},{Csv(f.Evidence)}");
        return sb.ToString();
    }

    private static string Cell(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var escaped = value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return $"\"{escaped}\"";
    }
}
