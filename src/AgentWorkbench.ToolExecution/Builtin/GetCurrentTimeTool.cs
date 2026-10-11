namespace AgentWorkbench.ToolExecution.Builtin;

/// <summary>工具 <c>get_current_time</c>：返回当前 UTC 时间（ISO-8601），只读、无副作用。</summary>
public sealed class GetCurrentTimeTool : IAgentToolProvider
{
    public const string ToolName = "get_current_time";

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            () => DateTimeOffset.UtcNow.ToString("o"),
            ToolName,
            "返回当前 UTC 时间（ISO-8601）。")
    ];
}
