# ADR-0001：采用多项目模块化单体

- 状态：已采纳
- 日期：2026-10-05
- 决策者：AgentWorkbench

## 背景
原为单项目 AgentWorkbench.Server，所有功能按技术分层堆在
Controllers/Application/Persistence/Knowledge/Documents 下。
新增能力（工具、Agent 运行时、记忆、多变体知识引擎）会跨多
层改文件，边界靠约定、易腐化，无法"只加不改"。

## 决策
重构为多项目模块化单体（参考 NitroGateway）：
1. 每模块独立 .csproj：Shared / Domain / Persistence / Documents /
   Knowledge.Abstractions / Knowledge.Local / Knowledge / Providers / Webapi。
2. 模块自注册：AddNitroPersistence / AddNitroProviders /
   AddNitroKnowledge / AddNitroDocuments；Program.cs 仅组合根。
3. 变体走"抽象 + 实现 + 聚合"：Knowledge.Abstractions（契约）→
   Knowledge.Local（本地实现）→ Knowledge（聚合注册）。
4. 依赖单向：模块间只依赖对方契约；Domain 只依赖 Shared。
5. ArchUnitNET 架构测试强制边界（Domain 纯净 / 无循环 / 切片覆盖）。

## 影响
- 正面：新增能力 = 新增模块/类，零改动既有模块；边界编译期+测试双重保证。
- 负面：多项目带来少量样板；构建/引用管理更复杂。
- 迁移：一次性拆分，已通过 P0 29/29、P1 9/9、架构 3/3 回归。

## 备选方案
- 保持单项目 + 文件夹模块：边界靠约定，不满足"只加不改"。
- 微服务：MVP 规模过重，运维成本高，否决。
