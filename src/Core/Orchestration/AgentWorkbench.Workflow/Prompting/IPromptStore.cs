namespace AgentWorkbench.Workflow.Prompting;

/// <summary>提示词版本存储（P4）。</summary>
public interface IPromptStore
{
    Task<string?> ResolveAsync(string promptKey, string? version, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptVersionRecord>> ListAsync(string? promptKey, CancellationToken cancellationToken);

    Task<PromptVersionRecord> AddAsync(string promptKey, string version, string content, bool active, CancellationToken cancellationToken);

    Task<bool> ActivateAsync(string promptKey, string version, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string promptKey, string version, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class PromptStore : IPromptStore
{
    private readonly IWorkflowRepository _repository;

    public PromptStore(IWorkflowRepository repository) => _repository = repository;

    public async Task<string?> ResolveAsync(string promptKey, string? version, CancellationToken cancellationToken)
        => (await _repository.GetPromptAsync(promptKey, version))?.Content;

    public Task<IReadOnlyList<PromptVersionRecord>> ListAsync(string? promptKey, CancellationToken cancellationToken)
        => _repository.ListPromptsAsync(promptKey);

    public Task<PromptVersionRecord> AddAsync(string promptKey, string version, string content, bool active, CancellationToken cancellationToken)
        => _repository.AddPromptAsync(promptKey, version, content, active);

    public Task<bool> ActivateAsync(string promptKey, string version, CancellationToken cancellationToken)
        => _repository.ActivatePromptAsync(promptKey, version);

    public async Task<bool> ExistsAsync(string promptKey, string version, CancellationToken cancellationToken)
        => await _repository.GetPromptAsync(promptKey, version) is not null;
}
