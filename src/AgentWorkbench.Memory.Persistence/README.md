# 状态与记忆的落库实现（Memory.Persistence）

这个模块是 `Memory` 的**落库后端**：把契约模块里的 `ISessionStore` 换成 SQLite 版，
让会话在进程重启后仍能续上。它不定义契约、不做历史策略，也不碰 prompt 组装。

---

## 一、它是什么

- `SqliteSessionStore`：`GetOrCreateAsync` 命中且未过期就 `DeserializeAsync`，否则新建；
  `SaveAsync` 用 MAF 序列化会话后 UPSERT。
- `SqliteSessionStateRepository`：`agent_sessions` 表的读写（Dapper）。
- `SessionRetentionService`：`BackgroundService`，定期删过期行（`Memory:SessionTtlMinutes` 关掉时不启动清理）。
- `AddNitroMemoryPersistence(...)`：注册上述实现，并用 `services.Replace` 覆盖 `AddNitroMemory` 注册的内存版。

---

## 二、表

迁移 `M023_CreateAgentSessions`（FluentMigrator，位于 `Persistence/Migrations`）：

```sql
agent_sessions (
  session_id TEXT PRIMARY KEY,   -- 64，对齐 agent_runs.session_id
  state_json TEXT NOT NULL,      -- MAF 序列化结果，含聊天历史
  created_at TEXT NOT NULL,      -- ISO-8601
  updated_at TEXT NOT NULL,
  expires_at TEXT NULL           -- NULL = 不过期
)
```

---

## 三、怎么接上

```csharp
builder.Services.AddNitroMemory(builder.Configuration);            // 契约 + 内存默认实现
builder.Services.AddNitroMemoryPersistence(builder.Configuration); // 换成 SQLite + 过期清理
```

未调用 `AddNitroMemoryPersistence` 时仍是内存版（重启即丢），单测与精简环境不需要数据库。

---

## 四、红线

1. **只实现契约，不改契约**。契约与 `AgentSessionFactory` 都在 `Memory`。
2. **不反向依赖**。本模块依赖 `Memory` + `Persistence`；`Memory` 绝不依赖本模块或 `Persistence`。
3. **序列化 `JsonSerializerOptions` 必须传 `null`**（见 `AgentSessionFactory` 注释），否则 MAF 抛异常。
4. **写库失败不能改变本模块之外的结局**：`AgentHarness` 已用 best-effort 写回。

## 依赖

Memory、Persistence、Shared。
