# Domains/Gitea · 第三个动作域（第一个真实外部系统）

## 它是什么

方案第 1/2/3 步的第一个**真实**样板：Gitea 公开实例（`https://gitea.com`）作为考官，
本工程是它的 Connector + 业务能力目录。

判据来源：`docs/验证与现状/gitea-真实行为-20261108.md`（2026-11-08 实测，非推测）。
本工程所有状态码、时间格式、错误分类都以那份采集为准。

## 铁律

- 只引用 `Core.Ports`，零 NuGet 包、零 DI/Options 包。
- **加入本工程时 `src/Core` 的 diff 必须为 0**（AC-01）。契约收敛属于独立的前置动作，
  不能伪装成「接域时改了底座」。
- 配置缺失 → 拒绝且**零 HTTP**，不回落任何内存假实现。
- `AllowWrites=false`（默认）→ 写能力在目录里可见，但执行返回 `not_supported_in_m2` 且零 HTTP。
  M3 由组合根显式打开。

## 文件

| 文件 | 职责 |
|---|---|
| `GiteaActionSpecs.cs` | 业务能力目录（**按业务对象命名**，原语降级为属性） |
| `GiteaOptions.cs` | 连接参数，plain record |
| `IGiteaTransport.cs` / `HttpGiteaTransport.cs` | 方案第 2 步的 Connector：认证、重试、超时、失败分类 |
| `GiteaAdapter.cs` | `IActionExecutor`：请求映射、状态映射、响应白名单投影 |
| `GiteaPolicies.cs` | Schema / PDP / 业务规则 / 审批闸（四个注入口） |
| `GiteaActionSet.cs` | 组合注入点 |

## 与 Erp 域的三点差别（都是刻意的）

| | Erp | Gitea |
|---|---|---|
| 能力粒度 | `erp.query` 等，按**原语**命名 | `repo.query` / `issue.create` 等，按**业务对象**命名 |
| 时间 | Frappe naive 串，需按站点时区补偏移 | RFC3339 + `Z`，**原样透传** |
| 响应信封 | `{"data":{...}}` | 直接返回对象/数组 |

时间这条最要紧：把 Frappe 的「补偏移」逻辑套到 Gitea 上，会把 UTC 当本地时间，
差 8 小时且不报错。时间解释策略必须由域声明，不能进底座。

## 三条实测坑，已固化在 Adapter 里

1. `PATCH` 关闭议题返回 **201**，不是 200 → 成功状态集合含 200/201/204。
2. `DELETE` / **204 无 body** → 不允许无条件 `JsonDocument.Parse`。
3. 403 的 body 含 `token scope=...` 权限清单 → **绝不回给调用方**，只回通用错误码。

## 安全边界

- `GiteaResponse` 不携带响应头，transport 层拿不到 `Authorization` 之外的敏感头，
  TraceEvent 更拿不到。
- 成功数据按白名单投影（`kind/number/state/created_at/...`），不把原始响应整体写入审计：
  实测 `GET /api/v1/user` 返回 `email`。
- 401 = 凭据问题，403 = scope 不足，404 = 不存在**或无权看到其存在**（实测两者 body 字节级相同，
  故合并为 `not_found_or_forbidden`）。

## 未做（如实）

- 未接组合根 / `appsettings`，当前只由测试驱动。
- 未做 token scope 的自检端点（需要第二个低权限账号）。
- 审计与 run 状态仍内存存储，落库是 M2 第 5 步。
