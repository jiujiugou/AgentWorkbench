using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Sufficiency;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>
/// 检索结果否决：模型调了 MCP 检索工具、判定为「资料不足」时终止这一轮生成。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定留在 C#，不交给 Python server。</b>ADR 0013 把「资料够不够」划在持有状态的一侧；
/// 工具侧只负责把 hits 摆出来。把 verdict 塞进工具返回，等于让被校验的一方自己出结论。
/// 这里把 MCP 的 JSON 还原成 <see cref="RetrievalHit"/> 后交给既有的
/// <see cref="SufficiencyGate"/>，判据不在否决层重写第二遍。
/// </para>
/// <para>
/// <b>能力边界（不要误读成已闭环）</b>：Python server 目前只跑 bigram 全文路，返回
/// <c>textScore</c>（BM25），没有向量距离。因此 <c>SufficiencyGate</c> 实际只会命中
/// <c>no_candidates</c> 这一条；其余三条（no_winner / too_far / answered）会落在
/// <c>undetermined / no_vector</c>，不会触发否决。这是本次移出检索后真实丢失的能力，
/// 不是完成态；等 Python 侧提供向量距离后无需改这里，判定自动恢复。
/// </para>
/// <para>
/// 解析失败一律<b>不否决</b>：格式变了应该让来源面板少一块，不该把正常对话全部拒掉。
/// 保守方向在这条链路上是单向的——宁可放过，不可误杀。
/// </para>
/// </remarks>
public sealed class McpKnowledgeVeto : IToolResultVeto
{
    private const string ToolName = "mcp__rag__search_knowledge";

    private static readonly JsonSerializerOptions HitOptions = new(JsonSerializerDefaults.Web);

    public AgentRunStop? Inspect(string toolName, string? result)
    {
        if (!string.Equals(toolName, ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result))
            return null;

        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("hits", out var hitsElement)
                || hitsElement.ValueKind != JsonValueKind.Array)
                return null; // 不是检索结果（工具报错对象），不否决

            var hits = hitsElement.Deserialize<List<RetrievalHit>>(HitOptions) ?? [];
            var verdict = SufficiencyGate.Judge(hits, SufficiencyOptions.Unset);
            if (!string.Equals(verdict.Verdict, SufficiencyVerdict.Insufficient, StringComparison.Ordinal))
                return null;

            return new AgentRunStop(
                AgentRunStatus.Refused,
                AgentRunCode.KnowledgeInsufficient,
                KnowledgeRefusal.Text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
