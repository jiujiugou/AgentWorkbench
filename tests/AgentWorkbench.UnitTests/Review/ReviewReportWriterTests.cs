using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using Xunit;

namespace AgentWorkbench.UnitTests.Review;

public sealed class ReviewReportWriterTests
{
    private readonly ReviewReportWriter _writer = new();

    [Fact]
    public void WriteMarkdown_ContainsHeaderAndDocument()
    {
        var md = _writer.WriteMarkdown(Run(), Document(), new[] { Element() }, Array.Empty<ReviewFindingRecord>());

        Assert.Contains("# 合同审查报告", md);
        Assert.Contains("合同.pdf", md);
        Assert.Contains("| 要素 | 值 | 类型 | 来源 | 置信度 | 出处 |", md);
        Assert.Contains("片段#0 (p1-1)", md);
    }

    [Fact]
    public void WriteMarkdown_NoFindings_StatesNone()
    {
        var md = _writer.WriteMarkdown(Run(), Document(), Array.Empty<ReviewElementRecord>(), Array.Empty<ReviewFindingRecord>());

        Assert.Contains("未发现不一致项。", md);
    }

    [Fact]
    public void WriteMarkdown_WithFindings_RendersTable()
    {
        var md = _writer.WriteMarkdown(Run(), Document(), Array.Empty<ReviewElementRecord>(), new[] { Finding() });

        Assert.Contains("| 严重级 | 结论 | 期望 | 实际 | 出处 |", md);
        Assert.Contains("金额不一致", md);
    }

    [Fact]
    public void WriteMarkdown_EscapesPipesAndNewlines()
    {
        var md = _writer.WriteMarkdown(Run(), Document(), new[] { Element(value: "a|b\nc") }, Array.Empty<ReviewFindingRecord>());

        Assert.Contains("a\\|b c", md);
    }

    [Fact]
    public void WriteMarkdown_NullSeq_ShowsDash()
    {
        var md = _writer.WriteMarkdown(Run(), Document(), new[] { Element(seq: null) }, Array.Empty<ReviewFindingRecord>());

        Assert.Contains("| 0.90 | - |", md);
    }

    [Fact]
    public void WriteCsv_HasHeaderAndRows()
    {
        var csv = _writer.WriteCsv(Run(), Document(), new[] { Element() }, new[] { Finding() });

        Assert.StartsWith("kind,key/severity,title/label,value/expected,actual,source/type,chunkId,seq,evidence", csv);
        Assert.Contains("element,\"amount\",\"合同总价\",\"128000\",,\"money\",3,0,\"ev\"", csv);
        Assert.Contains("finding,\"high\",\"金额不一致\",\"128000\",\"120000\",\"金额不一致\",3,0,\"ev\"", csv);
    }

    [Fact]
    public void WriteCsv_EscapesQuotes()
    {
        var csv = _writer.WriteCsv(Run(), Document(), new[] { Element(value: "a\"b") }, Array.Empty<ReviewFindingRecord>());

        Assert.Contains("\"a\"\"b\"", csv);
    }

    [Fact]
    public void WriteCsv_NullSeq_RendersEmpty()
    {
        var csv = _writer.WriteCsv(Run(), Document(), new[] { Element(seq: null) }, Array.Empty<ReviewFindingRecord>());

        Assert.Contains(",\"money\",3,,", csv);
    }

    private static ReviewRunRecord Run() => new()
    {
        Id = 7, DocumentId = "d", Mode = "both", Template = "买卖合同",
        Status = "succeeded", LatencyMs = 43, CreatedAt = "2026-01-01T00:00:00+00:00"
    };

    private static DocumentRecord Document() => new() { Id = "d", FileName = "合同.pdf" };

    private static ReviewElementRecord Element(string value = "128000", int? seq = 0) => new()
    {
        Id = 1, RunId = 7, Key = "amount", Label = "合同总价", Value = value, ValueType = "money",
        Unit = "元", Source = "rule", Confidence = 0.9, ChunkId = 3, Seq = seq, PageFrom = 1, PageTo = 1, Evidence = "ev"
    };

    private static ReviewFindingRecord Finding() => new()
    {
        Id = 1, RunId = 7, RuleId = 1, Severity = "high", Category = "金额不一致", Title = "金额不一致",
        Expected = "128000", Actual = "120000", ChunkId = 3, Seq = 0, PageFrom = 1, PageTo = 1, Evidence = "ev"
    };
}
