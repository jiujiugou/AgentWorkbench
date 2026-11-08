using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Runtime.Context;

/// <inheritdoc />
internal sealed class ContextAssembler(IOptions<ContextOptions> options) : IContextAssembler
{
    public ContextOptions Options => options.Value;

    public ContextBundle Assemble(ContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("本轮输入不能为空。", nameof(request));

        var history = TrimHistory(request.History);
        var messages = new List<ChatMessage>(history.Count + 1);
        foreach (var turn in history)
            messages.Add(new ChatMessage(ResolveRole(turn.Role), turn.Content));

        // 本轮输入永远单独成一条 user 消息：历史由会话存储负责，组装层不把两者揉成一个字符串，
        // 否则回放与轨迹里只剩一段拼接文本，分不清「模型看到的上一轮」和「用户这次说的话」。
        messages.Add(new ChatMessage(ChatRole.User, request.Input));

        var fragments = TrimFragments(request.Fragments);
        var instructions = Compose(request.Instructions, fragments);
        return new ContextBundle(messages, instructions, fragments);
    }

    public IChatClient Wrap(IChatClient inner, ContextBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!Options.Enabled || string.IsNullOrWhiteSpace(bundle.Instructions))
            return inner;

        return new ContextInjectingChatClient(inner, bundle.Instructions);
    }

    public ChatHistoryProvider CreateChatHistoryProvider()
        => new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions());

    /// <summary>只保留最近 N 条历史；<c>&lt;= 0</c> 表示不截断。</summary>
    private IReadOnlyList<ContextTurn> TrimHistory(IReadOnlyList<ContextTurn>? history)
    {
        if (history is null || history.Count == 0)
            return [];

        var limit = Options.MaxHistoryMessages;
        if (limit <= 0 || history.Count <= limit)
            return history;

        return history.Skip(history.Count - limit).ToList();
    }

    /// <summary>按总字符预算顺序保留片段，最后一段可截断；<c>&lt;= 0</c> 表示不截断。</summary>
    private IReadOnlyList<ContextFragment> TrimFragments(IReadOnlyList<ContextFragment>? fragments)
    {
        if (fragments is null || fragments.Count == 0)
            return [];

        var limit = Options.MaxFragmentChars;
        if (limit <= 0)
            return fragments;

        var kept = new List<ContextFragment>(fragments.Count);
        var used = 0;
        foreach (var fragment in fragments)
        {
            var remaining = limit - used;
            if (remaining <= 0)
                break;

            if (fragment.Content.Length <= remaining)
            {
                kept.Add(fragment);
                used += fragment.Content.Length;
                continue;
            }

            kept.Add(fragment with { Content = fragment.Content[..remaining] });
            break;
        }

        return kept;
    }

    /// <summary>把指令与片段合成一条 system 文本；两者都为空时返回 <c>null</c>（不注入）。</summary>
    private static string? Compose(string? instructions, IReadOnlyList<ContextFragment> fragments)
    {
        var hasInstructions = !string.IsNullOrWhiteSpace(instructions);
        if (!hasInstructions && fragments.Count == 0)
            return null;

        var builder = new StringBuilder();
        if (hasInstructions)
            builder.AppendLine(instructions!.Trim());

        if (fragments.Count > 0)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine("【参考片段】");
            for (var i = 0; i < fragments.Count; i++)
            {
                var fragment = fragments[i];
                var label = string.IsNullOrWhiteSpace(fragment.Source)
                    ? fragment.Title
                    : $"{fragment.Title} · {fragment.Source}";
                builder.AppendLine($"[{i + 1}] {label}");
                builder.AppendLine(fragment.Content.Trim());
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static ChatRole ResolveRole(string role)
        => string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? ChatRole.Assistant
            : ChatRole.User;
}
