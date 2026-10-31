using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Embedding;

/// <summary>
/// 失败分类表的穷举验证：这张表决定"重不重试"，写错一处就会把永久错误重试到超时、
/// 或把暂时错误直接判死。它是纯函数，所以可以逐条钉住。
/// </summary>
public sealed class EmbeddingFailureTests
{
    public static TheoryData<Exception, string, FailureKind> Cases => new()
    {
        { new HttpRequestException("connection refused"), EmbeddingFailureCode.Unavailable, FailureKind.Transient },
        { new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized), EmbeddingFailureCode.Unauthorized, FailureKind.Permanent },
        { new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden), EmbeddingFailureCode.Unauthorized, FailureKind.Permanent },
        { new HttpRequestException("not found", null, HttpStatusCode.NotFound), EmbeddingFailureCode.ModelNotFound, FailureKind.Permanent },
        { new HttpRequestException("too many", null, HttpStatusCode.TooManyRequests), EmbeddingFailureCode.RateLimited, FailureKind.Transient },
        { new HttpRequestException("bad request", null, HttpStatusCode.BadRequest), EmbeddingFailureCode.BadRequest, FailureKind.Permanent },
        { new HttpRequestException("server error", null, HttpStatusCode.InternalServerError), EmbeddingFailureCode.Unavailable, FailureKind.Transient },
        { new HttpRequestException("gateway", null, HttpStatusCode.BadGateway), EmbeddingFailureCode.Unavailable, FailureKind.Transient },
        { new TimeoutException("timed out"), EmbeddingFailureCode.Timeout, FailureKind.Transient },
        { new TaskCanceledException("timeout", new TimeoutException()), EmbeddingFailureCode.Timeout, FailureKind.Transient },
        { new OperationCanceledException(), EmbeddingFailureCode.Cancelled, FailureKind.NotFailure },
        { new SocketException(), EmbeddingFailureCode.Unavailable, FailureKind.Transient },
        { new IOException("reset"), EmbeddingFailureCode.Unavailable, FailureKind.Transient },
        { new JsonException("bad json"), EmbeddingFailureCode.BadResponse, FailureKind.Permanent },
        { new InvalidOperationException("Embedding 未配置（Knowledge:Embedding:Provider = none）。"), EmbeddingFailureCode.NotConfigured, FailureKind.Permanent },
        { new InvalidOperationException("Embedding 数量不匹配：1 != 2"), EmbeddingFailureCode.CountMismatch, FailureKind.Permanent },
        { new NotSupportedException("未归类的古怪异常"), EmbeddingFailureCode.Unclassified, FailureKind.Permanent }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_映射到预期的码与可处置性(
        Exception exception, string expectedCode, FailureKind expectedKind)
    {
        var failure = EmbeddingFailure.Classify(exception);

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(expectedKind == FailureKind.Transient, failure.IsTransient);
    }

    [Fact]
    public void Classify_未归类异常按永久处理()
    {
        // 未归类多半是代码缺陷：重试只会把一次失败拖成 N 次等待，并推迟暴露问题。
        var failure = EmbeddingFailure.Classify(new InvalidProgramException("boom"));

        Assert.Equal(FailureKind.Permanent, failure.Kind);
    }
}
