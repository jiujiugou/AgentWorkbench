# AgentWorkbench.Knowledge.Abstractions

> 校准基线：`b4d5f2c`。本文只写边界；文件与成员以代码为准。

## 这一格是什么

底座知识层的**契约**，不含实现：引擎能力位、embedding 扩展点、一条检索命中的形状。
上层与实现方都依赖它，因此**这里不许出现业务词，也不许依赖任何具体实现项目**。

`RetrievalHit` 是跨语言边界的那条线：Python MCP server 返回的 hits 由 C# 反序列化成它，
来源面板与引用校验读同一份，`[n]` 编号才不会错位。

## 红线

1. 只放契约，不放实现、不放判定逻辑（判定在 `Knowledge.Local`）。
2. 换动作域时本模块不动；出现 ERP / 单据类名即为越界。
3. **字段形状是跨进程契约**：改 `RetrievalHit` 必须在同一次提交里同步 Python server 的输出，
   否则表现为「来源悄悄少一块」，不报错。
4. 只放还有消费者的接口；没有消费者的扩展点直接删，不留占位实现。

## 怎么验

`DomainSwapAc01Tests` 的 AC-02 禁词扫描覆盖本目录；
文件清单用 `find src/Core/Knowledge/AgentWorkbench.Knowledge.Abstractions -name '*.cs'`。
