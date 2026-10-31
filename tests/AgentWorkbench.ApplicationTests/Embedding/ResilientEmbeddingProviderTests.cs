using System.Net;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Embedding;

/// <summary>
/// 装饰器的负控：不只验"不崩"，更要验"该重试的重试了、不该重试的没白等、形状不对必须失败"。
/// </summary>
/// <remarks>
/// 这些断言对应三个真实事故模式：
/// ① 暂时性失败被当永久失败 → 一次网络抖动就让整篇文档没向量；
/// ② 永久失败被无脑重试 → key 过期时白等三倍超时才报错；
/// ③ "成功但维度不对"被放行 → 向量库长期带脏数据而无人发现。
/// </remarks>
public sealed class ResilientEmbeddingProviderTests
{
    private const int Dimensions = 1024;

    private static readonly IReadOnlyList<string> TwoTexts = ["第一段", "第二段"];

    [Fact]
    public async Task 暂时性失败_重试到上限后抛带归因的异常()
    {
        var stub = new StubProvider((_, _) => throw new HttpRequestException("connection refused"));
        var provider = Create(stub);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => provider.EmbedAsync(TwoTexts, CancellationToken.None));

        Assert.Equal(3, stub.Calls); // 1 次尝试 + 2 次重试
        Assert.Equal(EmbeddingFailureCode.Unavailable, ex.Failure.Code);
        Assert.Equal(FailureKind.Transient, ex.Failure.Kind);
        Assert.Equal(1, provider.FailureCount);
        Assert.NotNull(provider.LastFailure);
    }

    [Fact]
    public async Task 永久性失败_不重试()
    {
        var stub = new StubProvider((_, _) => throw new HttpRequestException(
            "unauthorized", null, HttpStatusCode.Unauthorized));
        var provider = Create(stub);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => provider.EmbedAsync(TwoTexts, CancellationToken.None));

        Assert.Equal(1, stub.Calls); // key 错重试再多次也不会对
        Assert.Equal(EmbeddingFailureCode.Unauthorized, ex.Failure.Code);
        Assert.Equal(FailureKind.Permanent, ex.Failure.Kind);
    }

    [Fact]
    public async Task 重试后成功_返回结果且不记失败()
    {
        var attempt = 0;
        var stub = new StubProvider((texts, _) =>
        {
            attempt++;
            if (attempt == 1)
                throw new HttpRequestException("connection refused");
            return Task.FromResult(Vectors(texts.Count));
        });
        var provider = Create(stub);

        var vectors = await provider.EmbedAsync(TwoTexts, CancellationToken.None);

        Assert.Equal(2, stub.Calls);
        Assert.Equal(2, vectors.Length);
        Assert.Null(provider.LastFailure);
        Assert.Equal(0, provider.FailureCount);
    }

    [Fact]
    public async Task 维度不符_立即失败且不重试()
    {
        // 换模型但没改 Dimensions：sqlite-vec 的 FLOAT[1024] 是硬约束，放行就是长期污染。
        var stub = new StubProvider((texts, _) => Task.FromResult(Vectors(texts.Count, width: 768)));
        var provider = Create(stub);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => provider.EmbedAsync(TwoTexts, CancellationToken.None));

        Assert.Equal(1, stub.Calls);
        Assert.Equal(EmbeddingFailureCode.DimensionMismatch, ex.Failure.Code);
        Assert.Equal(FailureKind.Permanent, ex.Failure.Kind);
    }

    [Fact]
    public async Task 条数不符_立即失败()
    {
        var stub = new StubProvider((_, _) => Task.FromResult(Vectors(1)));
        var provider = Create(stub);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => provider.EmbedAsync(TwoTexts, CancellationToken.None));

        Assert.Equal(EmbeddingFailureCode.CountMismatch, ex.Failure.Code);
    }

    [Fact]
    public async Task 调用方取消_原样冒泡不算失败()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubProvider((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var provider = Create(stub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.EmbedAsync(TwoTexts, cts.Token));

        Assert.Equal(0, provider.FailureCount); // 用户中断不是系统故障
        Assert.Null(provider.LastFailure);
    }

    [Fact]
    public async Task 输入为空_不算失败且不触达内层()
    {
        var stub = new StubProvider((_, _) => throw new InvalidOperationException("不该被调用"));
        var provider = Create(stub);

        var vectors = await provider.EmbedAsync([], CancellationToken.None);

        Assert.Empty(vectors);
        Assert.Equal(0, stub.Calls);
        Assert.Null(provider.LastFailure);
    }

    private static ResilientEmbeddingProvider Create(IEmbeddingProvider inner)
        => new(
            inner,
            NullLogger<ResilientEmbeddingProvider>.Instance,
            new EmbeddingOptions { Dimensions = Dimensions, TimeoutSeconds = 5, MaxRetryAttempts = 2 },
            retryDelay: TimeSpan.Zero);

    private static float[][] Vectors(int count, int width = Dimensions)
        => Enumerable.Range(0, count).Select(_ => new float[width]).ToArray();

    private sealed class StubProvider(
        Func<IReadOnlyList<string>, CancellationToken, Task<float[][]>> impl) : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public string Name => "stub";
        public string Model => "stub-model";
        public bool IsConfigured => true;
        public int Dimensions => ResilientEmbeddingProviderTests.Dimensions;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls++;
            return impl(texts, cancellationToken);
        }
    }
}
