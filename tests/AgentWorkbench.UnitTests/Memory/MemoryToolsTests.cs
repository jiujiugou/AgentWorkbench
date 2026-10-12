using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.Memory.Persistence.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 记忆工具（<c>remember</c> / <c>recall</c>）：作用域取自 ambient（编排层设置），模型只能选「本作用域 / 全局」。
/// </summary>
public sealed class MemoryToolsTests
{
    private static readonly MemoryScope SessionA = MemoryScope.Session("session-a");
    private static readonly MemoryScope SessionB = MemoryScope.Session("session-b");

    [Fact]
    public async Task Remember_ThenRecall_InSameSession_FindsIt()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db);
        var remember = Remember(env);
        var recall = Recall(env);

        using (env.Scope.Begin(SessionA))
        {
            // 只传必填的 content：其余参数（key/kind/importance/global）必须可省略。
            var remembered = await remember.InvokeAsync(Args(("content", "用户偏好中文回答")));
            Assert.Contains("已记住", remembered?.ToString());

            var found = await recall.InvokeAsync(Args(("query", "中文")));
            Assert.Contains("用户偏好中文回答", found?.ToString());
        }
    }

    [Fact]
    public async Task Recall_InAnotherSession_IsEmpty()
    {
        // 负控：会话 A 记的东西，会话 B 必须看不到。
        using var db = new TempDatabase();
        var env = NewEnv(db);

        using (env.Scope.Begin(SessionA))
            await Remember(env).InvokeAsync(Args(("content", "只属于 A 的秘密")));

        using (env.Scope.Begin(SessionB))
        {
            var found = await Recall(env).InvokeAsync(Args(("query", "秘密")));
            Assert.Equal("无命中", found?.ToString());
        }
    }

    [Fact]
    public async Task Remember_Global_IsVisibleFromAnotherSession()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db);

        using (env.Scope.Begin(SessionA))
            await Remember(env).InvokeAsync(Args(("content", "全局：项目叫 AgentWorkbench"), ("global", true)));

        using (env.Scope.Begin(SessionB))
        {
            var found = await Recall(env).InvokeAsync(Args(("query", "AgentWorkbench")));
            Assert.Contains("AgentWorkbench", found?.ToString());
        }
    }

    [Fact]
    public async Task Remember_WithSameKey_Overwrites()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db);

        using (env.Scope.Begin(SessionA))
        {
            var remember = Remember(env);
            await remember.InvokeAsync(Args(("content", "先说中文"), ("key", "lang")));
            await remember.InvokeAsync(Args(("content", "改说英文"), ("key", "lang")));

            var found = await Recall(env).InvokeAsync(Args());
            var text = found?.ToString() ?? string.Empty;
            Assert.Contains("改说英文", text);
            Assert.DoesNotContain("先说中文", text);
        }
    }

    [Fact]
    public async Task Remember_WithoutAmbientScope_RefusesAndWritesNothing()
    {
        // 负控：不在任何会话内且配置默认按会话隔离时，宁可不写，也不能落到「谁都能看到」的地方。
        using var db = new TempDatabase();
        var env = NewEnv(db);

        var result = await Remember(env).InvokeAsync(Args(("content", "无处安放")));

        Assert.Contains("无法确定记忆作用域", result?.ToString());
        Assert.Empty(await env.Store.RecallAsync(new MemoryQuery(SessionA, IncludeGlobal: true)));
    }

    [Fact]
    public async Task Remember_WhenLongTermDisabled_RefusesAndWritesNothing()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db, new MemoryOptions { LongTermEnabled = false });

        using (env.Scope.Begin(SessionA))
        {
            var result = await Remember(env).InvokeAsync(Args(("content", "不该被记住")));
            Assert.Contains("长期记忆未启用", result?.ToString());
        }

        Assert.Empty(await env.Store.RecallAsync(new MemoryQuery(SessionA)));
    }

    [Fact]
    public async Task Remember_EmptyContent_ReportsFailureWithoutThrowing()
    {
        // 内容为空是调用方能修的问题：回一句话让模型改正，而不是把整轮 run 打挂。
        using var db = new TempDatabase();
        var env = NewEnv(db);

        using (env.Scope.Begin(SessionA))
        {
            var result = await Remember(env).InvokeAsync(Args(("content", "   ")));
            Assert.StartsWith("未记住", result?.ToString());
        }
    }

    [Fact]
    public async Task Recall_RespectsLimitAndKind()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db);

        using (env.Scope.Begin(SessionA))
        {
            var remember = Remember(env);
            await remember.InvokeAsync(Args(("content", "偏好一"), ("kind", "preference"), ("importance", 0.9)));
            await remember.InvokeAsync(Args(("content", "偏好二"), ("kind", "preference"), ("importance", 0.5)));
            await remember.InvokeAsync(Args(("content", "事实一"), ("kind", "fact"), ("importance", 0.7)));

            var recall = Recall(env);

            var limited = await recall.InvokeAsync(Args(("limit", 1)));
            var limitedText = limited?.ToString() ?? string.Empty;
            Assert.Contains("偏好一", limitedText);
            Assert.DoesNotContain("偏好二", limitedText);

            var facts = await recall.InvokeAsync(Args(("kind", "fact")));
            var factsText = facts?.ToString() ?? string.Empty;
            Assert.Contains("事实一", factsText);
            Assert.DoesNotContain("偏好一", factsText);
        }
    }

    [Fact]
    public async Task Recall_WhenLongTermDisabled_Refuses()
    {
        using var db = new TempDatabase();
        var env = NewEnv(db, new MemoryOptions { LongTermEnabled = false });

        using (env.Scope.Begin(SessionA))
        {
            var result = await Recall(env).InvokeAsync(Args(("query", "任意")));
            Assert.Contains("长期记忆未启用", result?.ToString());
        }
    }

    private static AIFunction Remember(Env env)
        => (AIFunction)new RememberTool(env.Store, env.Scope, Options.Create(env.Options)).GetTools().Single();

    private static AIFunction Recall(Env env)
        => (AIFunction)new RecallTool(env.Store, env.Scope, Options.Create(env.Options)).GetTools().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in values)
            args.Add(name, value);
        return args;
    }

    private static Env NewEnv(TempDatabase db, MemoryOptions? options = null)
        => new(
            new SqliteLongTermMemoryStore(
                new SqliteMemoryItemRepository(db.ConnectionFactory),
                Options.Create(options ?? new MemoryOptions()),
                TimeProvider.System),
            new AsyncLocalMemoryScopeAccessor(),
            options ?? new MemoryOptions());

    private sealed record Env(SqliteLongTermMemoryStore Store, AsyncLocalMemoryScopeAccessor Scope, MemoryOptions Options);
}
