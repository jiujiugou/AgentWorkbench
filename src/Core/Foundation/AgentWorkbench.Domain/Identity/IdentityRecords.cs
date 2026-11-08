namespace AgentWorkbench.Domain;

/// <summary>租户（客户单位）：多租户的最小隔离单元。</summary>
/// <remarks>
/// 一个租户下可有多个用户；用户与租户的绑定存在 <see cref="UserRecord.TenantId"/>。
/// 阶段 0.5 只把「谁」与「会话归属」接上，文档/知识库/轨迹的隔离在下一阶段补齐。
/// </remarks>
public sealed class TenantRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>用户（登录主体）：属于且只属于一个租户。</summary>
/// <remarks>
/// <see cref="UserName"/> 全局唯一：登录时先靠它定位用户，才能知道落在哪个租户
/// （若用户名只在租户内唯一，登录表单就得先选租户，那是另一个交互模型）。
/// 密码只存 PBKDF2 派生值（见 Webapi/Auth/PasswordHasher），库中不出现明文。
/// </remarks>
public sealed class UserRecord
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = string.Empty;
}
