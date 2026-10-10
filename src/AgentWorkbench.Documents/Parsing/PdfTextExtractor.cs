using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>从 PDF 抽取的单页文本。</summary>
public sealed record ExtractedPage(int PageNumber, string Text);

/// <summary>PDF 文本层抽取（不含 OCR，扫描件不在 P0 范围）。</summary>
public interface IPdfTextExtractor
{
    IReadOnlyList<ExtractedPage> Extract(string filePath);
}

public sealed class PdfTextExtractor : IPdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ExtractedPage> Extract(string filePath)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("PDF extract started: {FilePath}", filePath);

        using var document = PdfDocument.Open(filePath);
        var pages = new List<ExtractedPage>();

        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            pages.Add(new ExtractedPage(page.Number, text));
        }

        _logger.LogInformation("PDF extract completed: File={FilePath} Pages={PageCount} TotalChars={TotalChars} ElapsedMs={ElapsedMs}",
            filePath, pages.Count, pages.Sum(p => p.Text.Length), sw.ElapsedMilliseconds);
        return pages;
    }
}
