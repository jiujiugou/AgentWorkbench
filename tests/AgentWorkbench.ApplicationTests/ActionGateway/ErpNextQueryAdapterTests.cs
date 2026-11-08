using System.Text.Json;
using AgentWorkbench.Erp;
using AgentWorkbench.Erp.ErpNext;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// M2 只读适配器的行为验收（对应 M2 设计 §9.2 T1–T12）。
///
/// 全部走真实 <see cref="HttpClient"/> + 协议级替身 <see cref="FakeFrappeHandler"/>，
/// 因此覆盖的是 transport → 状态映射 → 解析 → 时间转换的完整链路，
/// 而不是把执行体 mock 掉只验编排。
///
/// 每条测试的注释里写了负控：改坏哪一行会让它变红。
/// </summary>
public sealed class ErpNextQueryAdapterTests
{
    private const string Tz = "Asia/Shanghai";

    private static (ErpNextQueryAdapter Adapter, FakeFrappeSite Site) Create(
        FakeFrappeSite? site = null,
        ErpNextOptions? options = null)
    {
        site ??= new FakeFrappeSite();
        options ??= new ErpNextOptions("http://fake-erp.local", site.ApiKey, site.ApiSecret, Tz);
        var http = new HttpClient(new FakeFrappeHandler(site));
        var transport = new HttpErpTransport(http, options, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        return (new ErpNextQueryAdapter(transport, options), site);
    }

    private static ActionRequest Query(string documentId = "PO-M2-0002")
        => new(
            "req-m2-1",
            "wang",
            ErpActionSpecs.QueryKey,
            JsonDocument.Parse($$"""{"document_id":"{{documentId}}"}""").RootElement.Clone(),
            "req-m2-1");

    // T1 负控：把 docstatus 读成字符串比较（"0" != 0）→ 本条红
    [Fact]
    public async Task T1_解析Draft单据()
    {
        var (adapter, site) = Create();
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query("PO-M2-0001"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal("PO-M2-0001", result.ExternalId);
        Assert.Equal(0, result.Data.GetProperty("docstatus").GetInt32());
        Assert.Equal("draft", result.Data.GetProperty("docstatus_text").GetString());
        Assert.Equal(1, site.RequestCount);
    }

    // T2 负控：把 status 硬编码成枚举（Submitted/Completed…）→ 本条红
    [Fact]
    public async Task T2_status字符串必须原样保留()
    {
        var (adapter, _) = Create();
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(1, result.Data.GetProperty("docstatus").GetInt32());
        // ERPNext 的 status 是计算值，版本间会变（To Receive and Bill / To Bill / Completed…），
        // 所以只能原样透传，不能映射成我们自己的枚举。
        Assert.Equal("To Receive and Bill", result.Data.GetProperty("status").GetString());
    }

    // T3 负控：不做时区转换、直接把 naive 串当 UTC → 偏移变成 +00:00，本条红
    [Fact]
    public async Task T3_naive时间按站点时区补偏移()
    {
        var (adapter, _) = Create();
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal("2026-11-08 10:00:00.123456", result.Data.GetProperty("erp_modified_raw").GetString());
        var at = result.Data.GetProperty("erp_modified_at").GetString();
        Assert.NotNull(at);
        Assert.EndsWith("+08:00", at);
        Assert.StartsWith("2026-11-08T10:00:00", at);
        Assert.Equal(Tz, result.Data.GetProperty("erp_time_zone").GetString());
    }

    // T4 负控：把 404 并进 5xx 分支 → 本条红（「查不到」会变成「系统错误」）
    [Fact]
    public async Task T4_单号不存在返回not_found而不是upstream_error()
    {
        var (adapter, _) = Create();
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query("PO-NOT-EXIST"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ErpErrorCodes.NotFound, result.Code);
        Assert.NotEqual(ErpErrorCodes.UpstreamError, result.Code);
        Assert.Null(result.ExternalId);
    }

    // T5 负控：403 与 404 都映射成 not_found → 本条红（越权探测会变得可行）
    [Fact]
    public async Task T5_无权限与不存在必须区分()
    {
        var site = new FakeFrappeSite();
        var (adapter, _) = Create(site, new ErpNextOptions("http://fake-erp.local", "wrong", "wrong", Tz));
        var forbidden = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        // 凭据错走 401；再单独构造 403
        Assert.Equal(ErpErrorCodes.AuthFailed, forbidden.Code);

        var site403 = new FakeFrappeSite { ForceStatus = 403 };
        var (adapter403, _) = Create(site403);
        var result = await adapter403.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ErpErrorCodes.Forbidden, result.Code);
        Assert.NotEqual(ErpErrorCodes.NotFound, result.Code);
    }

    // T6 负控：给 401 加重试 → RequestCount 变 3，本条红
    [Fact]
    public async Task T6_凭据失效不重试()
    {
        var site = new FakeFrappeSite();
        var (adapter, _) = Create(site, new ErpNextOptions("http://fake-erp.local", "bad", "bad", Tz));
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ErpErrorCodes.AuthFailed, result.Code);
        Assert.Equal(1, site.RequestCount); // 401 重试等于拿凭据去爆破
    }

    // T7 负控：去掉重试 → RequestCount 变 1，本条红
    [Fact]
    public async Task T7_上游5xx只读重试两次()
    {
        var site = new FakeFrappeSite { ForceStatus = 500 };
        var (adapter, _) = Create(site, new ErpNextOptions("http://fake-erp.local", site.ApiKey, site.ApiSecret, Tz, MaxReadRetries: 2));
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ErpErrorCodes.UpstreamError, result.Code);
        Assert.Equal(3, site.RequestCount); // 1 次 + 2 次重试
    }

    // T8 负控：缺字段时兜默认值（grand_total=0 之类）→ 本条红
    [Fact]
    public async Task T8_缺字段不猜默认值()
    {
        var site = new FakeFrappeSite { OmitField = "company" };
        var (adapter, _) = Create(site);
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ErpErrorCodes.SchemaMismatch, result.Code);
        Assert.Contains("company", result.Data.GetProperty("reason").GetString());
    }

    // T9 负控：让写原语回落到假实现 → RequestCount 变 1，本条红
    [Fact]
    public async Task T9_M2里写原语显式拒绝且零HTTP调用()
    {
        var (adapter, site) = Create();
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Submit, Query(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ErpErrorCodes.NotSupportedInM2, result.Code);
        Assert.Equal(0, site.RequestCount); // 比「不能写」更糟的是「以为写了其实写进了内存」
    }

    // T10 负控：配置缺失时回落到假 ERP → RequestCount 变 1，本条红
    [Fact]
    public async Task T10_配置缺失直接拒绝且零HTTP调用()
    {
        var (adapter, site) = Create(options: new ErpNextOptions());
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ErpErrorCodes.ConfigurationMissing, result.Code);
        Assert.Equal(0, site.RequestCount);
        Assert.Contains("BaseUrl", result.Data.GetProperty("reason").GetString());
    }

    // T11 负控：超时也判成 failed（而不是 Unknown）→ 本条红；状态未知与失败是两件事
    [Fact]
    public async Task T11_超时归Unknown且只再试一次()
    {
        var site = new FakeFrappeSite { Delay = TimeSpan.FromMilliseconds(300) };
        var options = new ErpNextOptions(
            "http://fake-erp.local", site.ApiKey, site.ApiSecret, Tz,
            Timeout: TimeSpan.FromMilliseconds(30));
        var (adapter, _) = Create(site, options);

        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Unknown, result.Status);
        Assert.Equal(ErpErrorCodes.Timeout, result.Code);
        Assert.Equal(2, site.RequestCount); // 首次 + 一次重试
    }

    // T12 负控：200 就当成功、不检查 error 载荷 → 本条红
    [Fact]
    public async Task T12_200里的Frappe错误载荷不能被当成成功()
    {
        var site = new FakeFrappeSite { ErrorPayloadWith200 = true };
        var (adapter, _) = Create(site);
        var result = await adapter.ExecuteAsync(ErpActionSpecs.Query, Query(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ErpErrorCodes.ErrorPayload, result.Code);
        Assert.Contains("ValidationError", result.Data.GetProperty("reason").GetString());
    }
}
