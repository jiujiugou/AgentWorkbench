using System.Text.Json;
using AgentWorkbench.Ports;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>MCP 执行体的失败归类。与动作错误码并列，供审计与归因。</summary>
public static class McpActionCodes
{
    /// <summary>动作没有绑定到任何工具 = 这个能力不存在。</summary>
    public const string NoBinding = "mcp_no_binding";
    public const string ToolNotFound = "mcp_tool_not_found";
    public const string InvocationFailed = "mcp_invocation_failed";
    /// <summary>写入参数里出现了没有声明的字段（含 <c>docstatus</c> 这类状态字段）。</summary>
    public const string FieldDenied = "mcp_field_denied";
    /// <summary>取不到业务键。有副作用的动作拿不到业务键 = 事后无法查证，直接拒。</summary>
    public const string BusinessKeyMissing = "mcp_business_key_missing";
    public const string InvalidArguments = "mcp_invalid_arguments";
    public const string Ok = "mcp_ok";
}

/// <summary>
/// 用外部工具实现动作执行：把一次 <see cref="ActionRequest"/> 翻译成一次工具调用。
/// </summary>
/// <remarks>
/// <para>
/// 它坐在<b>四道闸之后</b>：到了这里，Schema 已校验、权限已放行、业务规则已通过、审批已落。
/// 所以本类不做任何「允不允许」的判断 —— 它只负责三件确定性的事：
/// 参数怎么对应、写入字段的范围、以及对方给出的业务键怎么取回来。
/// </para>
/// <para>
/// 但它守两条<b>结构性</b>的下限，因为这两条不依赖具体业务、而是「能不能事后查证」的前提：
/// 一、写入参数里出现未声明的字段一律拒（否则一次 update 就能顺手改 <c>docstatus</c>）；
/// 二、有副作用的动作必须能取到业务键，取不到就拒（拿不到单号，就永远无法回查这张单）。
/// </para>
/// </remarks>
public sealed class McpActionExecutor : IActionExecutor
{
    private readonly IToolInvoker _invoker;
    private readonly IReadOnlyDictionary<string, McpActionBinding> _bindings;

    public McpActionExecutor(IToolInvoker invoker, IEnumerable<McpActionBinding> bindings)
    {
        _invoker = invoker;

        var map = new Dictionary<string, McpActionBinding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (!map.TryAdd(binding.ActionKey, binding))
                throw new InvalidOperationException(
                    $"同一个动作只能有一条工具绑定：{binding.ActionKey}（{map[binding.ActionKey].ToolName} 与 {binding.ToolName}）");
        }

        _bindings = map;
    }

    /// <summary>当前已绑定的动作键。组合根与自检用，不参与决策。</summary>
    public IReadOnlyCollection<string> BoundActionKeys => (IReadOnlyCollection<string>)_bindings.Keys;

    public async ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        if (!_bindings.TryGetValue(spec.ActionKey, out var binding))
            return Reject(request.RequestId, McpActionCodes.NoBinding,
                $"动作 {spec.ActionKey} 没有工具绑定");

        if (!_invoker.Has(binding.ToolName))
            return Fail(request.RequestId, McpActionCodes.ToolNotFound,
                $"工具不在当前目录：{binding.ToolName}");

        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return Fail(request.RequestId, McpActionCodes.InvalidArguments, "参数必须是 JSON 对象");

        var arguments = MapArguments(request.Arguments, binding);
        if (arguments is null)
            return Fail(request.RequestId, McpActionCodes.InvalidArguments,
                "参数里出现了绑定的常量字段，不允许覆盖");

        var denied = FirstDeniedField(arguments.Value, binding);
        if (denied is not null)
            return Reject(request.RequestId, McpActionCodes.FieldDenied, denied);

        ToolInvocationResult called;
        try
        {
            called = await _invoker.InvokeAsync(binding.ToolName, arguments.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (!called.Ok)
            return Fail(request.RequestId, called.Code, called.Message ?? "工具调用失败");

        var content = called.Content ?? string.Empty;
        var data = ToData(content);
        var externalId = ReadBusinessKey(content, binding.BusinessKeyPath);

        // 有副作用却拿不到业务键：这次调用发生过，但事后无法按业务键回查 —— 比失败更危险。
        if (externalId is null && spec.SideEffecting)
            return new ActionResult(
                request.RequestId, ActionStatuses.Unknown, McpActionCodes.BusinessKeyMissing, null,
                McpActionCodes.BusinessKeyMissing,
                JsonSerializer.SerializeToElement(new { reason = "外部已可能产生副作用，但返回里取不到业务键", raw = Truncate(content) }));

        return new ActionResult(
            request.RequestId, ActionStatuses.Succeeded, McpActionCodes.Ok, externalId, null, data);
    }

    /// <summary>
    /// 业务参数 → 工具参数：<b>只转发绑定里声明过的</b>，改名、并入常量。
    /// </summary>
    /// <remarks>
    /// 「未声明的丢在门外」是刻意的，不是省事：
    /// <list type="bullet">
    /// <item>业务参数里有只给判定用的字段（金额档位要用的 <c>amount</c>），它不该出现在对方系统的请求里；</item>
    /// <item>对方工具的 schema 大多写了 <c>additionalProperties:false</c>，多带一个字段就是一次被拒的调用；</item>
    /// <item>转发范围等于对外暴露面，它必须是可读的一行行声明，而不是「请求里有什么就发什么」。</item>
    /// </list>
    /// </remarks>
    /// <returns>null 表示请求试图覆盖绑定里的常量。</returns>
    private static JsonElement? MapArguments(JsonElement source, McpActionBinding binding)
    {
        var mapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in source.EnumerateObject())
        {
            if (binding.Constants.ContainsKey(property.Name))
                return null;

            if (!binding.Names.TryGetValue(property.Name, out var name))
                continue;

            mapped[name] = property.Value.Clone();
        }

        foreach (var constant in binding.Constants)
            mapped[constant.Key] = JsonSerializer.SerializeToElement(constant.Value);

        return JsonSerializer.SerializeToElement(mapped);
    }

    /// <summary>
    /// 字段闸：写入对象的键必须都在白名单里。返回 null = 放行，否则返回拒绝原因。
    /// </summary>
    /// <remarks>
    /// 为什么必须有这一道：通用的 <c>update_document(doctype, name, fields)</c> 里，
    /// <c>fields={"remarks":"x"}</c> 是改备注、<c>fields={"docstatus":1}</c> 是提交。
    /// 不解释字段就分不开这两件事，而「提交」本该走它自己的动作与审批。
    /// </remarks>
    private static string? FirstDeniedField(JsonElement arguments, McpActionBinding binding)
    {
        if (!binding.GuardsFields)
            return null;

        var holder = binding.WritableFieldsArgument!;
        if (!arguments.TryGetProperty(holder, out var fields))
            return null;

        if (fields.ValueKind != JsonValueKind.Object)
            return $"参数 {holder} 必须是对象";

        var allow = binding.WritableFields ?? [];
        foreach (var field in fields.EnumerateObject())
        {
            if (!allow.Contains(field.Name, StringComparer.Ordinal))
                return $"字段 {field.Name} 不在 {holder} 的白名单里";
        }

        return null;
    }

    /// <summary>按点号路径取业务键，如 <c>data.name</c>。取不到返回 null，不猜。</summary>
    private static string? ReadBusinessKey(string content, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(content))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var node = document.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                    return null;
            }

            return node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
        }
    }

    /// <summary>返回体原样进 <c>data</c>：解析得了就当 JSON，解析不了就包一层 raw，不丢内容。</summary>
    private static JsonElement ToData(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return JsonSerializer.SerializeToElement(new { });

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = Truncate(content) });
        }
    }

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];

    private static ActionResult Fail(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Failed, code, null, code,
            JsonSerializer.SerializeToElement(new { reason }));

    private static ActionResult Reject(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Rejected, code, null, code,
            JsonSerializer.SerializeToElement(new { reason }));
}
