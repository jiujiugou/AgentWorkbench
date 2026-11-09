# 状态与记忆的落库实现（Memory.Persistence）

这个模块是 `Memory` 的**落库后端**：把契约模块里的 `ISessionStore` / `ILongTermMemoryStore` 换成 SQLite 版，
让会话在进程重启后仍能续上、长期记忆有地方存。它不定义契约、不做历史策略，也不碰 prompt 组装。

### 这一格是什么（模型上下文的落库实现，不是新功能）

2026-11-09 起 `SqliteChatHistoryProvider` 把每条消息逐条追加进 `chat_messages`，
取代「整个会话序列化后覆盖 `agent_sessions` 一行」那条路。它是 MAF `ChatHistoryProvider`
扩展点的实现：每次模型调用前 `ProvideChatHistoryAsync` 读回、结束后 `StoreChatHistoryAsync` 追加。
**在热路径上，不受任何开关控制，也不能「需要了再引用」。**

- 本模块负责：只追加、按自增 `id` 读回、单条消息序列化不丢 `functionCall`；不覆盖、不裁剪、不删除。
- 不负责：契约（应在 `Runtime.Session`）、注入形态与预算（`Runtime.Context`）、界面显示、
  `system` 指令（注入发生在 provider 下游，不落库——见 `docs/core/runtime.md` §7.3 第 9 条）。
- 改动的动机、三段归属与连锁问题见 `docs/core/runtime.md` §7；**会话快照的并发写入归 §7.6 管**，
  加新 provider 前先看那节的「L1 同时保护 `agent_sessions`」，不要当成独立缺陷。

---

## 一、它是什么

- `SqliteSessionStore`：`GetOrCreateAsync` 命中且未过期就 `DeserializeAsync`，否则新建；
  `SaveAsync` 用 MAF 序列化会话后 UPSERT。**快照里已经没有历史了**，只剩 StateBag 的非历史键。
  整份 Upsert 无 version，但**单进程内不构成缺陷**：`SaveAsync` 唯一调用点在 Harness 的
  `finally`，被 `ISessionRunGate` 的 `await using` 包住，同一 `sessionId` 不会有两个写入者。
  剩余风险（L2 未实现的跨进程、未来绕过 Harness 的后台写入者）归 `docs/core/runtime.md` §7.6；
  真要修到多实例，是在**这里**给 `SaveAsync` 加 `version` + `UPDATE ... WHERE version=?`（CAS）。
- `SqliteChatHistoryProvider`：`ChatHistoryProvider` 的落库实现。每次模型调用前按 `sessionId`
  从 `chat_messages` 读回历史，run 结束把本轮新消息追加进去。`sessionId` 从
  `session.StateBag[AgentSessionKeys.SessionId]` 取（MAF 的会话对象不带 id）。
- `SqliteChatMessageStore`：`chat_messages` 的读写（**EF Core**，`IDbContextFactory<AppDbContext>`，
  与 `ConversationRepository` 同一套；建表仍归 FluentMigrator）。只追加、按自增 `id` 升序读回，
  单条消息用 `AIJsonUtilities.DefaultOptions` 序列化（保住 `functionCall` / `functionResult`）。
  实体映射在 `Persistence/ChatMessageRecord.cs` + `AppDbContext.ChatMessages`。
- `SqliteLongTermMemoryStore`：长期记忆的作用域隔离 / key 覆盖 / 保留期语义。
- `SqliteSessionStateRepository` / `SqliteMemoryItemRepository`：两张表的读写（Dapper）。
- `Tools/RememberTool`、`Tools/RecallTool`：agent 工具入口（`IAgentToolProvider`），作用域取自
  `IMemoryScopeAccessor`，模型只能选「本作用域 / 全局」。
- `MemoryRetentionService`：`BackgroundService`，定期删过期会话行与过保留期的记忆行。
- `AddNitroMemoryPersistence(...)`：注册上述实现，并用 `services.Replace` 覆盖 `AddNitroMemory`
  的内存版 `ISessionStore`，以及 `AddNitroContext` 注册的内存版 `ChatHistoryProvider`。

---

## 二、表

迁移 `M023_CreateAgentSessions`（会话，FluentMigrator，位于 `Persistence/Migrations`）：

```sql
agent_sessions (
  session_id TEXT PRIMARY KEY,   -- 64，对齐 agent_runs.session_id
  state_json TEXT NOT NULL,      -- MAF 序列化结果；2026-11-09 起不再含聊天历史，只剩非历史状态
  created_at TEXT NOT NULL,      -- ISO-8601
  updated_at TEXT NOT NULL,
  expires_at TEXT NULL           -- NULL = 不过期
)
```

迁移 `M031_ChatHistory`（对话历史，DDL 与 `Persistence/ChatHistorySchema.cs` 共用一份）：

```sql
chat_messages (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,  -- 全局递增；会话内即写入顺序
  session_id TEXT NOT NULL,
  role       TEXT NOT NULL,                      -- 便于查询，不用反序列化 payload
  payload    TEXT NOT NULL,                      -- 单条 ChatMessage 的 JSON（含 functionCall/Result）
  created_at TEXT NOT NULL
)
-- ix_chat_messages_session(session_id, id)
```

> 刻意**不建** `(session_id, message_id)` 唯一约束：消息没有稳定的业务主键，
> 按内容派生 id 会把「用户连说两遍同一句话」误当重复合并——那是两次合法输入。
> 幂等属于动作层（`request_id`），不在这里伪装。

迁移 `M024_CreateMemoryItems`（长期记忆）：

```sql
memory_items (
  id TEXT PRIMARY KEY,           -- guid N
  scope_type TEXT NOT NULL,      -- global | agent | user | session
  scope_id   TEXT NOT NULL,      -- 作用域实例；global 恒为空串
  kind TEXT NOT NULL,            -- fact | preference | decision | note（不强制白名单）
  mem_key TEXT NULL,             -- 覆盖键；NULL = 每次都新增
  content TEXT NOT NULL,
  importance REAL NOT NULL,      -- 0..1，召回排序用
  source_session_id TEXT NULL,
  source_run_id INTEGER NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  last_used_at TEXT NULL,
  use_count INTEGER NOT NULL,
  expires_at TEXT NULL           -- 保留期
)
-- ux_memory_items_key(scope_type, scope_id, mem_key) UNIQUE
```

> SQLite 唯一索引把多个 `NULL` 视为互不相同，所以「没有 mem_key 的记忆」不会被去重键误伤；
> 反过来，这个唯一索引是同 key 只能有一条的**最终保证**（应用层预查只挡正常路径）。

---

## 三、怎么接上

```csharp
builder.Services.AddNitroMemory(builder.Configuration);            // 契约 + 内存版会话
builder.Services.AddNitroMemoryPersistence(builder.Configuration); // 换成 SQLite + 长期记忆 + 工具 + 清理
```

未调用 `AddNitroMemoryPersistence` 时：会话仍是内存版（重启即丢），`remember` / `recall` 工具不注册
（工具提供方在本模块），依赖方向不允许别处补注册。

`LongTermEnabled=false`（`Harness:Memory:LongTermEnabled`）时同样不注册这两个工具——模型看不到 = 调不动。

---

## 四、红线

1. **只实现契约，不改契约**。契约（`ISessionStore` / `ISessionFactory` / `ILongTermMemoryStore` /
   `IMemoryScopeAccessor`）都在 `Memory`。
2. **不反向依赖**。本模块依赖 `Memory` + `Persistence` + `ToolExecution`；`Memory` 绝不依赖本模块或 `Persistence`。
   工具放这里（而不是 ToolExecution）正是为了让工具平台不反向依赖记忆。
3. **序列化 `JsonSerializerOptions` 必须传 `null`**（见 `AgentSessionFactory` 注释），否则 MAF 抛异常。
4. **召回不回 prompt 文本**：`SqliteLongTermMemoryStore` 只回 `MemoryItem`；格式化注入归 `Context`。
5. **工具的作用域来自 ambient，不来自模型参数**。
6. **历史 provider 不能持有会话相关状态**。它注册为单例、服务所有会话，`sessionId` 只能从
   `StateBag[AgentSessionKeys.SessionId]` 读。把当前会话 id 存进实例字段必然串号——官方明确要求
   session 状态放 StateBag。
7. **不要开 `RequirePerServiceCallChatHistoryPersistence`**。实测（`HistoryProviderProbeTests.H1`）
   一次 run 结束时工具调用与工具结果都已在历史里；开那个开关只会让 `FunctionInvokingChatClient`
   把对话当成服务端管理、清掉自己累积的历史，改变守卫与轨迹行为，收益为零。

## 依赖

Memory、Persistence、ToolExecution、Shared。
