using System.Text.Json;

namespace AgentWorkbench.Documents.Parsing;

/// <summary>纯文本文件（.txt/.md/.json）抽取：单页化，产物与 PDF 抽取同型，复用清洗/分块/索引链路。</summary>
public interface ITextFileExtractor
{
    /// <summary>该文件名是否为支持的纯文本格式。</summary>
    bool Supports(string fileName);

    Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, string fileName, CancellationToken cancellationToken);
}

public sealed class TextFileExtractor : ITextFileExtractor
{
    private static readonly string[] SupportedExtensions = [".txt", ".md", ".json"];

    public bool Supports(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        string filePath, string fileName, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken);

        if (Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            text = UnwrapJson(text);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("文件未包含有效文本内容");

        return [new ExtractedPage(1, text)];
    }

    /// <summary>
    /// JSON 约定：根对象含 <c>content</c> 或 <c>text</c> 字符串字段时取该字段作为正文，
    /// 否则整体按纯文本处理（含非法 JSON——不做强制校验，摄入宽容）。
    /// </summary>
    private static string UnwrapJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in (ReadOnlySpan<string>)["content", "text"])
                {
                    if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非法 JSON：按纯文本摄入，交给清洗/分块兜底
        }

        return raw;
    }
}
