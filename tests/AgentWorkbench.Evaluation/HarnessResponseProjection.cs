namespace AgentWorkbench.Evaluation;

/// <summary>
/// 把一次 run 的步骤序列投影成 MAF 能读的对话消息。
/// </summary>
/// <remarks>
/// 只做一件事：让框架的内置判定（<c>ToolCalledCheck</c> / <c>ToolCallsPresent</c> /
/// <c>ToolCallArgsMatch</c>）看得见「调过哪些工具、参数是什么」。
/// <para>
/// <b>刻意不投影轨迹事实</b>（<c>Truncated</c> / <c>Status</c> / <c>Code</c> / 用量）：
/// 它们不属于对话，塞进 <c>EvalItem.Context</c> 会把它当状态袋用——那个字段的语义是 grounding
/// context，塞进去等于自造协议。这些事实由 <see cref="KernelChecks"/> 的闭包按 RunId 读回来判。
/// </para>
/// <para>
/// callId 用「步骤序号」而不是工具名：同名工具被调两次时，用工具名当 id 会把两次调用压成一次，
/// 回放看到的是「调了一次」，与事实不符。
/// </para>
/// </remarks>
public static class HarnessResponseProjection
{
    /// <summary>步骤序列 → 对话消息（工具步产出 call + result 两条，答复步产出一条 assistant）。</summary>
    public static IReadOnlyList<ChatMessage> ToConversation(HarnessRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var messages = new List<ChatMessage>();
        var callSequence = 0;

        foreach (var step in result.Steps)
        {
            if (string.Equals(step.Kind, "tool", StringComparison.Ordinal))
            {
                var callId = $"call-{++callSequence}";
                messages.Add(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(callId, step.Tool ?? "unknown", ParseArguments(step.Arguments))
                ]));

                // 失败步把异常文本当结果回填：框架的 ToolCallArgsMatch 只关心「调了什么」，
                // 但回放时「失败」与「返回空」必须分得开，否则同样的绿掩盖两种事实。
                messages.Add(new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(callId, step.Error ?? step.Result ?? string.Empty)
                ]));
                continue;
            }

            // 空答复不落消息：提前终止（循环 / 预算）时模型往往没有最终文本，
            // 造一条空 assistant 会把「没有答复」写成「答复为空」。
            if (!string.IsNullOrEmpty(step.Result))
                messages.Add(new ChatMessage(ChatRole.Assistant, step.Result));
        }

        return messages;
    }

    private static IDictionary<string, object?>? ParseArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(raw);
        }
        catch (JsonException)
        {
            // 参数不是合法 JSON 时不让投影炸掉：判据要评的是「这次 run 做对了没有」，
            // 不是「模型输出合不合语法」——后者归工具管道的参数校验。
            return null;
        }
    }
}
