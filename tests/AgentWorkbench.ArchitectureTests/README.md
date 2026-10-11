# AgentWorkbench.ArchitectureTests

架构约束测试（ArchUnitNET + xUnit），以测试强制模块边界，防止依赖腐化。

## 功能

| 文件 | 说明 |
| --- | --- |
| `LayeringTests.cs` | 分层约束：Domain 必须保持纯净，不得依赖 Persistence / Webapi / Documents / Knowledge / Providers 等基础设施 |
| `ModuleCycleTests.cs` | 顶层模块之间不得出现循环依赖；切片必须覆盖所有预期模块 |
| `ModuleSlices.cs` | 按 `AgentWorkbench.<Module>` 顶层命名空间切分为模块切片 |
| `TestArchitecture.cs` | 加载待测程序集，供各规则复用 |

预期模块：Shared、Domain、Persistence、Documents、Knowledge、Providers、ModelRuntime、Conversation、
ToolExecution、Safety、AgentLoop、Context、Memory、Observability、AgentHarness、Workflow、Webapi。

负控：`Modules_should_not_depend_on_webapi` —— 任何模块反向 `using AgentWorkbench.Webapi` 都必须让本用例变红。

## 运行

```bash
dotnet test tests/AgentWorkbench.ArchitectureTests
```
