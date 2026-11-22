namespace AgentWorkbench.ApplicationTests.ActionGateway;

using System.Text.Json;
using AgentWorkbench.Erp;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Mcp;
using Xunit;

/// <summary>
/// 四条 MCP 绑定的离网判据：只验「动作 → 工具调用」的翻译，不碰真站点。
/// 每条负控都能指出「改哪里会红」：把绑定里的固定字段放开、或让业务参数穿透到顶层，立即失败。
/// </summary>
public sealed class ErpMcpBindingTests
{
    private const string CreatedDoc = """{"data":{"name":"PO-MCP-00001","docstatus":0,"remarks":"123.45"}}""";
    private const string UpdatedDoc = """{"data":{"name":"PUR-ORD-2026-00001","docstatus":2}}""";

    [Fact]
    public void All_ExposesFourBindings()
    {
        var executor = Executor();

        Assert.Equal(4, executor.BoundActionKeys.Count);
        Assert.Contains(ErpActionSpecs.QueryKey, executor.BoundActionKeys);
        Assert.Contains(ErpActionSpecs.DraftKey, executor.BoundActionKeys);
        Assert.Contains(ErpActionSpecs.SubmitKey, executor.BoundActionKeys);
        Assert.Contains(ErpActionSpecs.CancelKey, executor.BoundActionKeys);
    }

    [Fact]
    public async Task Draft_UsesCreateDoc_AndPutsAmountInsideData()
    {
        var invoker = new StubInvoker(ErpMcpBindings.DefaultDraftTool, CreatedDoc);
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Draft,
            Request(ErpActionSpecs.DraftKey, """{"document_id":"PO-1108","amount":123.45}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal("PO-MCP-00001", result.ExternalId);
        var call = Assert.Single(invoker.Calls);
        Assert.Equal(ErpMcpBindings.DefaultDraftTool, call.ToolName);
        Assert.Equal("Purchase Order", ReadString(call.Arguments, "doctype"));
        Assert.Equal(123.45, ReadData(call.Arguments).GetProperty("remarks").GetDouble());
        // 负控点：name 由对方命名序列分配，拟用单号不能冒充；amount 也不能漏到顶层。
        Assert.False(call.Arguments.TryGetProperty("name", out _));
        Assert.False(call.Arguments.TryGetProperty("document_id", out _));
        Assert.False(call.Arguments.TryGetProperty("amount", out _));
    }

    [Fact]
    public async Task Cancel_UsesUpdateDoc_AndWritesOnlyDocstatusTwo()
    {
        var invoker = new StubInvoker(ErpMcpBindings.DefaultCancelTool, UpdatedDoc);
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Cancel,
            Request(ErpActionSpecs.CancelKey, """{"document_id":"PUR-ORD-2026-00001","reason":"重复下单"}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal("PUR-ORD-2026-00001", result.ExternalId);
        var call = Assert.Single(invoker.Calls);
        Assert.Equal(ErpMcpBindings.DefaultCancelTool, call.ToolName);
        Assert.Equal("PUR-ORD-2026-00001", ReadString(call.Arguments, "name"));
        Assert.Equal("Purchase Order", ReadString(call.Arguments, "doctype"));
        var field = Assert.Single(ReadData(call.Arguments).EnumerateObject());
        Assert.Equal("docstatus", field.Name);
        Assert.Equal(2, field.Value.GetInt32());
        Assert.False(call.Arguments.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Draft_RejectsRequestTryingToOverrideFixedDoctype()
    {
        var invoker = new StubInvoker(ErpMcpBindings.DefaultDraftTool, CreatedDoc);
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Draft,
            Request(ErpActionSpecs.DraftKey, """{"document_id":"PO-1108","amount":1,"doctype":"Sales Invoice"}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(McpActionCodes.InvalidArguments, result.Code);
        Assert.Empty(invoker.Calls); // 拒绝必须发生在调用之前
    }

    [Fact]
    public async Task Cancel_DropsRequestProvidedDataObject()
    {
        // 请求试图自带 data 改字段；未声明的参数被丢在门外，写入只剩固定的 docstatus=2。
        // 负控点：若把 "data" 加进 Cancel 的 ArgumentNames，本条会因额外字段进入字段闸而变红。
        var invoker = new StubInvoker(ErpMcpBindings.DefaultCancelTool, UpdatedDoc);
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Cancel,
            Request(ErpActionSpecs.CancelKey,
                """{"document_id":"PUR-ORD-2026-00001","data":{"docstatus":1,"remarks":"偷改"}}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        var call = Assert.Single(invoker.Calls);
        var field = Assert.Single(ReadData(call.Arguments).EnumerateObject());
        Assert.Equal("docstatus", field.Name);
        Assert.Equal(2, field.Value.GetInt32());
    }

    [Fact]
    public async Task Submit_StillWritesOnlyDocstatusOne()
    {
        var invoker = new StubInvoker(ErpMcpBindings.DefaultSubmitTool, UpdatedDoc);
        var executor = new McpActionExecutor(invoker, ErpMcpBindings.All());

        var result = await executor.ExecuteAsync(
            ErpActionSpecs.Submit,
            Request(ErpActionSpecs.SubmitKey, """{"document_id":"PUR-ORD-2026-00001","amount":999}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        var call = Assert.Single(invoker.Calls);
        var field = Assert.Single(ReadData(call.Arguments).EnumerateObject());
        Assert.Equal("docstatus", field.Name);
        Assert.Equal(1, field.Value.GetInt32());
    }

    [Fact]
    public void DataFieldsWithoutHolder_FailsFastAtConstruction()
    {
        var bad = new McpActionBinding("x", "mcp__erpnext__frappe_create_doc",
            DataFields: new Dictionary<string, string> { ["amount"] = "remarks" });

        Assert.Throws<InvalidOperationException>(() => new McpActionExecutor(new StubInvoker(), [bad]));
    }

    private static McpActionExecutor Executor()
        => new(new StubInvoker(), ErpMcpBindings.All());

    private static ActionRequest Request(string key, string json)
        => new("req-test", "wang", key, JsonDocument.Parse(json).RootElement.Clone(), "idem-test");

    private static string ReadString(JsonElement args, string name)
        => args.GetProperty(name).GetString()!;

    private static JsonElement ReadData(JsonElement args) => args.GetProperty("data");

    private sealed class StubInvoker : IToolInvoker
    {
        private readonly Dictionary<string, string> _replies = new(StringComparer.Ordinal);

        public StubInvoker() { }

        public StubInvoker(string toolName, string reply) => _replies[toolName] = reply;

        public List<(string ToolName, JsonElement Arguments)> Calls { get; } = [];

        public bool Has(string toolName) => _replies.ContainsKey(toolName);

        public ValueTask<ToolInvocationResult> InvokeAsync(
            string toolName, JsonElement arguments, CancellationToken ct)
        {
            Calls.Add((toolName, arguments.Clone()));
            return ValueTask.FromResult(_replies.TryGetValue(toolName, out var reply)
                ? ToolInvocationResult.Success(reply)
                : ToolInvocationResult.Failure(ToolInvocationCodes.NotFound, "未在桩目录"));
        }
    }
}
