using System.Text;
using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Documents.Parsing;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Documents;

/// <summary>
/// 功能 3 的解析侧：真 Worker 落库后，页与片段的内容、顺序、页范围必须自洽。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §8（P07–P08）。
/// </summary>
public class DocumentContentParseTests
{
    [Fact]
    public async Task P07_多页PDF_页序与片段页范围一致()
    {
        using var h = new ParseWorkerHarness();
        var pageTexts = new[]
        {
            new string('甲', 900),
            new string('乙', 900),
            new string('丙', 900),
        };
        var document = await h.InsertAsync("a.pdf", [1, 2, 3]);
        h.PdfExtractor
            .Setup(x => x.Extract(document.StoredPath))
            .Returns(pageTexts
                .Select((text, index) => new ExtractedPage(index + 1, text))
                .ToList());

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Parsed, result.Status);
        var pages = await h.Repository.GetPagesAsync(document.Id);
        Assert.Equal(3, pages.Count);
        Assert.Equal(1, pages[0].PageNo);
        Assert.Equal(2, pages[1].PageNo);
        Assert.Equal(3, pages[2].PageNo);
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Equal(pageTexts[i], pages[i].RawText);
            Assert.Equal(pageTexts[i], pages[i].CleanedText);
        }

        var chunks = await h.Repository.GetChunksAsync(document.Id);
        Assert.NotEmpty(chunks);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Seq));

        var previousFrom = 0;
        var previousTo = 0;
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.PageFrom, 1, 3);
            Assert.InRange(chunk.PageTo, chunk.PageFrom, 3);
            Assert.True(chunk.PageFrom >= previousFrom, "片段起始页应随序号单调不减");
            Assert.True(chunk.PageTo >= previousTo, "片段结束页应随序号单调不减");
            previousFrom = chunk.PageFrom;
            previousTo = chunk.PageTo;
        }
    }

    [Fact]
    public async Task P08_长文本多块_序号与字符数自洽()
    {
        using var h = new ParseWorkerHarness();
        var paragraphs = Enumerable.Range(0, 5)
            .Select(index => $"段落{(char)('A' + index)}" + new string('文', 195) + "。");
        var document = await h.InsertAsync(
            "long.md",
            Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs)));

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Parsed, result.Status);
        var pages = await h.Repository.GetPagesAsync(document.Id);
        Assert.Single(pages);

        var chunks = await h.Repository.GetChunksAsync(document.Id);
        Assert.True(chunks.Count >= 2, $"长文本应切成多块，实际 {chunks.Count} 块");
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Seq));

        foreach (var chunk in chunks)
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk.Content));
            Assert.Equal(chunk.Content.Length, chunk.CharCount);
            Assert.Equal(1, chunk.PageFrom);
            Assert.Equal(1, chunk.PageTo);
        }
    }
}
