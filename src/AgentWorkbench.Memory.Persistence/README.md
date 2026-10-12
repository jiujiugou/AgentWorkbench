# 状态与记忆的落库实现（Memory.Persistence）

这个模块是 `Memory` 的**落库后端**：把契约模块里的 `ISessionStore` / `ILongTermMemoryStore` 换成 SQLite 版，
让会话在进程重启后仍能续上、长期记忆有地方存。它不定义契约、不做历史策略，也不碰 prompt 组装。

---

## 一、它是什么

- `SqliteSessionStore`：`GetOrCreateAsync` 命中且未过期就 `DeserializeAsync`，否则新建；
  `SaveAsync` 用 MAF 序列化会话后 UPSERT。
- `SqliteLongTermMemoryStore`：长期记忆的作用域隔离 / key 覆盖 / 保留期语义。
- `SqliteSessionStateRepository` / `SqliteMemoryItemRepository`：两张表的读写（Dapper）。
- `Tools/RememberTool`、`Tools/RecallTool`：agent 工具入口（`IAgentToolProvider`），作用域取自
  `IMemoryScopeAccessor`，模型只能选「本作用域 / 全局」。
- `MemoryRetentionService`：`BackgroundService`，定期删过期会话行与过保留期的记忆行。
- `AddNitroMemoryPersistence(...)`：注册上述实现，并用 `services.Replace` 覆盖 `AddNitroMemory` 的内存版注册。

---

## 二、表

迁移 `M023_CreateAgentSessions`（会话，FluentMigrator，位于 `Persistence/Migrations`）：

```sql
agent_sessions (
  session_id TEXT PRIMARY KEY,   -- 64，对齐 agent_runs.session_id
  state_json TEXT NOT NULL,      -- MAF 序列化结果，含聊天历史
  created_at TEXT NOT NULL,      -- ISO-8601
  updated_at TEXT NOT NULL,
  expires_at TEXT NULL           -- NULL = 不过期
)
```

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

## 依赖

Memory、Persistence、ToolExecution、Shared。
