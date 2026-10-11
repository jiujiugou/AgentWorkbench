# AgentWorkbench.Memory

状态与记忆模块（功能域 4 的「记忆」侧）：**会话存储** —— 按 `sessionId` 复用同一个 Agent 会话对象。

## 功能

| 文件 | 说明 |
| --- | --- |
| `SessionStore.cs` | `ISessionStore.GetOrCreateAsync(sessionId, factory)`；实现为进程内 `ConcurrentDictionary`，**重启即丢、多实例不共享**（持久化见缺口清单 P1-3） |
| `MemoryOptions.cs` | 配置节 `Harness:Memory`：`SessionEnabled=true`（关掉则每次 run 新建会话，无多轮记忆） |
| `MemoryServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroMemory` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `ISessionStore` | `GetOrCreateAsync` | `SessionStore` | AgentHarness `RunAsync` |

## 边界（容易混淆的点）

| 关注点 | 归属 |
| --- | --- |
| 会话对象容器（`sessionId → AgentSession`） | **本模块** |
| 历史消息怎么进上下文（裁剪/压缩） | `AgentWorkbench.Context` |
| 对话历史与执行记录落库、任务恢复 | `AgentWorkbench.Persistence`（+ `AgentWorkbench.Observability`） |

## 依赖

Shared。
