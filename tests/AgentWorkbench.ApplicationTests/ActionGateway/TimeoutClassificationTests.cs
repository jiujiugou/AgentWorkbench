namespace AgentWorkbench.ApplicationTests.ActionGateway;

using System.Text.Json;
using AgentWorkbench.Erp;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Mcp;
using Xunit;

/// <summary>
/// 第 4 句「超时只查证不重放」的前置机制：把「不知道成没成」和「对方明确拒绝」分开。
/// 负控：把 ToolFailureClassifier 的传输关键字删空，或把执行体的 Unavailable 分支去掉，本文件会红。
/// </summary>
public sealed class TimeoutClassificationTests
{
    [Theory]
    [InlineData("Cannot reach Frappe at https://x. Is the site running?")]
    [InlineData("Request to https://x timed out after 30s.")]
    [InlineData("FrappeConnectionError: connection refused")]
    [InlineData("httpx.ConnectError: dial tcp failed")]
    [InlineData("mcp tool_unavailable: process not started")]
    public void 传输故障归为不可用(string message)
        => Assert.True(ToolFailureClassifier.IsTransportFailure(null, message));

    [Theory]
    [InlineData("417 EXPECTATION FAILED")]
    [InlineData("frappe.exceptions.MandatoryError: supplier, items")]
    [InlineData("Permission denied (403)")]
    [InlineData("ValidationError: grand_total must be positive")]
    [InlineData("")]
    public void 对方拒绝不归为传输故障(string message)
        => Assert.False(ToolFailureClassifier.IsTransportFailure(null, message));

    [Fact]
    public void CSharp传输异常归为不可用()
        => Assert.True(ToolFailureClassifier.IsTransportFailure(
            new System.Net.Http.HttpRequestException("connect failed"), ""));

    [Fact]
    public void CSharp业务异常不归为传输故障()
        => Assert.False(ToolFailureClassifier.IsTransportFailure(
            new InvalidOperationException("schema mismatch"), ""));

    [Fact]
    public async Task 执行体遇传输故障返回Unknown而非Failed()
    {
        var invoker = new StubInvoker(
            ErpMcpBindings.DefaultCancelTool,
            ToolInvocationResult.Failure(ToolInvocationCodes.Unavailable, "Cannot reach Frappe"));
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Cancel,
            Request("""{"document_id":"PUR-ORD-2026-00009"}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Unknown, result.Status);
        Assert.Equal(McpActionCodes.UnknownTransport, result.Code);
    }

    [Fact]
    public async Task 执行体遇对方拒绝仍返回Failed()
    {
        // 负控点：若把 Unavailable→Unknown 放宽成「所有 !Ok 都 Unknown」，本条会红。
        var invoker = new StubInvoker(
            ErpMcpBindings.DefaultCancelTool,
            ToolInvocationResult.Failure(ToolInvocationCodes.Failed, "417 MandatoryError: supplier, items"));
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Cancel,
            Request("""{"document_id":"PUR-ORD-2026-00009"}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ToolInvocationCodes.Failed, result.Code);
    }

    private static ActionRequest Request(string json)
        => new("req-timeout-test", "wang", ErpActionSpecs.CancelKey,
            JsonDocument.Parse(json).RootElement.Clone(), "idem-timeout-test");

    private sealed class StubInvoker : IToolInvoker
    {
        private readonly string _toolName;
        private readonly ToolInvocationResult _reply;

        public StubInvoker(string toolName, ToolInvocationResult reply)
        {
            _toolName = toolName;
            _reply = reply;
        }

        public bool Has(string toolName) => toolName == _toolName;

        public ValueTask<ToolInvocationResult> InvokeAsync(
            string toolName, JsonElement arguments, CancellationToken ct)
            => ValueTask.FromResult(_reply);
    }
}
