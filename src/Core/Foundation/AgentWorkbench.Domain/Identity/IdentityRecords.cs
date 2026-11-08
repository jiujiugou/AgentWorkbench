namespace AgentWorkbench.Domain;

/// <summary>租户（客户单位）：多租户的最小隔离单元。</summary>
/// <remarks>
/// 一个租户下可有多个用户。用户主体是 <c>Persistence.AppUser : IdentityUser</c>，
/// 通过其 <c>TenantId</c> 绑定到这里；认证、密码、锁定、并发控制全部由 ASP.NET Core Identity 负责，
/// 本层不再持有密码派生值，也不再有自建的用户表实体。
/// </remarks>
public sealed class TenantRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
