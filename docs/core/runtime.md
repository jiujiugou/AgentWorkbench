# Core / Runtime

- **适用侧**：底座 ｜ **寿命**：现状（就近维护）｜ **最后校准**：2026-11-09
- **对应代码**：`src/Core/Runtime/`（6 个项目）
- **回答**：这条链怎么走、先读哪个文件、哪里不能碰、怎么验证
- **不回答**：单模块内部怎么实现（各项目 README 已写）、动作网关与翻译工具（→ [core/tools.md](tools.md)）、端口契约（`core/ports.md` 待写）、完成度与测试条数

> **本文件是地图，不是说明书。** 6 个子项目各自已有 README，细节在那里、也在代码注释里；这里不复制，只给「遇到 X 去查 Y」。
>
> **最重要的现状**：Runtime 这条链只服务 **v1 知识问答**；v3 主线的受控动作链不走这里。模型的入口是 `propose_action`（`Core/Tools/Builtin`），四道网关与落库见 [core/tools.md](tools.md)。别把本文件读成「产品主线怎么跑」。

## 1. 路由（先读这三个，别全仓 grep）

| 想知道 | 先读 |
| --- | --- |
| 一次提问从 Hub 到答案怎么走 | `Runtime.Harness/README.md` §一「一次 run 的流水线」 |
| 工具怎么被模型看到、调用怎么落地、审批在哪 | `Runtime.Tools/README.md` §一 |
| 上下文怎么注入、为什么用装饰器 | `Runtime.Context/README.md` §三 |

其余按需：`Runtime.Loop/README.md`（迭代预算 / 重复调用 / token 预算 / 终态归类）、`Runtime.Session/README.md`（会话与记忆、与 Context 的边界）、`Runtime.Model/README.md`（框架提供 vs 自研、配置节）。

代码入口（名字会变，**用 grep 定位，别记行号**）：`AgentHub.RunAsync` → `IAgentHarness.RunAsync` → `AgentHarnessService.BuildAgent` → `ToolExecutionPipeline`（装配 `FunctionInvokingChatClient`）→ `IAgentToolProvider.GetTools()`。

## 2. 链的形状（跨模块，单个 README 里没有的一段）

```
ChatView (SignalR)
  → /hubs/agent   Send(通用) / Ask(知识)
  → AgentHub.RunAsync            建/取会话、起一次 harness 流、按事件推进前端
  → AgentHarnessService          装配 ChatClientAgent；ChatOptions.Tools = ToolCatalog.GetTools()
  → IContextAssembler.Wrap       IChatClient 装饰器，把指令与片段注入请求最前
  → FunctionInvokingChatClient   工具环：迭代上限 / 重复调用 / token 预算 / 审批闸
                                 / 异常归类 tool_error / 结果截断 / ToolCallTrace
  → 内置只读工具（6 个，见白名单）
  → Hub 事件推回前端
```

- `Send` 与 `Ask` 只差 `knowledgeMode`，**不是两条独立链**。
- `IAgentToolProvider` 是工具提供方唯一契约，单独成工程以避免反向依赖整个执行管道；`ToolCatalog` 按名字裁剪白名单。
- **主线不在这条链上**：受控动作走 `propose_action` → `ActionRunService` → `Core/Tools/ActionGateway` → 落库，见 [core/tools.md](tools.md)。

## 3. 代码推不出的坑（本文件最值钱的部分，只给指针）

| 现象 / 约束 | 为什么 | 去哪核 |
| --- | --- | --- |
| 不要给 `ChatClientAgent` 挂 `AIContextProviders` | MEA 会在我们装配的函数调用环外再包一层，绕过守卫与轨迹（实测：同参重复不再判 `loop_detected`、工具异常不归类 `tool_error`） | `Runtime.Context/README.md` §三；`IContextAssembler.Wrap` 的注释 |
| `Harness:Loop` 必须显式配 | 不配真模型必 504；现为 `MaxIterations=6` / `TimeoutSeconds=300` / `MaxRepeatedToolCalls=3` | `appsettings.json`；`Runtime.Loop/README.md` |
| 新工具「没生效」先查白名单 | `Harness:Tools:Whitelist` 按名裁剪，**裁掉静默不报错**；现为 7 个（含 `propose_action`） | `ToolCatalog.cs`；`appsettings.json` |
| 底座侧不许出现业务词 | AC-02；Runtime 尤其容易在 Context 拼装处被塞进业务语义 | 在 `src/Core` 扫业务词（禁词门禁） |
| 工具异常不能在环外吞掉 | 工具环负责归类 `tool_error` / `loop_detected`，在外面 catch 会丢轨迹 | `Runtime.Tools/README.md` §一 |

> **环境坑就地记，不指向其他文档**：`dotnet restore` 在本会话 shell 坏（`NuGet.targets(780)` 的 `path1` 为空），只能 `--no-restore`；`curl` 打 localhost 加 `--noproxy '*'`；`global.json` 锁定 10.0.201，别移走；后端运行时锁 `bin/**/*.dll` 会让整解决方案 build 失败（MSB3027），先停服务再 build。

## 4. 改动规矩（可检查）

- 新增能力 = 加类 / 加模块，**0 改旧代码**；模块间只依赖对方契约。
- 上下文注入只能走 `IChatClient` 装饰器；Agent 配置处 `grep AIContextProviders` 应为 0。
- 新工具必须实现 `IAgentToolProvider` **且**名字加进白名单，否则静默不可见。
- `Core` 下任何项目不得 import `Domains`；参数对底座是不透明 JSON。
- 改这里**不必**改上层骨架文档——只有边界变了才改（AC-01-doc）。

## 5. 验证（逐字可跑）

```
dotnet build AgentWorkbench.slnx --no-restore --nologo
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --no-build --nologo
cd web; npm run build
```

- 本会话 shell 的 restore 坏（`NuGet.targets(780)` 的 `path1` 为空），只能 `--no-restore`。
- 测试条数与红绿以实测为准，不写死。

## 6. 待核 / 我不知道（带日期）

- **2026-11-08**：11-01 端到端真跑曾出现「模型零检索、凭常识编六条建议，系统判 `unsupported` 但答案照给」。当前来源面板已改由工具完成事件驱动，**同类问题是否仍存在未复测**；不把旧结论当现状。
- `Runtime.Model` 引用 `Core/Foundation/Domain`（v1 文档业务记录）但零使用，是否该清未定。
- `AgentHarnessService` 构造依赖较多，是否有收拢空间未评估。

---

## 写法

见 [docs/README.md](../README.md) §6「模块文档写法」。本节不重复定义——同一套规矩只写一处。
