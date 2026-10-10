using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Extraction;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Extraction;

public sealed class RuleBasedElementExtractorTests
{
    private readonly RuleBasedElementExtractor _extractor = new();

    private const string Contract =
        "工业品买卖合同\n" +
        "出卖人（甲方）：华成精密制造有限公司\n" +
        "买受人（乙方）：恒远机电设备有限公司\n" +
        "签订地点：河北省邢台市\n" +
        "第二条 价款与支付\n" +
        "合同总价款为人民币128,000 元（壹拾贰万捌仟元整）。\n" +
        "第三条 交付与验收\n" +
        "交货期限：甲方应于2026 年 5 月 1 日前将货物运至乙方指定仓库。\n" +
        "质量保证期为12个月。\n" +
        "甲方应于收到货物后 7 日内提出异议。\n" +
        "违约方按合同金额 10% 支付违约金。\n";

    [Fact]
    public void Name_IsRule() => Assert.Equal("rule", _extractor.Name);

    [Fact]
    public async Task Extract_Parties_FromFirstChunk()
    {
        var drafts = await Extract(Contract);

        Assert.Equal("华成精密制造有限公司", Value(drafts, "seller"));
        Assert.Equal("恒远机电设备有限公司", Value(drafts, "buyer"));
        Assert.All(drafts.Where(d => d.Key is "seller" or "buyer"), d => Assert.Equal("rule", d.Source));
        Assert.All(drafts.Where(d => d.Key is "seller" or "buyer"), d => Assert.Equal(0.7, d.Confidence));
    }

    [Fact]
    public async Task Extract_Amount_StripsThousandsSeparator()
    {
        var amount = Single(await Extract(Contract), "amount");

        Assert.Equal("128000", amount.Value);
        Assert.Equal("money", amount.ValueType);
        Assert.Equal("元", amount.Unit);
        Assert.Equal(0.9, amount.Confidence);
        Assert.Equal("rule", amount.Source);
    }

    [Fact]
    public async Task Extract_AmountUppercase()
    {
        Assert.Equal("壹拾贰万捌仟元整", Value(await Extract(Contract), "amount_text"));
    }

    [Fact]
    public async Task Extract_DatesAndPeriods()
    {
        var drafts = await Extract(Contract);

        Assert.Equal("2026 年 5 月 1 日", Value(drafts, "delivery_date"));
        Assert.Equal("12", Value(drafts, "warranty_months"));
        Assert.Equal("7", Value(drafts, "acceptance_days"));
        Assert.Equal("10", Value(drafts, "penalty_rate"));
    }

    [Fact]
    public async Task Extract_PopulatesEvidenceAndCoordinates()
    {
        var amount = Single(await Extract(Contract), "amount");

        Assert.Equal(1L, amount.ChunkId!.Value);
        Assert.Equal(0, amount.Seq!.Value);
        Assert.Equal(1, amount.PageFrom!.Value);
        Assert.Equal(1, amount.PageTo!.Value);
        Assert.False(string.IsNullOrWhiteSpace(amount.Evidence));
        Assert.Contains("128,000", amount.Evidence);
    }

    [Fact]
    public async Task Extract_NoMatch_ReturnsEmpty()
    {
        var drafts = await _extractor.ExtractAsync(Context("完全无关的一句话"), CancellationToken.None);

        Assert.Empty(drafts);
    }

    [Fact]
    public async Task Extract_MultipleChunks_PicksFirstMatch()
    {
        var drafts = await _extractor.ExtractAsync(Context("出卖人：甲公司\n买受人：乙公司", "出卖人：丙公司\n买受人：丁公司"), CancellationToken.None);

        Assert.Equal("甲公司", Value(drafts, "seller"));
        Assert.Equal("乙公司", Value(drafts, "buyer"));
    }

    private Task<IReadOnlyList<ElementDraft>> Extract(string content) => _extractor.ExtractAsync(Context(content), CancellationToken.None);

    private static ReviewContext Context(params string[] contents)
    {
        var chunks = contents.Select((c, i) => new ChunkRecord
        {
            Id = i + 1,
            DocumentId = "doc",
            Seq = i,
            PageFrom = 1,
            PageTo = 1,
            Content = c,
            CharCount = c.Length,
            CreatedAt = "t"
        }).ToList();
        return new ReviewContext("doc", "f.pdf", chunks);
    }

    private static ElementDraft Single(IReadOnlyList<ElementDraft> drafts, string key) => Assert.Single(drafts, d => d.Key == key);

    private static string Value(IReadOnlyList<ElementDraft> drafts, string key) => Single(drafts, key).Value;
}
