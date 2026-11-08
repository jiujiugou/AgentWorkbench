using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Context;

/// <summary>历史消息的通用形态。由调用方从自己的存储类型映射过来，本模块不依赖任何存储模块。</summary>
/// <param name="Role">角色，取 <c>user</c> / <c>assistant</c>；其余值按 user 处理。</param>
/// <param name="Content">消息正文。</param>
public sealed record ContextTurn(string Role, string Content);

/// <summary>一段可注入的参考片段（检索命中、前置步骤输出等）。</summary>
/// <param name="Title">片段标题，用于注入时的标号说明。</param>
/// <param name="Content">片段正文。</param>
/// <param name="Source">来源标识（文件名 / 步骤名 / 页码）；可为空。</param>
public sealed record ContextFragment(string Title, string Content, string? Source = null);

/// <summary>一次组装的输入：本轮输入 +（可选）历史 / 指令 / 片段。</summary>
/// <param name="Input">用户本轮输入原文。</param>
/// <param name="History">历史消息；由调用方按自己的存储映射成 <see cref="ContextTurn"/>。</param>
/// <param name="Instructions">run 级系统指令（人格 / 引用规范）；注入到模型请求，不写入会话历史。</param>
/// <param name="Fragments">需要注入的参考片段。</param>
public sealed record ContextRequest(
    string Input,
    IReadOnlyList<ContextTurn>? History = null,
    string? Instructions = null,
    IReadOnlyList<ContextFragment>? Fragments = null);

/// <summary>一次组装的输出。<see cref="Messages"/> 只含历史 + 本轮输入；指令与片段由装饰器注入。</summary>
/// <param name="Messages">要交给模型的对话消息（不含 system 注入）。</param>
/// <param name="Instructions">合并后的系统指令（含片段格式化结果）；无内容时为 <c>null</c>。</param>
/// <param name="Fragments">实际注入的片段（已按预算截断）。</param>
public sealed record ContextBundle(
    IReadOnlyList<ChatMessage> Messages,
    string? Instructions,
    IReadOnlyList<ContextFragment> Fragments);
