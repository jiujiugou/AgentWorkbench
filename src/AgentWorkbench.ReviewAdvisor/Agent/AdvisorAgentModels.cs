using System.Text.Json.Serialization;

namespace AgentWorkbench.ReviewAdvisor.Agent;

/// <summary>一条可核对引用。</summary>
public sealed record AdvisorCitation(long ChunkId, int Seq, string Evidence);

/// <summary>单条 finding 的结论草稿（落库前）。</summary>
public sealed record AdvisorConclusionDraft(
    long? FindingId, string Status, string Title, string? Conclusion, string? Suggestion, string? Impact,
    string Basis, double Confidence, string Source, long? ChunkId, int? Seq, int? PageFrom, int? PageTo,
    string? Evidence, string? Citations, int? StepsUsed);

/// <summary>Agent Loop 单步草稿。</summary>
public sealed record AdvisorStepDraft(long? FindingId, int StepIndex, string Kind, string? Tool, string? Arguments, string? Result);

/// <summary>Agent Loop 结果：结论 + 轨迹。</summary>
public sealed record AdvisorLoopResult(AdvisorConclusionDraft Conclusion, IReadOnlyList<AdvisorStepDraft> Steps);

/// <summary>模型最终结论（原生函数调用循环结束后的文本 JSON）。</summary>
internal sealed class AdvisorAnswer
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
    [JsonPropertyName("suggestion")] public string? Suggestion { get; set; }
    [JsonPropertyName("impact")] public string? Impact { get; set; }
    [JsonPropertyName("citations")] public List<AdvisorAnswerCitation>? Citations { get; set; }
}

internal sealed class AdvisorAnswerCitation
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }
}

/// <summary>结论 prompt（常量；P4 再做版本化存储）。</summary>
public static class AdvisorPrompts
{
    public const string Version = "advisor-p0.2";

    public const string System =
        "你是严谨的合同审查助手。你只能依据提供的证据片段作答，禁止编造；" +
        "结论、建议、影响说明都不得出现胜负、责任归属或法律意见，只做文档层面的补正/核对建议。" +
        "你可以调用只读工具补充证据：retrieve_evidence(query)、read_chunk(seq)、locate_evidence(keyword)。" +
        "当证据足以形成结论时，必须停止调用工具，直接输出一个 JSON 对象作为最终答案：" +
        "{\"title\":\"...\",\"conclusion\":\"...\",\"suggestion\":\"...\",\"impact\":\"...\",\"citations\":[{\"seq\":N,\"evidence\":\"原文片段\"}]}。" +
        "不要输出该 JSON 以外的任何内容，不要使用 Markdown 代码块。";

    public static string BuildUser(ReviewFindingRecord finding, IReadOnlyList<string> observations)
    {
        var obs = observations.Count == 0 ? "（暂无）" : string.Join("\n", observations);
        return
            $"【待审查问题】\n类别：{finding.Category}\n标题：{finding.Title}\n细节：{finding.Detail}\n期望：{finding.Expected}\n实际：{finding.Actual}\n\n" +
            $"【已获得证据】\n{obs}\n\n请决定下一步。";
    }
}
