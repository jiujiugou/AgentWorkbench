namespace AgentWorkbench.Domain;

/// <summary>模型供应商（OpenAI 兼容端点）。API Key 明文存储。</summary>
public sealed class ProviderRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int IsActive { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>供应商下的可选模型；is_default 表示该供应商当前使用的模型。</summary>
public sealed class ProviderModelRecord
{
    public long Id { get; set; }
    public long ProviderId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int IsDefault { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
