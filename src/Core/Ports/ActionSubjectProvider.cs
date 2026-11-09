namespace AgentWorkbench.Ports;

/// <summary>
/// 身份委托端口：底座侧唯一允许取「当前是谁在提动作」的入口。
///
/// 归属来自宿主的认证态，由组合根实现并注入。模型输出、请求体、工具参数一律不得提供
/// 这一项；工具面只能读，不能写。取不到身份时调用方必须按 fail-closed 拒绝，
/// 不得回退到模型给出的任何名字、也不得回退到一个默认账号。
///
/// 本接口只有读取语义：怎么把当前身份放进作用域是宿主的事，底座不定义。
/// </summary>
public interface IActionSubjectProvider
{
    /// <summary>当前已认证主体的名字；无法确认时返回 null。</summary>
    string? CurrentSubject { get; }
}
