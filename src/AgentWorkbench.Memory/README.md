# 状态与记忆模块（Memory）

这个模块干两件事：**让同一个 `sessionId` 在一段时间内复用同一个会话对象并把它存得住**，以及
**给长期记忆一个载体（存 / 查 / 忘）**。

它不做历史策略、不决定「材料怎么进上下文」、也不做检索排序之外的组装。会话里装了什么由装配它的编排层
（`AgentHarness`）决定；历史怎么裁剪/压缩、注入几条、占多少预算，都是 `Context` 的事。

---

## 一、现在已经具备的能力

### 会话怎么被复用

- 契约是 `ISessionStore.GetOrCreateAsync(sessionId, factory)` + `ISessionStore.SaveAsync(...)`。
  命中就返回已有会话（落库版会反序列化），没命中就调用 `factory` 建一个新的，再按 `sessionId` 存下来。
- `ISessionFactory` 把「建 / 序列化 / 反序列化」抽象出来，由 `AgentHarness` 用当次 run 的 agent 实现
  （`AgentSessionFactory`）。存储实现因此不需要认识具体的模型客户端。
- 两个实现：
  - `SessionStore`（本模块，进程内 `ConcurrentDictionary`）：重启即丢，适合单测 / 精简环境。
  - `SqliteSessionStore`（`Memory.Persistence`）：会话序列化后落 `agent_sessions` 表，重启后同 `sessionId` 能续上。
- 消费者只有一个：`AgentHarness.RunAsync`（run 结束会 best-effort `SaveAsync`，成功/失败/取消/超时都写）。

### 会话里装的是什么

- 会话是 MAF 的 `AgentSession`，历史由 `InMemoryChatHistoryProvider` 持有。
- 序列化用 MAF 官方的 `AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync`，**历史会一起带上**
  （序列化结果里含 `InMemoryChatHistoryProvider.messages`）。
- ⚠️ 调用时 `JsonSerializerOptions` 必须传 `null`：传自定义实例不带 MAF 的 source-generated 元数据，
  Serialize / Deserialize 会抛 `NotSupportedException`。见 `AgentSessionFactory` 注释。

### 长期记忆怎么落地

- 契约是 `ILongTermMemoryStore`：`RememberAsync` / `RecallAsync` / `ForgetAsync` / `PurgeExpiredAsync`。
  **只进出结构化数据**（`MemoryItem`），绝不返回拼好的 prompt 文本，也不决定注入策略。
- **隔离维度是作用域**（`MemoryScope` = `type` + `id`）：`session` / `user` / `agent` / `global`。
  召回只发生在本作用域内，外加可选的 `global` 兜底，不会串到别人的记忆里。
- **覆盖维度是 key**：同 `(scope, key)` 的写入是覆盖（保留原 id 与使用统计），没有 key 则每次新增。
- **保留期**：`MemoryWrite.Retention` 或 `MemoryOptions.LongTermRetentionDays` 决定 `expires_at`；
  过期的行召回时看不见，并由后台服务删除。
- 写入/召回的入口（MVP）是 agent 工具：`remember` / `recall`（在 `Memory.Persistence` 注册）。

### 记忆工具的作用域从哪来

工具在被模型调用时读 `IMemoryScopeAccessor.Current`（`AsyncLocal` 实现），由 `AgentHarness` 在每次 run
开始时设为 `MemoryScope.Session(sessionId)`。

**为什么不让模型用参数指定 scope_id**：那等于把隐私边界交给模型。模型只能选「本作用域 / 全局」这种粗粒度
开关（`global=true`），具体 id 由编排层决定。

### 会话不会无限增长

- `MemoryOptions`（配置节 `Harness:Memory`）：

  | 键 | 默认 | 含义 |
  | --- | --- | --- |
  | `SessionEnabled` | `true` | 是否维持会话内记忆 |
  | `SessionTtlMinutes` | `1440` | 会话空闲过期时间（分钟）；`0` = 不过期 |
  | `MaxCachedSessions` | `1000` | 进程内缓存条数上限；`0` = 不限 |
  | `EvictionSweepSeconds` | `60` | 落库版过期清理周期（秒） |
  | `LongTermEnabled` | `true` | 是否注册 `remember` / `recall` 工具 |
  | `LongTermScope` | `session` | 工具写入的默认作用域（`session` 或 `global`） |
  | `MaxMemoryChars` | `2000` | 单条记忆内容上限，超出拒绝写入 |
  | `RecallCandidateK` | `20` | 召回候选上限默认值（取库上限，不是注入条数） |
  | `LongTermRetentionDays` | `0` | 长期记忆默认保留天数；`0` = 不过期 |

- 内存版会话：命中时判 TTL，超上限按 LRU 淘汰（`TimeProvider` 可注入，测试可确定性推进时钟）。
- 落库版：读时判 `expires_at`（过期即删并当新会话），并由 `MemoryRetentionService` 定期清理。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `ISessionStore` | `GetOrCreateAsync`、`SaveAsync` | `SessionStore` / `SqliteSessionStore` | `AgentHarness.RunAsync` |
| `ISessionFactory` | `CreateAsync`、`SerializeAsync`、`DeserializeAsync` | `AgentSessionFactory` | 会话存储 |
| `ILongTermMemoryStore` | `RememberAsync`、`RecallAsync`、`ForgetAsync`、`PurgeExpiredAsync` | `SqliteLongTermMemoryStore` | `remember` / `recall` 工具（阶段 3 还会给 Harness） |
| `IMemoryScopeAccessor` | `Current`、`Begin` | `AsyncLocalMemoryScopeAccessor` | 记忆工具 |
| `ISessionStateRepository` / `IMemoryItemRepository` | 表读写 | `Sqlite*Repository` | 落库版存储、保留服务 |

落库版由 `AgentWorkbench.Memory.Persistence` 提供，组合根里 `AddNitroMemoryPersistence` 用 `Replace`
覆盖 `AddNitroMemory` 注册的内存版 `ISessionStore`，并注册长期记忆载体与记忆工具。

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 长期记忆自动召回 | 记忆只在模型主动调 `recall` 时生效 | 只有显式工具（阶段 2） | Harness 取候选 → Context 注入（阶段 3） |
| 记忆的语义召回 | 关键词召回命中率低 | `LIKE` 关键词 | 需先定 FTS5 / 向量口径与评测指标 |
| 历史无限长 | 上下文窗口撑爆 | 落库存的是**完整**历史，不裁剪 | 裁剪/压缩归 `Context`，不在本模块做 |
| 并发同 `sessionId` 的 run | 两个 run 共用一个会话，消息可能串 | 内存版只护住「建会话」一段，run 之间无串行 | 明确「同会话串行」或按 run 隔离会话 |
| 多实例共享会话/记忆 | SQLite 单文件不是分布式存储 | 同机多进程可读，跨机不行 | 分布式后端另开模块，不改本契约 |

---

## 三、换一个存储要做什么

1. 会话：实现 `ISessionStore`（+ 需要的 `ISessionStateRepository`）。
2. 长期记忆：实现 `ILongTermMemoryStore`。
3. 在新模块或组合根里换掉注册。
4. 不要动 `AgentHarness` —— 它只认契约。

判断标准：**契约留在本模块，实现可以在本模块（进程内）或新模块（持久化）**，但依赖方向不能反向。

---

## 四、与 Context 的边界（防重叠）

- **数据面（本模块 / `Memory.Persistence`）**：会话存取；长期记忆的存/查/忘；只出**结构化数据**，不拼 prompt。
- **组装面（`Context`）**：把传入材料拼成最终 user 文本；注入条数/长度/预算；历史裁剪与压缩。纯逻辑、禁止 IO。
- **编排面（`AgentHarness`）**：Memory 取数据 → Context 组装 → 调模型；并负责设置记忆作用域。

四条硬规则：Memory 不拼 prompt；Memory 不管注入策略（`RecallAsync` 的 `CandidateK` 只是取库上限）；
Context 不查库、不引 `Memory`；历史只有一个策略点（Memory 存完整会话，Context 决定模型可见视图）。
架构测试强制 `Memory → Persistence` 与 `Context → Memory/Persistence` 均为非法。

> 例外说明：`remember` / `recall` 返回的是**工具结果**（给模型看的一句话），属于工具契约，不是系统注入的
> prompt。系统注入仍然只在 `Context`。

---

## 五、红线

1. **不把历史策略塞进本模块**。会话是容器，不是上下文策略；裁剪/压缩归 `Context`。
2. **不引用 `Persistence`**。依赖方向由架构测试强制（`Memory_should_not_depend_on_persistence`）；
   落库实现放 `Memory.Persistence`，契约留本模块。
3. **会话按 `sessionId` 隔离，记忆按 scope 隔离**。禁止跨 id 复用会话，禁止跨 scope 召回。
4. **作用域不让模型指定**。scope_id 只能来自编排层（`IMemoryScopeAccessor`）。
5. **没有能变红的负控，不新增能力**。

---

## 六、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

已覆盖（`tests/AgentWorkbench.UnitTests/Memory/`）：

- 会话正例：同 `sessionId` 连续取两次会话拿到同一对象；`SqliteSessionStore` + 真实 SQLite + 真实 MAF agent，
  「重启」（销毁 store/agent、复用同一库）后同 `sessionId` 的历史续上。
- 会话负控：换 `sessionId` 必须新建、不得读到别人会话；TTL 过期后历史必须丢，TTL 内必须续上；
  上限调小必须淘汰最久未用，上限关掉不淘汰；快照损坏（坏 JSON）必须丢掉重来、不得永久卡死。
- 长期记忆正例：同 scope 写入后可召回；同 `(scope,key)` 二次写入覆盖不新增；召回按重要性排序、
  `CandidateK` 生效；召回回写 `use_count`；过保留期不召回、未过期正常召回。
- 长期记忆负控：换 scope 查询必须为空；不带 key 必须是两条；查询里的 `%` 必须当字面量（不得返回全部）；
  `Forget` 不存在的 id 必须报 false；非 global 作用域缺 id 必须拒绝；内容为空/超长必须拒绝。
- 保留服务：越过保留期后一轮清理必须删掉过期会话行与过期记忆行、保留未过期的；时间没到的一轮清理必须一条都不删。
- 组合根：`AddNitroMemoryPersistence` 必须把两个存储换成落库版并注册记忆工具；`LongTermEnabled=false` 时必须不注册。
- 工具负控：会话 A 记的记忆，会话 B 必须看不到；不在任何会话内且默认按会话隔离时**必须拒绝写入**；
  `LongTermEnabled=false` 时必须拒绝且不落库。
- 架构：`Memory` 不得依赖 `Persistence`、`Context` 不得依赖 `Memory`/`Persistence`（负控：加真实依赖即变红）。

尚未覆盖：真·跨进程重启（验收脚本未落地）、同 `sessionId` 并发 run、记忆的自动召回（阶段 3）。

## 依赖

Shared。落库实现（`AgentWorkbench.Memory.Persistence`）：Memory、Persistence、ToolExecution、Shared。
