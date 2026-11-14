// 本模块只放内核侧的轨迹数据与契约：ExecutionTrace / ExecutionStep / IExecutionTraceStore。
// 刻意只依赖 Shared（终态与归类码词表），不依赖任何存储或领域记录——
// 否则「内核写一条轨迹」就会把 EF Core 与业务记录拖进内核的编译期依赖。
global using AgentWorkbench.Shared;
