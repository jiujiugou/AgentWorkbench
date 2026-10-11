using System.Collections.Concurrent;
using System.Text;
using AgentWorkbench.Conversation;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace AgentWorkbench.Webapi.Hubs;

/// <summary>客户端发起的对话请求。</summary>
public sealed record ChatRequest(string? ConversationId, string Text, List<ChatTurn>? History);

/// <summary>
/// Agent 实时通道：接收用户消息，流式推送 token，并汇报工具调用事件。
/// 消息历史持久化到数据库，刷新/切换后可恢复。
/// </summary>
public sealed class AgentHub : Hub
{
    private readonly IChatService _chat;
    private readonly IRagService _rag;
    private readonly IConversationRepository _conversations;
    private readonly ILogger<AgentHub> _logger;

    /// <summary>每个连接当前的生成任务，用于"停止生成"与断线取消。</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Generations = new();

    public AgentHub(IChatService chat, IRagService rag, IConversationRepository conversations, ILogger<AgentHub> logger)
    {
        _chat = chat;
        _rag = rag;
        _conversations = conversations;
        _logger = logger;
    }

    /// <summary>发送一条消息并流式接收回复。</summary>
    public async Task Send(ChatRequest request)
    {
        var messageId = Guid.NewGuid().ToString("N");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        Generations[Context.ConnectionId] = linked;

        var answer = new StringBuilder();
        ConversationRecord? conversation = null;

        try
        {
            if (!await _chat.IsConfiguredAsync(Context.ConnectionAborted))
            {
                await SendErrorAsync("模型未配置：请在「设置」页添加供应商与模型。");
                return;
            }

            conversation = await _conversations.GetOrCreateAsync(request.ConversationId);
            await Clients.Caller.SendAsync("ReceiveConversation", new { conversationId = conversation.Id }, Context.ConnectionAborted);

            var history = await LoadHistoryAsync(conversation.Id);
            await _conversations.AddMessageAsync(conversation.Id, "user", request.Text);

            await foreach (var delta in _chat.StreamAsync(history, request.Text, linked.Token))
            {
                answer.Append(delta);
                await Clients.Caller.SendAsync("ReceiveDelta", new { messageId, delta }, Context.ConnectionAborted);
            }

            await SaveAnswerAsync(conversation.Id, answer);
            await Clients.Caller.SendAsync("ReceiveDone", new { messageId, conversationId = conversation.Id }, Context.ConnectionAborted);
        }
        catch (OperationCanceledException)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            await SendErrorAsync("已停止生成。");
        }
        catch (Exception ex)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            _logger.LogError(ex, "生成失败");
            await SendErrorAsync(ex.Message);
        }
        finally
        {
            Generations.TryRemove(Context.ConnectionId, out _);
        }
    }

    /// <summary>带知识库引用的问答：先推送命中来源，再流式生成答案。</summary>
    public async Task Ask(ChatRequest request)
    {
        var messageId = Guid.NewGuid().ToString("N");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        Generations[Context.ConnectionId] = linked;

        var answer = new StringBuilder();
        ConversationRecord? conversation = null;

        try
        {
            conversation = await _conversations.GetOrCreateAsync(request.ConversationId);
            await Clients.Caller.SendAsync("ReceiveConversation", new { conversationId = conversation.Id }, Context.ConnectionAborted);

            var history = await LoadHistoryAsync(conversation.Id);
            await _conversations.AddMessageAsync(conversation.Id, "user", request.Text);

            var sources = await _rag.RetrieveAsync(request.Text, 0, linked.Token);
            await Clients.Caller.SendAsync(
                "ReceiveSources",
                new { messageId, sources = sources.Select(ToSource) },
                linked.Token);

            await foreach (var delta in _rag.StreamAnswerAsync(request.Text, history, linked.Token))
            {
                answer.Append(delta);
                await Clients.Caller.SendAsync("ReceiveDelta", new { messageId, delta }, linked.Token);
            }

            await SaveAnswerAsync(conversation.Id, answer);
            await Clients.Caller.SendAsync("ReceiveDone", new { messageId, conversationId = conversation.Id }, linked.Token);
        }
        catch (OperationCanceledException)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            await SendErrorAsync("已停止生成。");
        }
        catch (Exception ex)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            _logger.LogError(ex, "知识问答失败");
            await SendErrorAsync(ex.Message);
        }
        finally
        {
            Generations.TryRemove(Context.ConnectionId, out _);
        }
    }

    private async Task<IReadOnlyList<ChatTurn>> LoadHistoryAsync(string conversationId)
    {
        var messages = await _conversations.GetMessagesAsync(conversationId);
        return messages
            .Select(m => new ChatTurn(m.Role, m.Content))
            .ToList();
    }

    private async Task SaveAnswerAsync(string? conversationId, StringBuilder answer)
    {
        if (string.IsNullOrEmpty(conversationId) || answer.Length == 0)
            return;
        try
        {
            await _conversations.AddMessageAsync(conversationId, "assistant", answer.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存助手消息失败：{ConversationId}", conversationId);
        }
    }

    private Task SendErrorAsync(string message)
        => Clients.Caller.SendAsync("Error", new { message });

    private static object ToSource(RetrievalHit hit) => new
    {
        chunkId = hit.ChunkId,
        documentId = hit.DocumentId,
        fileName = hit.FileName,
        seq = hit.Seq,
        heading = hit.Heading,
        pageFrom = hit.PageFrom,
        pageTo = hit.PageTo,
        vectorScore = hit.VectorScore,
        textScore = hit.TextScore,
        fusedScore = hit.FusedScore,
        content = hit.Content
    };

    /// <summary>停止当前生成。</summary>
    public Task Stop()
    {
        if (Generations.TryGetValue(Context.ConnectionId, out var cts))
            cts.Cancel();
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Generations.TryRemove(Context.ConnectionId, out var cts))
            cts.Cancel();
        return base.OnDisconnectedAsync(exception);
    }
}
