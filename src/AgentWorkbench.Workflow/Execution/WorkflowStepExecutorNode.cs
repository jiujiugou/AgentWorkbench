using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;
using Microsoft.Agents.AI.Workflows;

namespace AgentWorkbench.Workflow.Execution;

/// <summary>
/// 单个工作流步骤的 MAF 执行器：包装 <see cref="IWorkflowStepExecutor"/>，
/// 负责步骤级重试/超时/取消、状态持久化与进度通知；已 <c>succeeded</c> 的步骤直接放行（检查点）。
/// </summary>
internal sealed class WorkflowStepExecutorNode(
    WorkflowStepDefinition definition,
    IWorkflowStepExecutor executor,
    IWorkflowRepository repository,
    IPromptStore prompts,
    IWorkflowProgressNotifier notifier,
    ILogger<WorkflowStepExecutorNode> logger)
    : Executor<WorkflowStepMessage, WorkflowStepMessage>(definition.Key)
{
    public override async ValueTask<WorkflowStepMessage> HandleAsync(
        WorkflowStepMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var step = message.Steps.First(s => string.Equals(s.StepKey, definition.Key, StringComparison.Ordinal));

        if (string.Equals(step.Status, WorkflowStepStatuses.Succeeded, StringComparison.Ordinal))
            return message; // 检查点：已完成步骤跳过

        step.Status = WorkflowStepStatuses.Running;
        step.StartedAt = DateTimeOffset.UtcNow.ToString("o");
        step.Error = null;
        step.FinishedAt = null;
        await repository.UpdateStepAsync(step);
        await NotifyAsync(message, step, cancellationToken);

        var promptContent = string.IsNullOrWhiteSpace(definition.PromptKey)
            ? null
            : await prompts.ResolveAsync(definition.PromptKey!, message.Run.PromptVersion, cancellationToken);

        var stepContext = new WorkflowStepContext(
            message.Run, step, definition, message.DocumentId, message.FileName,
            message.Chunks, promptContent, message.Steps);

        try
        {
            var output = await ExecuteWithRetryAsync(step, stepContext, cancellationToken);
            step.Status = WorkflowStepStatuses.Succeeded;
            step.Output = output;
            step.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            step.Error = null;
            await repository.UpdateStepAsync(step);
            await NotifyAsync(message, step, cancellationToken);
            return message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            step.Status = WorkflowStepStatuses.Cancelled;
            step.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            await repository.UpdateStepAsync(step);
            await NotifyAsync(message, step, CancellationToken.None);
            throw;
        }
        catch (WorkflowStepFailedException ex)
        {
            step.Status = WorkflowStepStatuses.Failed;
            step.Error = ex.Message;
            step.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            await repository.UpdateStepAsync(step);
            await NotifyAsync(message, step, CancellationToken.None);
            throw;
        }
    }

    private async Task<string> ExecuteWithRetryAsync(
        WorkflowStepRecord step, WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, definition.MaxAttempts);
        var timeoutSeconds = Math.Max(1, definition.TimeoutSeconds);

        while (true)
        {
            step.Attempts++;
            var attemptContext = context with { Step = step };
            await repository.UpdateStepAsync(step);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                return await executor.ExecuteAsync(attemptContext, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                if (step.Attempts >= maxAttempts)
                    throw new WorkflowStepFailedException(
                        $"步骤 {definition.Key} 超时（{timeoutSeconds}s，已尝试 {step.Attempts} 次）", ex);
                logger.LogWarning("Step {StepKey} timeout, retry {Attempt}/{Max}", definition.Key, step.Attempts, maxAttempts);
            }
            catch (Exception ex)
            {
                if (step.Attempts >= maxAttempts)
                    throw new WorkflowStepFailedException($"步骤 {definition.Key} 失败：{ex.Message}", ex);
                logger.LogWarning(ex, "Step {StepKey} failed, retry {Attempt}/{Max}", definition.Key, step.Attempts, maxAttempts);
            }
        }
    }

    private Task NotifyAsync(WorkflowStepMessage message, WorkflowStepRecord step, CancellationToken cancellationToken)
        => notifier.NotifyAsync(
            message.Run.Id, step.StepKey, step.Status, step.Attempts, step.Output, cancellationToken);
}
