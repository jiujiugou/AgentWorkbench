using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Model;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime;

/// <summary>
/// 模型运行时端口化的判据：内核只认识 <see cref="IModelEndpointSource"/>，
/// 四级回退与「没配模型 → null」的语义留在内核，实现在 Persistence。
/// </summary>
/// <remarks>
/// 这一格不属于六部件里的任何一格（六格是「一次 run 的六个动作」，调哪家模型是 run 之前的环境事实），
/// 所以 agent-parts.toml 里没有登记、也没有门禁会变红。这里的用例就是它唯一的常驻判据。
/// </remarks>
public sealed class 模型运行时端口
{
    // ── 结构：编译期依赖方向 ──────────────────────────────────────────

    // 负控：给 Runtime.Model 加回一条 Persistence / Domain 引用 → 本条红。
    [Fact]
    public void 模型运行时只引Shared_不拖存储与领域进编译期()
    {
        var csproj = Read("src", "Core", "Runtime", "AgentWorkbench.Runtime.Model",
            "AgentWorkbench.Runtime.Model.csproj");

        var referenced = csproj.Split('\n')
            .Where(line => line.Contains("ProjectReference", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        var only = Assert.Single(referenced);
        Assert.Contains("AgentWorkbench.Shared", only, StringComparison.Ordinal);
    }

    [Fact]
    public void 实现方向是Persistence指向Model_内核源码里没有仓储词汇()
    {
        var persistence = Read("src", "Implementations", "AgentWorkbench.Persistence",
            "AgentWorkbench.Persistence.csproj");
        Assert.Contains("AgentWorkbench.Runtime.Model.csproj", persistence, StringComparison.Ordinal);

        var modelDir = Path.Combine(RepoRoot(), "src", "Core", "Runtime", "AgentWorkbench.Runtime.Model");
        // 只扫代码行：注释里写「实现在 Persistence」是解释，不是编译期依赖。
        // 把注释算进去会误红，和「只扫含注释全文」那类假绿是同一个坑的两面。
        var sources = Directory.GetFiles(modelDir, "*.cs")
            .Select(File.ReadAllText)
            .Select(StripComments)
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.DoesNotContain("IProviderRepository", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentWorkbench.Persistence", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentWorkbench.Domain", source, StringComparison.Ordinal);
        }
    }

    private static string StripComments(string source)
        => string.Join('\n', source.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                        && !line.StartsWith("/*", StringComparison.Ordinal)
                        && !line.StartsWith("*", StringComparison.Ordinal))
            .Select(line => line.Split("//", 2)[0]));

    // 负控：把 ActiveModelProvider 的某个字段换成 ProviderRecord → 本条红。
    [Fact]
    public void 端口值对象只带内核词汇_不含数据库记录类型()
    {
        foreach (var property in typeof(ActiveModelProvider).GetProperties())
        {
            var assembly = property.PropertyType.Assembly.GetName().Name;
            Assert.DoesNotContain("AgentWorkbench.Domain", assembly ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentWorkbench.Persistence", assembly ?? string.Empty, StringComparison.Ordinal);
        }
    }

    // ── 行为：四级回退链与 null 语义 ──────────────────────────────────

    [Fact]
    public async Task 端口有默认模型时_端点用默认模型且沿用数据库来源标识()
    {
        var factory = Create(new FakeSource(Db(defaultModel: "default-model", models: ["model-a"])));

        var endpoint = await factory.ResolveAsync(null, default);

        Assert.NotNull(endpoint);
        Assert.Equal("default-model", endpoint.Model);
        Assert.Equal("https://db.example/v1", endpoint.BaseUrl);
        Assert.Equal("db-key", endpoint.ApiKey);
        Assert.Equal(7L, endpoint.ProviderId);
    }

    // 负控：删掉 ChatClientFactory 里的 active.Models.FirstOrDefault() → 本条红。
    [Fact]
    public async Task 没有默认模型时_取模型列表首个_不跳到配置()
    {
        var factory = Create(new FakeSource(Db(defaultModel: null, models: ["first-model", "second-model"])));

        var endpoint = await factory.ResolveAsync(null, default);

        Assert.NotNull(endpoint);
        Assert.Equal("first-model", endpoint.Model);
        Assert.Equal("https://db.example/v1", endpoint.BaseUrl);
    }

    [Fact]
    public async Task 模型列表也为空时_模型名取配置兜底_地址仍来自数据库()
    {
        var factory = Create(new FakeSource(Db(defaultModel: null, models: [])));

        var endpoint = await factory.ResolveAsync(null, default);

        Assert.NotNull(endpoint);
        Assert.Equal("fallback-model", endpoint.Model);
        Assert.Equal("https://db.example/v1", endpoint.BaseUrl);
    }

    [Fact]
    public async Task 调用方指定模型时_覆盖端口与配置()
    {
        var factory = Create(new FakeSource(Db(defaultModel: "default-model", models: ["model-a"])));

        var endpoint = await factory.ResolveAsync("override-model", default);

        Assert.NotNull(endpoint);
        Assert.Equal("override-model", endpoint.Model);
    }

    [Fact]
    public async Task 无激活供应商但配置就绪时_走配置兜底()
    {
        var factory = Create(new FakeSource(null));

        var endpoint = await factory.ResolveAsync(null, default);

        Assert.NotNull(endpoint);
        Assert.Null(endpoint.ProviderId);
        Assert.Equal("fallback-model", endpoint.Model);
        Assert.Equal("https://fallback.example/v1", endpoint.BaseUrl);
    }

    // 最要紧的一条：Harness 的 model_unavailable 归类与探活都依赖这个 null。
    // 负控：把 ResolveAsync 末尾的 return null 改成抛异常或返回空对象 → 本条红。
    [Fact]
    public async Task 无激活供应商且配置未就绪时_返回null而不是抛异常()
    {
        var factory = Create(new FakeSource(null), NotConfigured());

        var endpoint = await factory.ResolveAsync(null, default);

        Assert.Null(endpoint);
    }

    [Fact]
    public async Task 每次解析都问端口一次_不缓存激活供应商()
    {
        var source = new FakeSource(Db(defaultModel: "default-model", models: ["model-a"]));
        var factory = Create(source);

        await factory.ResolveAsync(null, default);
        await factory.ResolveAsync(null, default);

        Assert.Equal(2, source.Calls);
    }

    // ── 实现：Persistence 侧把 14 方法仓储折叠成一个事实 ──────────────

    [Fact]
    public async Task 实现类把供应商与模型列表折叠成端口事实()
    {
        var repository = new Mock<IProviderRepository>();
        repository.Setup(x => x.GetActiveProviderAsync()).ReturnsAsync(new ProviderRecord
        {
            Id = 7, Name = "db", BaseUrl = "https://db.example/v1", ApiKey = "db-key", IsActive = 1
        });
        repository.Setup(x => x.GetDefaultModelAsync(7)).ReturnsAsync((ProviderModelRecord?)null);
        repository.Setup(x => x.ListModelsAsync(7)).ReturnsAsync(new List<ProviderModelRecord>
        {
            new() { Id = 1, ProviderId = 7, Model = "m1" },
            new() { Id = 2, ProviderId = 7, Model = "m2" }
        });

        var source = new EfModelEndpointSource(repository.Object);
        var active = await source.GetActiveAsync(default);

        Assert.NotNull(active);
        Assert.Equal(7L, active.ProviderId);
        Assert.Equal("https://db.example/v1", active.BaseUrl);
        Assert.Equal("db-key", active.ApiKey);
        Assert.Null(active.DefaultModel);
        Assert.Equal(2, active.Models.Count);
        Assert.Equal("m1", active.Models[0]);
    }

    [Fact]
    public async Task 实现类在无激活供应商时返回null_不造空对象()
    {
        var repository = new Mock<IProviderRepository>();
        repository.Setup(x => x.GetActiveProviderAsync()).ReturnsAsync((ProviderRecord?)null);

        var active = await new EfModelEndpointSource(repository.Object).GetActiveAsync(default);

        Assert.Null(active);
    }

    // ── 装配 ──────────────────────────────────────────────────────────

    private static ChatClientFactory Create(IModelEndpointSource source, ProviderOptions? fallback = null)
        => new(source, Options.Create(fallback ?? Configured()), NullLogger<ChatClientFactory>.Instance);

    private static ProviderOptions Configured() => new()
    {
        BaseUrl = "https://fallback.example/v1",
        ApiKey = "fallback-key",
        Model = "fallback-model"
    };

    private static ProviderOptions NotConfigured() => new()
    {
        BaseUrl = "https://fallback.example/v1",
        ApiKey = "",  // IsConfigured == false
        Model = "fallback-model"
    };

    private static ActiveModelProvider Db(string? defaultModel, string[] models)
        => new(7L, "https://db.example/v1", "db-key", defaultModel, models);

    private sealed class FakeSource(ActiveModelProvider? active) : IModelEndpointSource
    {
        public int Calls { get; private set; }

        public Task<ActiveModelProvider?> GetActiveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(active);
        }
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>从测试输出目录往上找带 <c>AgentWorkbench.slnx</c> 的目录。</summary>
    private static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException("未找到 AgentWorkbench.slnx，无法定位仓库根。");
    }
}
