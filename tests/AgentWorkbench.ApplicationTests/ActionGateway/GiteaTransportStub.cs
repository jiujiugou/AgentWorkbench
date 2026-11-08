namespace AgentWorkbench.ApplicationTests.ActionGateway;

using System.Text.Json;
using AgentWorkbench.Erp;
using AgentWorkbench.Gitea;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;

/// <summary>
/// 协议级替身：只替换 IGiteaTransport，Adapter 的请求映射、状态映射、投影全部走真实代码。
/// 不是「返回假 ActionResult」的 mock —— 那样的测试证明不了 Connector。
/// </summary>
public sealed class StubGiteaTransport : IGiteaTransport
{
    public List<(string Method, string Path, string? Body)> Calls { get; } = [];
    public Queue<GiteaResponse> Responses { get; } = new();
    public GiteaResponse DefaultResponse { get; set; } = new(200, "{}");

    public int CallCount => Calls.Count;

    public ValueTask<GiteaResponse> SendAsync(string method, string path, string? jsonBody, CancellationToken ct)
    {
        Calls.Add((method, path, jsonBody));
        return ValueTask.FromResult(Responses.Count > 0 ? Responses.Dequeue() : DefaultResponse);
    }

    public void Enqueue(int status, string body) => Responses.Enqueue(new GiteaResponse(status, body));
}

internal static class GiteaTestData
{
    public static GiteaOptions Configured(bool allowWrites = false)
        => new("https://gitea.example", "test-token", "owner", "repo", AllowWrites: allowWrites);

    public static ActionRequest Req(string actionKey, string args = "{}")
        => new("req-1", "wang", actionKey,
            JsonDocument.Parse(args).RootElement.Clone(), null);

    /// <summary>真实采集到的仓库响应形状（已删去无关字段，但权限自述与时间格式一致）。</summary>
    public const string RepoBody = """
        {"full_name":"owner/repo","private":false,"created_at":"2026-11-08T10:22:41Z",
         "updated_at":"2026-11-08T10:27:43Z","email":"should-not-leak@example.com",
         "permissions":{"admin":true,"push":true,"pull":true}}
        """;

    /// <summary>真实采集到的议题响应形状。</summary>
    public const string IssueBody = """
        {"number":1,"state":"open","title":"[AW] probe",
         "created_at":"2026-11-08T10:27:43Z","updated_at":"2026-11-08T10:27:43Z",
         "closed_at":null,"email":"should-not-leak@example.com"}
        """;

    /// <summary>实测 403 的真实错误体：含完整 token scope 清单，绝不能回给调用方。</summary>
    public const string ScopeDeniedBody = """
        {"message":"token does not have at least one of required scope(s), required=[write:issue], token scope=read:repository,read:user"}
        """;

    public static string DataOf(ActionResult result) => result.Data.GetRawText();
}
