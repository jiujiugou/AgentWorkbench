using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;
using Microsoft.Agents.AI.Workflows;

namespace AgentWorkbench.Workflow.Execution;

/// <summary>
/// 单个工作流步骤的 MAF 执行器：包装 <see cref="IWorkflowStepExecutor"/>，
/// 负责步骤级策略、幂等登记与台账写入。
/// </summary>
/// <remarks>
/// 恢复放行<b>不在这里判</b>：执行进度由 MAF <c>CheckpointManager</c> 快照决定，
/// <c>workflow_steps</c> 只是供查询与审计的业务台账（ADR 0018 #1）。
/// </remarks>
internal sealed class WorkflowStepExecutorNode(
    WorkflowStepDefinition definition,
    IWorkflowStepExecutor executor,
    IWorkflowRepository repository,
    IPromptStore prompts,
    IWorkflowProgressNotifier notifier,
    IWorkflowIdempotencyStore idempotency,
    ILogger<WorkflowStepExecutorNode> logger)
    : Executor<WorkflowStepMessage, WorkflowStepMessage>(definition.Key)
{
    public override async ValueTask<WorkflowStepMessage> HandleAsync(
        WorkflowStepMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var step = message.Steps.First(s => string.Equals(s.StepKey, definition.Key, StringComparison.Ordinal));
        var key = IdempotencyKey(message.Run.Id);
        var lease = await idempotency.AcquireAsync(key, cancellationToken);

        if (string.Equals(lease.State, WorkflowIdempotencyStates.Completed, StringComparison.Ordinal))
        {
            // 重放：外部动作已经成功过，直接复用产物，绝不产生第二次副作用。
            step.Status = WorkflowStepStatuses.Succeeded;
            step.Output = lease.Output;
            step.ExternalRef = lease.ExternalRef;
            step.IdempotencyKey = key;
            step.Error = null;
            step.FinishedAt ??= DateTimeOffset.UtcNow.ToString("o");
            await repository.UpdateStepAsync(step);
            await NotifyAsync(message, step, cancellationToken);
            return message;
        }

        if (string.Equals(lease.State, WorkflowIdempotencyStates.Unknown, StringComparison.Ordinal))
            throw new WorkflowStepFailedException(
                $"步骤 {definition.Key} 的副作用状态未知（幂等键 {key}）；需查证并 Resolve 后才能继续，不自动重放");

        step.Status = WorkflowStepStatuses.Running;
        step.IdempotencyKey = key;
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
            message.Chunks, promptContent, message.Steps, key);

        try
        {
            var output = await ExecuteWithRetryAsync(step, stepContext, cancellationToken);

            // 先登记幂等再写台账：进程在两步之间挂掉时，重放会命中 completed 而不重做动作。
            await idempotency.CompleteAsync(key, output, step.ExternalRef, cancellationToken);

            step.Status = WorkflowStepStatuses.Succeeded;
            step.Output = output;
            step.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            step.Error = null;
            await repository.UpdateStepAsync(step);
            await NotifyAsync(message, step, cancellationToken);
            return message;
        }
        catch (WorkflowStepTimeoutException ex)
        {
            await idempotency.MarkUnknownAsync(key, ex.Message, CancellationToken.None);
            await FailStepAsync(message, step, ex.Message);
            throw;
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
            await FailStepAsync(message, step, ex.Message);
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
                // B2：超时后外部系统可能已经写成功，状态未知。绝不重放，转查证。
                throw new WorkflowStepTimeoutException(
                    $"步骤 {definition.Key} 超时（{timeoutSeconds}s，第 {step.Attempts} 次）；"
                  + "副作用状态未知，需查证后继续，不自动重放", ex);
            }
            catch (Exception ex)
            {
                if (step.Attempts >= maxAttempts)
                    throw new WorkflowStepFailedException($"步骤 {definition.Key} 失败：{ex.Message}", ex);
                logger.LogWarning(ex, "Step {StepKey} failed, retry {Attempt}/{Max}", definition.Key, step.Attempts, maxAttempts);
            }
        }
    }

    private async Task FailStepAsync(
        WorkflowStepMessage message, WorkflowStepRecord step, string error)
    {
        step.Status = WorkflowStepStatuses.Failed;
        step.Error = error;
        step.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
        await repository.UpdateStepAsync(step);
        await NotifyAsync(message, step, CancellationToken.None);
    }

    private Task NotifyAsync(WorkflowStepMessage message, WorkflowStepRecord step, CancellationToken cancellationToken)
        => notifier.NotifyAsync(
            message.Run.Id, step.StepKey, step.Status, step.Attempts, step.Output, cancellationToken);

    private string IdempotencyKey(long runId) => $"{runId}:{definition.Key}";
}
