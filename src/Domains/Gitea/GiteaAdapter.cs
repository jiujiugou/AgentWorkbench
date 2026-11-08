namespace AgentWorkbench.Gitea;

/// <summary>
/// 方案第 2 步的 Connector / 方案第 3 步目录的执行半边：
/// 把已校验的 ActionRequest 翻译成 Gitea REST 调用，再把响应翻回 ActionResult。
///
/// 三条来自 2026-11-08 真实采集、仿真写不出来的约束（见 docs/验证与现状/gitea-真实行为-20261108.md）：
/// 1. <b>PATCH 返回 201 而不是 200，也算成功</b>；判成功只看状态码集合，不看 200。
/// 2. <b>DELETE/204 无 body</b>；不允许无条件 JsonDocument.Parse(body)。
/// 3. <b>时间是 RFC3339 + Z（UTC）</b>；原样透传并标注 time_basis，绝不套用「按站点时区补偏移」。
///
/// 另外两条安全约束：
/// - 成功数据按白名单提取，不把原始响应（可能含 email 等个人信息）整体塞进审计。
/// - 403 的错误体含 token scope 清单，绝不能回给调用方；只回一个通用错误码。
/// </summary>
public sealed class GiteaAdapter : IActionExecutor
{
    /// <summary>实测：GET/POST/PATCH 成功是 200 或 201，DELETE 是 204。</summary>
    private static readonly int[] SuccessStatuses = [200, 201, 204];

    private readonly GiteaOptions _options;

    /// <summary>
    /// 延迟创建：未配置时连 HttpClient 都不构造，更不可能发出请求。
    /// 若在此处直接 new HttpGiteaTransport(Unconfigured)，会因为空 BaseUrl 抛 UriFormatException，
    /// 那反而是「fail-closed 没兜住」—— 配置缺失应当安静地返回拒绝。
    /// </summary>
    private readonly Lazy<IGiteaTransport> _transport;

    /// <summary>调用计数，供「四道不过零外部调用」「未审批零外部调用」做行为断言。</summary>
    public int HttpCallCount { get; private set; }

    public GiteaAdapter(GiteaOptions options, IGiteaTransport? transport = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = new Lazy<IGiteaTransport>(() => transport ?? new HttpGiteaTransport(options));
    }

    public async ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return Fail(request.RequestId, GiteaErrorCodes.NotConfigured,
                "Gitea 连接配置缺失；fail-closed，未发起任何 HTTP 调用");

        // M2 只读边界：写能力在目录里可见，但执行显式拒绝且零 HTTP。
        // 绝不回落到内存假实现 —— 比「不能写」更糟的是「以为写了其实写进内存」。
        if (!_options.AllowWrites && GiteaActionSpecs.IsWrite(spec))
            return Fail(request.RequestId, GiteaErrorCodes.WritesDisabled,
                "M2 只读边界：写能力未开启，未发起任何 HTTP 调用");

        var owner = GiteaArguments.ReadString(request.Arguments, "owner", _options.DefaultOwner);
        var repo = GiteaArguments.ReadString(request.Arguments, "repo", _options.DefaultRepo);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return Fail(request.RequestId, GiteaErrorCodes.InvalidPayload, "owner / repo 缺失");

        var o = GiteaArguments.EscapePath(owner);
        var r = GiteaArguments.EscapePath(repo);
        var basePath = $"api/v1/repos/{o}/{r}";

        return spec.ActionKey switch
        {
            GiteaActionSpecs.RepoQueryKey => await ReadAsync(request, $"{basePath}", GiteaSuccessCodes.Read,
                root => new
                {
                    kind = "repository",
                    full_name = Str(root, "full_name"),
                    @private = Bool(root, "private"),
                    state_permissions = Obj(root, "permissions"),
                    created_at = Str(root, "created_at"),
                    updated_at = Str(root, "updated_at"),
                    time_basis = "utc-rfc3339",
                }, ct),

            GiteaActionSpecs.IssueQueryKey => await ReadAsync(request,
                $"{basePath}/issues/{ReadNumber(request)}", GiteaSuccessCodes.Read,
                root => IssueProjection(root), ct),

            GiteaActionSpecs.IssueCreateKey => await WriteAsync(request, "POST", $"{basePath}/issues",
                JsonSerializer.Serialize(new
                {
                    title = GiteaArguments.ReadString(request.Arguments, "title", ""),
                    body = GiteaArguments.ReadString(request.Arguments, "body", ""),
                }), GiteaSuccessCodes.Created, ct),

            GiteaActionSpecs.IssueCloseKey => await WriteAsync(request, "PATCH",
                $"{basePath}/issues/{ReadNumber(request)}", """{"state":"closed"}""",
                GiteaSuccessCodes.Closed, ct),

            _ => Fail(request.RequestId, GiteaErrorCodes.InvalidPayload, $"unknown capability: {spec.ActionKey}"),
        };
    }

    private async ValueTask<ActionResult> ReadAsync(
        ActionRequest request, string path, string successCode,
        Func<JsonElement, object> projection, CancellationToken ct)
        => await SendAndProjectAsync(request, "GET", path, null, successCode, projection, ct);

    private async ValueTask<ActionResult> WriteAsync(
        ActionRequest request, string method, string path, string jsonBody,
        string successCode, CancellationToken ct)
        => await SendAndProjectAsync(request, method, path, jsonBody, successCode,
            root => IssueProjection(root), ct);

    private async ValueTask<ActionResult> SendAndProjectAsync(
        ActionRequest request, string method, string path, string? jsonBody,
        string successCode, Func<JsonElement, object> projection, CancellationToken ct)
    {
        HttpCallCount++;
        var response = await _transport.Value.SendAsync(method, path, jsonBody, ct);

        if (!SuccessStatuses.Contains(response.StatusCode))
            return MapFailure(request.RequestId, response);

        // 204 无 body（实测 DELETE）。其余情况 body 为空也按成功处理，但给出结构化的空投影。
        var root = ParseBody(response);
        object payload;
        try
        {
            payload = projection(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return Fail(request.RequestId, GiteaErrorCodes.InvalidPayload,
                "响应字段与契约不符", response.StatusCode);
        }

        var data = JsonSerializer.SerializeToElement(payload);
        var externalId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("number", out var n)
            && n.ValueKind == JsonValueKind.Number
                ? n.GetRawText()
                : null;

        return new ActionResult(request.RequestId, ActionStatuses.Succeeded, successCode,
            externalId, null, data);
    }

    private static ActionResult MapFailure(string requestId, GiteaResponse response)
    {
        // 403 的 body 含 `token scope=...`，绝不透传；只给一个通用码与不含权限结构的说明。
        var (code, reason) = response.StatusCode switch
        {
            401 => (GiteaErrorCodes.NotAuthenticated, "凭据缺失或无效"),
            403 => (GiteaErrorCodes.ScopeDenied, "当前凭据不具备该操作所需权限"),
            404 => (GiteaErrorCodes.NotFoundOrForbidden, "资源不存在，或无权查看其存在"),
            429 => (GiteaErrorCodes.RateLimited, "上游限流"),
            >= 500 => (GiteaErrorCodes.UpstreamError, "上游服务错误"),
            0 when response.TransportError == "timeout" => (GiteaErrorCodes.Timeout, "上游超时，结果未知"),
            0 => (GiteaErrorCodes.UpstreamError, "传输失败"),
            _ => (GiteaErrorCodes.UpstreamError, "未预期的上游响应"),
        };

        return Fail(requestId, code, reason, response.StatusCode);
    }

    private static object IssueProjection(JsonElement root) => new
    {
        kind = "issue",
        number = Int(root, "number"),
        state = Str(root, "state"),
        title = Str(root, "title"),
        created_at = Str(root, "created_at"),
        updated_at = Str(root, "updated_at"),
        closed_at = Str(root, "closed_at"),
        time_basis = "utc-rfc3339",
    };

    private static JsonElement ParseBody(GiteaResponse response)
    {
        if (response.StatusCode == 204 || string.IsNullOrWhiteSpace(response.Body))
            return JsonSerializer.SerializeToElement(new { });
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static int ReadNumber(ActionRequest request)
        => GiteaArguments.TryReadNumber(request.Arguments, out var n) ? n : 0;

    private static ActionResult Fail(string id, string code, string reason, int httpStatus = 0)
        => new(id, ActionStatuses.Failed, code, null, code,
            JsonSerializer.SerializeToElement(new { reason, http_status = httpStatus }));

    private static string? Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static int? Int(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i)
            ? i
            : null;

    private static JsonElement? Obj(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Object
            ? v.Clone()
            : null;
}
