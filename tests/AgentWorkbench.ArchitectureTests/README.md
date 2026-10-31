# AgentWorkbench.ArchitectureTests

架构约束测试（ArchUnitNET + xUnit），以测试强制模块边界，防止依赖腐化。

## 功能

| 文件 | 说明 |
| --- | --- |
| `LayeringTests.cs` | 分层约束：Domain 必须保持纯净，不得依赖 Persistence / Webapi / Documents / Knowledge / Providers 等基础设施 |
| `ModuleCycleTests.cs` | 顶层模块之间不得出现循环依赖；切片必须覆盖所有预期模块 |
| `ModuleSlices.cs` | 按 `AgentWorkbench.<Module>` 顶层命名空间切分为模块切片 |
| `ConventionTests.cs` | 代码形状约定：控制器不许自己计时（C7）、长驻工作必须由宿主启动（C4a）、仓储写方法必须接受事务参数（§8.3） |
| `TestArchitecture.cs` | 加载待测程序集，供各规则复用 |

## ConventionTests 的判法

用 ArchUnitNET 的**类型依赖**判，不用文本正则——注释里写 `Stopwatch` 不算违规，只有真用了才算。

三条规则都是**棘轮**：基线数字是实测的违规类型数，只许减少不许增加。修掉一处就把基线调小，新增一处就红。
要求立刻归零会让测试长期红着，红久了没人看，所以先冻结现状。

| 规则 | 判据 | 基线 |
| --- | --- | --- |
| C7 | Controller 类型依赖 `System.Diagnostics.Stopwatch` 的个数 | 3（Documents / Knowledge / Settings） |
| C4a | 非 `BackgroundService` 子类依赖 `PeriodicTimer` / `Thread` / `Timer` 的个数 | **0**（必须为零） |
| §8.3 | `DocumentRepository` 里 `Insert|Update|Delete|Replace*Async` 缺 `IDbTransaction` 参数的个数 | 5（存量不追溯，新增必须带） |

**做不到的**：`C1 错误响应只在中间件产出`（判断「这个匿名对象是不是错误体」是语义问题，类型层面看不见）、`判据 17 删除入口唯一`（要调用图）。这两条只能靠文本扫描，暂未实现。

预期模块：Shared、Domain、Persistence、Documents、Knowledge、Providers、ModelRuntime、Conversation、
ToolExecution、Safety、AgentLoop、Context、Memory、Observability、AgentHarness、Workflow、Webapi。

负控：`Modules_should_not_depend_on_webapi` —— 任何模块反向 `using AgentWorkbench.Webapi` 都必须让本用例变红。

## 运行

```bash
dotnet test tests/AgentWorkbench.ArchitectureTests
```
