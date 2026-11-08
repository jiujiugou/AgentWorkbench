using System.Text.Json;
using AgentWorkbench.Knowledge.Local.Sufficiency;
using AgentWorkbench.Knowledge.Local.Tools;
using AgentWorkbench.Shared;
using AgentWorkbench.Runtime.Tools;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>
/// 把「这次检索判为资料不足」翻译成一次终止：不再回问模型，整轮按 <see cref="AgentRunStatus.Refused"/> 收口。
/// </summary>
/// <remarks>
/// 判定不在这里重算——它读 <c>search_knowledge</c> 输出里的 <c>sufficiency</c>，那份值由工具在检索时算好。
/// 拒答闸最容易做歪的地方就是"两处各判一遍"：工具判一次决定返回什么，否决层再判一次决定停不停，
/// 两个实现迟早漂移，而且漂移时不会报错。
/// <para>
/// 解析失败一律<b>不否决</b>：格式变了应该让来源面板少一块，不该把正常对话全部拒掉。
/// 保守方向在这条链路上是单向的——宁可放过，不可误杀。
/// </para>
/// </remarks>
public sealed class KnowledgeInsufficiencyVeto : IToolResultVeto
{
    /// <inheritdoc />
    public AgentRunStop? Inspect(string toolName, string? result)
    {
        if (!string.Equals(toolName, SearchKnowledgeTool.ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result))
            return null;

        SearchKnowledgeTool.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SearchKnowledgeTool.Payload>(
                result, SearchKnowledgeTool.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || !string.Equals(payload.Sufficiency.Verdict, SufficiencyVerdict.Insufficient, StringComparison.Ordinal))
            return null;

        return new AgentRunStop(
            AgentRunStatus.Refused,
            AgentRunCode.KnowledgeInsufficient,
            KnowledgeRefusal.Text);
    }
}
