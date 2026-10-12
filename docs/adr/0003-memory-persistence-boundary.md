# ADR-0003：会话与长期记忆的持久化边界与模块拆分

- 状态：已采纳
- 日期：2026-10-12
- 决策者：架构

## 背景

`AgentWorkbench.Memory` 原本是「契约 + 内存实现」合一的模块，被约束为**只依赖 `Shared`**。这带来四个问题：

1. **会话重启即丢**：会话只活在进程内 `ConcurrentDictionary` 里，没有序列化出口，也没有落库载体。
2. **字典无限增长**：没有 TTL、也没有条数上限。
3. **长期记忆没有载体**：没有表、没有写入/召回/遗忘口径。
4. **依赖方向挡住持久化**：任何 SQLite / Dapper 都无处安放。

已用 spike 验证：`Microsoft.Agents.AI` 的 `AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync`
能完整往返 `AgentSession`（含 `InMemoryChatHistoryProvider` 的历史）。**必须传 `null` 作为
`JsonSerializerOptions`**，传自定义实例会抛 `NotSupportedException`。

## 决策

1. **新增 `AgentWorkbench.Memory.Persistence` 模块**（与 `Knowledge.Local` 同模式）承载落库实现；
   `Memory` 保持只依赖 `Shared`，只留契约 + 内存实现。组合根先 `AddNitroMemory` 再
   `AddNitroMemoryPersistence`，后者用 `services.Replace` 覆盖注册。
2. **会话以 MAF 官方序列化 JSON 落 `agent_sessions` 表**（M023）；`ISessionStore` 升级为
   `GetOrCreateAsync` / `SaveAsync`，序列化细节抽到 `ISessionFactory`（由 Harness 用当次 agent 实现）。
3. **会话与长期记忆都有界**：`MemoryOptions` 增 `SessionTtlMinutes` / `MaxCachedSessions` /
   `EvictionSweepSeconds`；内存版 TTL + LRU（注入 `TimeProvider` 保证可测），落库版读路径判过期
   并由 `MemoryRetentionService` 后台清理。
4. **长期记忆落 `memory_items` 表**（M024），契约 `ILongTermMemoryStore` 留在 `Memory`；
   隔离维度是 `(scope_type, scope_id)`，覆盖维度是 `mem_key`。
5. **写入/召回入口（MVP）是 agent 工具** `remember` / `recall`，注册在 `Memory.Persistence`；
   工具的作用域来自 `IMemoryScopeAccessor`（ambient），**不接受模型指定 scope_id**。
6. **职责三面切分（防与 `Context` 重叠）**：`Memory` = 数据面（IO，只出结构化数据）；
   `Context` = 组装面（纯逻辑，注入条数与预算、历史裁剪）；`AgentHarness` = 编排面（串联 + 设置作用域）。
   `Context` **不新增**对 `Memory` / `Persistence` 的依赖，由架构测试强制。

## 影响

- `Memory.Persistence → Memory, Persistence, ToolExecution`；`Memory → Shared` 不变；
  `Context → Knowledge.Abstractions, Shared` 不变；`AgentHarness → Memory, Context` 不变。
- 架构测试新增：`Memory` 不得依赖 `Persistence`；`Context` 不得依赖 `Memory` / `Persistence`
  （负控：加真实依赖即变红）。
- 迁移 M023 / M024 只增不改，旧库可平滑升级；升级后内存里未落库的历史会话会丢一次（可接受）。
- 已知限制：SQLite 单文件不是分布式存储，多副本部署仍需外置后端（另开模块，不改本契约）。

## 备选方案

1. **只持久化历史消息（复用 `messages` 表）**：丢失 MAF 会话中历史以外的状态，且把「历史怎么进会话」
   塞进 `Memory`（违反其红线）。序列化整会话更完整、更少猜测。
2. **让 `Memory` 直接引用 `Persistence`**：最省事，但把契约模块污染成基础设施模块，破坏依赖方向。
3. **用 MAF 自带 `AgentSessionStore` 接管**：控制反转点在框架内部、装配不透明，且与现有
   `ISessionStore` 契约重叠；保留自有契约、后续可加适配器。
4. **只做 TTL 不做落库**：治不了「重启即丢」这个核心缺口。
5. **记忆工具只写 `global`**：不需要 ambient 作用域，但会话级记忆与全局记忆无法共存、隐私边界更粗；
   最终选择「默认按会话隔离 + 可显式 global」，把 scope_id 的决定权留在编排层。
