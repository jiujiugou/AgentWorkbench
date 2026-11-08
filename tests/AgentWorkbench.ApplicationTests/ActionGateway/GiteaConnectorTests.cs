using System.Net;
using System.Net.Http;
using System.Text.Json;
using AgentWorkbench.Gitea;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 方案第 2 步「Connector」的验收。三条来自真实采集的坑（201 / 204 / UTC）各有一条断言，
/// 都必须能被改坏成红 —— 仿真写不出这三条，所以它们是本轮最值钱的测试。
/// </summary>
public sealed class GiteaConnectorTests
{
    // G1：配置缺失必须 fail-closed 且零 HTTP。
    [Fact]
    public async Task G1_配置缺失零HTTP直接拒绝()
    {
        var stub = new StubGiteaTransport();
        var set = new GiteaActionSet(GiteaOptions.Unconfigured, stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.RepoQuery, GiteaTestData.Req(GiteaActionSpecs.RepoQueryKey),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(GiteaErrorCodes.NotConfigured, result.ErrorCode);
        Assert.Equal(0, stub.CallCount);
        Assert.Equal(0, set.Adapter.HttpCallCount);
    }

    // G2：M2 只读边界，写能力在目录里可见但执行被拒，绝不回落内存假实现。
    [Fact]
    public async Task G2_写能力未开启时零HTTP()
    {
        var stub = new StubGiteaTransport();
        var set = new GiteaActionSet(GiteaTestData.Configured(allowWrites: false), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.IssueCreate,
            GiteaTestData.Req(GiteaActionSpecs.IssueCreateKey, """{"title":"[AW] x"}"""),
            CancellationToken.None);

        Assert.Equal(GiteaErrorCodes.WritesDisabled, result.ErrorCode);
        Assert.Equal(0, stub.CallCount);
    }

    // G3/G4/G5：实测状态码映射。401 凭据、403 scope、404 不存在或无权看到。
    [Theory]
    [InlineData(401, GiteaErrorCodes.NotAuthenticated)]
    [InlineData(403, GiteaErrorCodes.ScopeDenied)]
    [InlineData(404, GiteaErrorCodes.NotFoundOrForbidden)]
    public async Task G3_401_403_404按实测映射(int status, string expectedCode)
    {
        var stub = new StubGiteaTransport { DefaultResponse = new GiteaResponse(status, GiteaTestData.ScopeDeniedBody) };
        var set = new GiteaActionSet(GiteaTestData.Configured(), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.RepoQuery, GiteaTestData.Req(GiteaActionSpecs.RepoQueryKey),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    // G4：403 的真实 body 含 `token scope=...` 权限清单，绝不能带出。
    // 负控：把 MapFailure 改成把 response.Body 塞进 Data → 本条红。
    [Fact]
    public async Task G4_scope拒绝不得带出权限清单()
    {
        var stub = new StubGiteaTransport
        {
            DefaultResponse = new GiteaResponse(403, GiteaTestData.ScopeDeniedBody),
        };
        var set = new GiteaActionSet(GiteaTestData.Configured(), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.RepoQuery, GiteaTestData.Req(GiteaActionSpecs.RepoQueryKey),
            CancellationToken.None);

        var data = GiteaTestData.DataOf(result);
        Assert.DoesNotContain("token scope", data);
        Assert.DoesNotContain("write:issue", data);
        Assert.DoesNotContain("read:repository", data);
    }

    // G6：PATCH 关闭议题实测返回 201，不是 200。判成功只写 200 会误判为失败。
    // 负控：把 SuccessStatuses 里的 201 删掉 → 本条红。
    [Fact]
    public async Task G6_201也算成功_PATCH的真实状态码()
    {
        var stub = new StubGiteaTransport { DefaultResponse = new GiteaResponse(201, GiteaTestData.IssueBody) };
        var set = new GiteaActionSet(GiteaTestData.Configured(allowWrites: true), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.IssueClose,
            GiteaTestData.Req(GiteaActionSpecs.IssueCloseKey, """{"number":1}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal("PATCH", stub.Calls[0].Method);
        Assert.Contains("\"state\":\"closed\"", stub.Calls[0].Body);
    }

    // G7：204 无 body。无条件 JsonDocument.Parse 会在这里抛异常。
    // 负控：删掉 ParseBody 里的 StatusCode == 204 分支 → 本条红。
    [Fact]
    public async Task G7_204空body不崩且算成功()
    {
        var stub = new StubGiteaTransport { DefaultResponse = new GiteaResponse(204, "") };
        var set = new GiteaActionSet(GiteaTestData.Configured(allowWrites: true), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.IssueClose,
            GiteaTestData.Req(GiteaActionSpecs.IssueCloseKey, """{"number":1}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal(JsonValueKind.Object, result.Data.ValueKind);
    }

    // G8：Gitea 时间是 RFC3339 + Z（UTC），原样透传，绝不补 +08:00 偏移。
    // 负控：把 time_basis 改成本地时区 / 对 created_at 做偏移换算 → 本条红。
    [Fact]
    public async Task G8_UTC时间原样透传不补偏移()
    {
        var stub = new StubGiteaTransport { DefaultResponse = new GiteaResponse(200, GiteaTestData.IssueBody) };
        var set = new GiteaActionSet(GiteaTestData.Configured(), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.IssueQuery,
            GiteaTestData.Req(GiteaActionSpecs.IssueQueryKey, """{"number":1}"""),
            CancellationToken.None);

        var data = GiteaTestData.DataOf(result);
        Assert.Contains("\"created_at\":\"2026-11-08T10:27:43Z\"", data);
        Assert.Contains("\"time_basis\":\"utc-rfc3339\"", data);
        Assert.DoesNotContain("+08:00", data);
    }

    // G9：实测 GET /user 会返回 email。响应必须按白名单投影，不得整体入审计。
    // 负控：把 projection 改成直接返回 root → 本条红。
    [Fact]
    public async Task G9_响应按白名单投影不带出email()
    {
        var stub = new StubGiteaTransport { DefaultResponse = new GiteaResponse(200, GiteaTestData.RepoBody) };
        var set = new GiteaActionSet(GiteaTestData.Configured(), stub);

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.RepoQuery, GiteaTestData.Req(GiteaActionSpecs.RepoQueryKey),
            CancellationToken.None);

        var data = GiteaTestData.DataOf(result);
        Assert.DoesNotContain("should-not-leak@example.com", data);
        Assert.DoesNotContain("\"email\"", data);
        Assert.Contains("\"full_name\":\"owner/repo\"", data);
        Assert.Contains("\"admin\":true", data);
    }

    // G10：读可重试、401/403/404 不重试。
    // 负控：把 ShouldRetry 改成包含 401/403/404 → 后两条红。
    [Fact]
    public async Task G10_5xx重试且403不重试()
    {
        var retryHandler = new SequenceHandler(n => n < 3
            ? Response(HttpStatusCode.InternalServerError, "{}")
            : Response(HttpStatusCode.OK, GiteaTestData.RepoBody));
        var set = new GiteaActionSet(GiteaTestData.Configured(),
            new HttpGiteaTransport(GiteaTestData.Configured(), retryHandler));

        var result = await set.Executor.ExecuteAsync(
            GiteaActionSpecs.RepoQuery, GiteaTestData.Req(GiteaActionSpecs.RepoQueryKey),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal(3, retryHandler.CallCount);

        var denyHandler = new SequenceHandler(_ => Response(HttpStatusCode.Forbidden, GiteaTestData.ScopeDeniedBody));
        var denied = await new HttpGiteaTransport(GiteaTestData.Configured(), denyHandler)
            .SendAsync("GET", "api/v1/repos/o/r", null, CancellationToken.None);

        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(1, denyHandler.CallCount);
    }

    // G11：调用方取消必须传播，不能伪装成上游超时。
    // 负控：删掉 `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` → 本条红。
    [Fact]
    public async Task G11_调用方取消会传播()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var transport = new HttpGiteaTransport(GiteaTestData.Configured(),
            new SequenceHandler(_ => Response(HttpStatusCode.OK, "{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.SendAsync("GET", "api/v1/user", null, cts.Token));
    }

    // G12：超时查证只能用只读能力。
    [Fact]
    public void G12_超时查证目标是只读能力()
    {
        var set = new GiteaActionSet();
        Assert.Equal(GiteaActionSpecs.IssueQueryKey, set.QuerySpec.ActionKey);
        Assert.False(set.QuerySpec.SideEffecting);
        Assert.False(set.QuerySpec.RequiresApproval);
    }

    private static HttpResponseMessage Response(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body) };

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 刻意不检查 cancellationToken：让 transport 的取消检查成为唯一来源。
            // 若替身自己抛异常，G11 测的就是替身而不是 HttpGiteaTransport（假绿）。
            CallCount++;
            return Task.FromResult(factory(CallCount));
        }
    }
}
