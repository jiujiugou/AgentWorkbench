using System.Diagnostics;
using System.Text.Json;
using AgentWorkbench.Domain;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Controllers;

/// <summary>供应商新增/编辑请求（PUT 时 ApiKey 留空表示保持不变）。</summary>
public sealed record ProviderUpsertRequest(string? Name, string? BaseUrl, string? ApiKey);

/// <summary>新增模型请求。</summary>
public sealed record ModelAddRequest(string? Model, string? DisplayName);

/// <summary>拉取模型列表请求（用于保存前用临时凭据探测）。</summary>
public sealed record FetchModelsRequest(string? BaseUrl, string? ApiKey);

[ApiController]
[Route("api/[controller]")]
public sealed class SettingsController : ControllerBase
{
    private readonly IProviderRepository _providers;
    private readonly IChatClientFactory _chatFactory;
    private readonly ProviderOptions _options;
    private readonly KnowledgeOptions _knowledge;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        IProviderRepository providers,
        IChatClientFactory chatFactory,
        IOptions<ProviderOptions> options,
        IOptions<KnowledgeOptions> knowledge,
        IHttpClientFactory httpFactory,
        ILogger<SettingsController> logger)
    {
        _providers = providers;
        _chatFactory = chatFactory;
        _options = options.Value;
        _knowledge = knowledge.Value;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>非敏感的当前模型配置预览（兼容旧设置页）。</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var active = await _providers.GetActiveProviderAsync();
        var endpoint = await _chatFactory.ResolveAsync(null, cancellationToken);
        _logger.LogDebug("Get settings: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return Ok(new
        {
            baseUrl = endpoint?.BaseUrl ?? _options.BaseUrl,
            model = endpoint?.Model ?? _options.Model,
            embeddingModel = _knowledge.Embedding.Model,
            temperature = _options.Temperature,
            systemPrompt = _options.SystemPrompt,
            apiKeyConfigured = !string.IsNullOrWhiteSpace(endpoint?.ApiKey),
            configured = endpoint is not null,
            activeProviderId = active?.Id
        });
    }

    // ---------------- 供应商 ----------------

    [HttpGet("providers")]
    public async Task<IActionResult> ListProviders(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var providers = await _providers.ListProvidersAsync();
        var result = new List<object>(providers.Count);
        foreach (var provider in providers)
            result.Add(ToDto(provider, await _providers.ListModelsAsync(provider.Id)));
        _logger.LogDebug("List providers: {Count} in {ElapsedMs}ms", result.Count, sw.ElapsedMilliseconds);
        return Ok(result);
    }

    [HttpPost("providers")]
    public async Task<IActionResult> CreateProvider([FromBody] ProviderUpsertRequest body)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(body.BaseUrl))
        {
            _logger.LogWarning("CreateProvider failed: BaseUrl empty");
            return BadRequest(new { message = "BaseUrl 不能为空。" });
        }

        var name = string.IsNullOrWhiteSpace(body.Name) ? "未命名供应商" : body.Name!.Trim();
        var provider = await _providers.CreateProviderAsync(name, body.BaseUrl!.Trim(), body.ApiKey?.Trim() ?? "");
        _logger.LogInformation("CreateProvider: {Name} (id={Id}) in {ElapsedMs}ms", name, provider.Id, sw.ElapsedMilliseconds);
        return Ok(ToDto(provider, []));
    }

    [HttpPut("providers/{id:long}")]
    public async Task<IActionResult> UpdateProvider(long id, [FromBody] ProviderUpsertRequest body)
    {
        var sw = Stopwatch.StartNew();
        var existing = await _providers.GetProviderAsync(id);
        if (existing is null)
        {
            _logger.LogWarning("UpdateProvider not found: {Id}", id);
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(body.BaseUrl))
        {
            _logger.LogWarning("UpdateProvider failed: BaseUrl empty");
            return BadRequest(new { message = "BaseUrl 不能为空。" });
        }

        var name = string.IsNullOrWhiteSpace(body.Name) ? existing.Name : body.Name!.Trim();
        var apiKey = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey!.Trim();
        await _providers.UpdateProviderAsync(id, name, body.BaseUrl!.Trim(), apiKey);

        var updated = await _providers.GetProviderAsync(id);
        _logger.LogInformation("UpdateProvider: {Id} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return Ok(ToDto(updated!, await _providers.ListModelsAsync(id)));
    }

    [HttpDelete("providers/{id:long}")]
    public async Task<IActionResult> DeleteProvider(long id)
    {
        var sw = Stopwatch.StartNew();
        var deleted = await _providers.DeleteProviderAsync(id);
        _logger.LogInformation("DeleteProvider: {Id} Deleted={Deleted} in {ElapsedMs}ms", id, deleted, sw.ElapsedMilliseconds);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("providers/{id:long}/activate")]
    public async Task<IActionResult> ActivateProvider(long id)
    {
        var sw = Stopwatch.StartNew();
        var activated = await _providers.ActivateProviderAsync(id);
        _logger.LogInformation("ActivateProvider: {Id} Activated={Activated} in {ElapsedMs}ms", id, activated, sw.ElapsedMilliseconds);
        return activated ? Ok(new { activeProviderId = id }) : NotFound();
    }

    // ---------------- 模型 ----------------

    [HttpPost("providers/{id:long}/models")]
    public async Task<IActionResult> AddModel(long id, [FromBody] ModelAddRequest body)
    {
        var sw = Stopwatch.StartNew();
        if (await _providers.GetProviderAsync(id) is null)
        {
            _logger.LogWarning("AddModel provider not found: {Id}", id);
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(body.Model))
        {
            _logger.LogWarning("AddModel failed: empty model name");
            return BadRequest(new { message = "模型名不能为空。" });
        }

        var record = await _providers.AddModelAsync(id, body.Model!.Trim(), body.DisplayName?.Trim());
        _logger.LogInformation("AddModel: {Model} (id={Id}) for provider {ProviderId} in {ElapsedMs}ms", record.Model, record.Id, id, sw.ElapsedMilliseconds);
        return Ok(ToModelDto(record));
    }

    [HttpDelete("models/{id:long}")]
    public async Task<IActionResult> DeleteModel(long id)
    {
        var sw = Stopwatch.StartNew();
        var deleted = await _providers.DeleteModelAsync(id);
        _logger.LogInformation("DeleteModel: {Id} Deleted={Deleted} in {ElapsedMs}ms", id, deleted, sw.ElapsedMilliseconds);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("providers/{providerId:long}/models/{modelId:long}/default")]
    public async Task<IActionResult> SetDefaultModel(long providerId, long modelId)
    {
        var sw = Stopwatch.StartNew();
        var updated = await _providers.SetDefaultModelAsync(providerId, modelId);
        _logger.LogInformation("SetDefaultModel: provider={ProviderId} model={ModelId} Updated={Updated} in {ElapsedMs}ms", providerId, modelId, updated, sw.ElapsedMilliseconds);
        return updated ? NoContent() : NotFound();
    }

    // ---------------- 探测 ----------------

    [HttpPost("providers/{id:long}/fetch-models")]
    public async Task<IActionResult> FetchModelsForProvider(long id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var provider = await _providers.GetProviderAsync(id);
        if (provider is null)
        {
            _logger.LogWarning("FetchModelsForProvider not found: {Id}", id);
            return NotFound();
        }
        var result = await FetchModelsAsync(provider.BaseUrl, provider.ApiKey, cancellationToken);
        _logger.LogDebug("FetchModelsForProvider: {Id} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return result;
    }

    [HttpPost("fetch-models")]
    public Task<IActionResult> FetchModels([FromBody] FetchModelsRequest body, CancellationToken cancellationToken)
        => FetchModelsAsync(body.BaseUrl ?? "", body.ApiKey ?? "", cancellationToken);

    [HttpPost("providers/{id:long}/test")]
    public async Task<IActionResult> TestProvider(long id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var provider = await _providers.GetProviderAsync(id);
        if (provider is null)
        {
            _logger.LogWarning("TestProvider not found: {Id}", id);
            return NotFound();
        }

        try
        {
            var models = await QueryModelsAsync(provider.BaseUrl, provider.ApiKey, cancellationToken);
            sw.Stop();
            _logger.LogInformation("TestProvider: {Id} OK Latency={LatencyMs}ms Count={Count}", id, sw.ElapsedMilliseconds, models.Count);
            return Ok(new { ok = true, latencyMs = sw.ElapsedMilliseconds, count = models.Count });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "TestProvider: {Id} Failed Latency={LatencyMs}ms Error={Error}", id, sw.ElapsedMilliseconds, ex.Message);
            return Ok(new { ok = false, latencyMs = sw.ElapsedMilliseconds, error = ex.Message });
        }
    }

    // ---------------- 辅助 ----------------

    private async Task<IActionResult> FetchModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { message = "BaseUrl 不能为空。" });
        try
        {
            var models = await QueryModelsAsync(baseUrl, apiKey, cancellationToken);
            return Ok(new { models });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchModels failed: {BaseUrl}", baseUrl);
            return BadRequest(new { message = $"拉取模型失败：{ex.Message}" });
        }
    }

    private async Task<List<string>> QueryModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var url = baseUrl.TrimEnd('/') + "/models";
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        var models = new List<string>();
        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            CollectIdArray(document.RootElement, models);
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
                 && document.RootElement.TryGetProperty("data", out var data)
                 && data.ValueKind == JsonValueKind.Array)
        {
            CollectIdArray(data, models);
        }

        _logger.LogDebug("QueryModels: {BaseUrl} Count={Count} ElapsedMs={ElapsedMs}", baseUrl, models.Count, sw.ElapsedMilliseconds);
        return models.Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectIdArray(JsonElement array, List<string> models)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                models.Add(item.GetString()!);
            }
            else if (item.ValueKind == JsonValueKind.Object
                     && item.TryGetProperty("id", out var id)
                     && id.ValueKind == JsonValueKind.String)
            {
                models.Add(id.GetString()!);
            }
        }
    }

    private static object ToDto(ProviderRecord provider, IReadOnlyList<ProviderModelRecord> models) => new
    {
        id = provider.Id,
        name = provider.Name,
        baseUrl = provider.BaseUrl,
        apiKeyMasked = Mask(provider.ApiKey),
        hasApiKey = !string.IsNullOrWhiteSpace(provider.ApiKey),
        isActive = provider.IsActive == 1,
        updatedAt = provider.UpdatedAt,
        models = models.Select(ToModelDto).ToList()
    };

    private static object ToModelDto(ProviderModelRecord model) => new
    {
        id = model.Id,
        providerId = model.ProviderId,
        model = model.Model,
        displayName = model.DisplayName,
        isDefault = model.IsDefault == 1
    };

    private static string Mask(string apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey.Length <= 4 ? "****" : "****" + apiKey[^4..];
}
