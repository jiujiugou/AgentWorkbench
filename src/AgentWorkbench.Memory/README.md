# 状态与记忆模块（Memory）

这个模块只干一件事：**让同一个 `sessionId` 在一段时间内复用同一个会话对象，并把它存得住。**

它不做历史落库之外的策略、不决定「历史怎么进上下文」、也不做检索。会话里到底装了什么，由装配它的编排层
（`AgentHarness`）决定；历史怎么裁剪/压缩是 `Context` 的事。

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

### 会话不会无限增长

- `MemoryOptions`（配置节 `Harness:Memory`）：

  | 键 | 默认 | 含义 |
  | --- | --- | --- |
  | `SessionEnabled` | `true` | 是否维持会话内记忆 |
  | `SessionTtlMinutes` | `1440` | 空闲过期时间（分钟）；`0` = 不过期 |
  | `MaxCachedSessions` | `1000` | 进程内缓存条数上限；`0` = 不限 |
  | `EvictionSweepSeconds` | `60` | 落库版过期清理周期（秒） |

- 内存版：命中时判 TTL，超上限按 LRU 淘汰（`TimeProvider` 可注入，测试可确定性推进时钟）。
- 落库版：读时判 `expires_at`（过期即删并当新会话），并由 `SessionRetentionService` 定期清理失效行。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `ISessionStore` | `GetOrCreateAsync`、`SaveAsync` | `SessionStore` / `SqliteSessionStore` | `AgentHarness.RunAsync` |
| `ISessionFactory` | `CreateAsync`、`SerializeAsync`、`DeserializeAsync` | `AgentSessionFactory` | 会话存储 |
| `ISessionStateRepository` | 读写 `agent_sessions` | `SqliteSessionStateRepository` | `SqliteSessionStore`、`SessionRetentionService` |

落库版由 `AgentWorkbench.Memory.Persistence` 提供，组合根里 `AddNitroMemoryPersistence` 用 `Replace`
覆盖 `AddNitroMemory` 注册的内存版。

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 长期记忆没有载体 | 「记住用户偏好」这类能力无处安放 | 只有会话状态，无记忆表/策略 | 先定写入/召回/隐私口径，再落表（方案 §4.5） |
| 历史无限长 | 上下文窗口撑爆 | 落库存的是**完整**历史，不裁剪 | 裁剪/压缩归 `Context`，不在本模块做 |
| 并发同 `sessionId` 的 run | 两个 run 共用一个会话，消息可能串 | 内存版只护住「建会话」一段，run 之间无串行 | 明确「同会话串行」或按 run 隔离会话 |
| 多实例共享会话 | SQLite 单文件不是分布式存储 | 同机多进程可读，跨机不行 | 分布式后端另开模块，不改本契约 |

---

## 三、换一个会话存储要做什么

1. 实现 `ISessionStore`（+ 需要的 `ISessionStateRepository`）。
2. 在新模块或组合根里换掉注册。
3. 不要动 `AgentHarness` —— 它只认契约。

判断标准：**契约留在本模块，实现可以在本模块（进程内）或新模块（持久化）**，但依赖方向不能反向。

---

## 四、与 Context 的边界（防重叠）

- **数据面（本模块 / `Memory.Persistence`）**：会话存取；长期记忆的存/查/忘；只出**结构化数据**，不拼 prompt。
- **组装面（`Context`）**：把传入材料拼成最终 user 文本；注入条数/长度/预算；历史裁剪与压缩。纯逻辑、禁止 IO。
- **编排面（`AgentHarness`）**：Memory 取数据 → Context 组装 → 调模型。

四条硬规则：Memory 不拼 prompt；Memory 不管注入策略；Context 不查库、不引 `Memory`；历史只有一个策略点
（Memory 存完整会话，Context 决定模型可见视图）。架构测试强制 `Memory → Persistence` 与 `Context → Memory/Persistence` 均为非法。

---

## 五、红线

1. **不把历史策略塞进本模块**。会话是容器，不是上下文策略；裁剪/压缩归 `Context`。
2. **不引用 `Persistence`**。依赖方向由架构测试强制（`Memory_should_not_depend_on_persistence`）；
   落库实现放 `Memory.Persistence`，契约留本模块。
3. **会话按 `sessionId` 隔离**。禁止把 `AgentSession` 当全局单例，也禁止跨 id 复用。
4. **没有能变红的负控，不新增能力**。

---

## 六、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

已覆盖（`tests/AgentWorkbench.UnitTests/Memory/`）：

- 正例：同 `sessionId` 连续取两次会话，拿到同一个对象；`SqliteSessionStore` + 真实 SQLite + 真实 MAF agent，
  「重启」（销毁 store/agent、复用同一库）后同 `sessionId` 的历史续上。
- 负控：换 `sessionId` 必须新建、不得读到别人会话；TTL 过期后历史必须丢，TTL 内必须续上；
  上限调小必须淘汰最久未用，上限关掉不淘汰；快照损坏（坏 JSON）必须丢掉重来、不得永久卡死。
- 架构：`Memory` 不得依赖 `Persistence`、`Context` 不得依赖 `Memory`/`Persistence`（负控：加真实依赖即变红）。

尚未覆盖：真·跨进程重启（验收脚本未落地）、同 `sessionId` 并发 run。

## 依赖

Shared。落库实现（`AgentWorkbench.Memory.Persistence`）：Memory、Persistence、Shared。
