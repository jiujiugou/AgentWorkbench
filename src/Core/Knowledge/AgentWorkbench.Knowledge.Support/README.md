# AgentWorkbench.Knowledge.Support

> 校准基线：`b4d5f2c`。本文只写判断与边界；接口成员以 `IChatService.cs` 为准。

## 这一格是什么

**它是探活适配层，不是对话运行时。**

名字是 11-07 目录重组留下的历史：原「Conversation」模块被收拢进 `Runtime.Context` 之后，
这里只剩「模型是否就绪」这一个能力，`HealthController` 用它回答健康检查。
流式回答、历史装配、工具环全都不在这里，分别在 Hub、Harness、`Runtime.Context`。

2026-11-10 瘦身删掉了全仓零调用的流式生成实现，`IChatService` 因此只剩一个方法。
**先有消费者再有 API**：没有第二个消费者之前，不往这个接口加东西；
若将来探活换了实现，本模块没有别的消费者，可以整体删掉，不必为它保留一层抽象。

## 红线

1. **不在这里重新长出对话能力**。要流式回答走 `/hubs/agent`，要装配上下文走 `IContextAssembler`。
2. **探活不得触发副作用**：不建会话、不落库、不调模型生成。
3. **不把异常吞成空串**：错误要么冒泡，要么变成调用方能分辨的异常。

## 怎么验

健康检查在模型未配置 / 已配置两种状态下结论必须不同。
端点与方法名以 `Controllers/HealthController.cs` 和 `IChatService.cs` 为准，别抄本文。
