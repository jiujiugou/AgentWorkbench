namespace AgentWorkbench.Workflow.Steps;

/// <summary>工作流步骤扩展点：新增步骤类型 = 新增实现。</summary>
public interface IWorkflowStepExecutor
{
    /// <summary>步骤类型（与定义中的 type 对应）。</summary>
    string Type { get; }

    /// <summary>执行步骤，返回输出摘要（JSON/文本）。失败/超时应抛出。</summary>
    Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken);
}

/// <summary>步骤执行器注册表。</summary>
public sealed class WorkflowStepRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowStepExecutor> _executors;

    public WorkflowStepRegistry(IEnumerable<IWorkflowStepExecutor> executors)
        => _executors = executors.ToDictionary(e => e.Type, StringComparer.Ordinal);

    public IReadOnlyList<string> Types => _executors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public IWorkflowStepExecutor Get(string type)
        => _executors.TryGetValue(type, out var executor)
            ? executor
            : throw new InvalidOperationException($"未知步骤类型：{type}");
}
