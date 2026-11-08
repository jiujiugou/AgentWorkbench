// Core.Ports —— 底座与动作域之间唯一的契约边界。
// 只放接口与 record，不放实现；不认识任何业务字段。
// 命名绑定 M1 设计 §3.4，改名会让 F5/X 组的契约存在性验收断掉。

global using System.Text.Json;
