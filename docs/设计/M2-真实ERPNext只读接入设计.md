# M2 真实 ERPNext 只读接入设计

- **版本**：v0.1-draft ｜ **状态**：待确认 ｜ **日期**：2026-11-08
- **定位**：**派生设计 + 执行手册**。把正本 `docs/需求/产品目标与范围.md` v3.0（已确认）§7「M2 只读」、§4.1 第 1 / 第 5 句、§5.4 前提 #2 / #3，投影成一条可执行链路、一份采集清单、一组可被证伪的验收。
- **不是什么**：不是第三份事实源，不新增判据，不定义完成标准，不替代 ADR 0013–0018。冲突时一律以正本 + ADR 为准。
- **生效条件**：M1 已落地并接进宿主（提交 `0f39bdc` / `8c3a55f` / `a645df2` / `8152d8e` 一组；基线 92 条测试 82 绿 / 10 预期红）。本文仍是 draft，**不挂进 `AGENTS.md` 事实源表**。
- **前置未达成**：正本 §5.4 前提 #2（可连的 ERPNext 实例）目前**没有**。所以这份文档的第一步不是写代码，是**采集**。

---

## 0 设计对象

设计对象是**一次只读动作 run**：人要查一张真实采购单的状态，系统必须给出「ERP 里现在到底是什么状态、什么时候变的」，并留下能反查的证据。

一句话：

> **M2 的成果不是「能查到一张单」，而是第一次让外部系统决定我们代码的形状。**

假 ERP 的字段、状态、错误码全是我们编的，验证的是想象（正本 §3.3 原话）。真接入会第一次出现「我们的假设错了」——错在字段名、错在状态取值、错在时间没时区、错在一个我们以为会 404 的场景其实返回 200。**这些错暴露出来，才是 M2 的价值；不暴露才该怀疑。**

---

## 1 边界

### 1.1 本设计做

| 项 | 内容 |
|---|---|
| 真查询 | ERPNext REST 只读适配器：`GET /api/resource/Purchase Order/{name}` + 列表回退 |
| 权限四维 | 业务对象 × 操作 × 数据范围 × 金额阈值（M2 的读语义版本，见 §4） |
| 失败语义 | 查不到 / 无权限 / 凭据失效 / 上游错 / 超时 / 字段漂移，各有独立码，不混成一个 `failed` |
| 审计落库 | `IActionRunStore` / `ITraceSink` / `IAuditQuery` 从内存换 SQLite，重启不丢 |
| 换域判据 | 真域上线后 AC-01 仍成立：`src/Core` 一行不改 |
| 证据 | live 采集脚本 + 回放夹具 + 证据文件，三者可互相印证 |

### 1.2 本设计不做（每条都有依据）

| 不做 | 依据 |
|---|---|
| 真 `Draft` / `Submit` / `Cancel` | 正本 §7 把这些放在 M3。M2 里真域必须**显式拒绝**这三个原语，**不许回落到内存假 ERP** |
| 最小审批页前端 | 正本 §7 把「最小审批页」放在 M3；M2 沿用 M1 的后端端点与 `[Authorize(Roles=owner)]` 闸门 |
| 知识层 / 制度文档检索 | 正本 §7「M1~M3 不碰 Python 仓」；「依据哪条规则」仍由动作域 `BusinessRule` 的证据快照提供 |
| 改 ERP 结构（加自定义字段） | 会动客户环境，属设计决定，不在 M2 擅自动手。§8 只出证据与建议 |
| 通用连接器 / 第二个 ERP | 正本 §2.3「不做通用连接器」：一个 ERP、四个动作，跑通再抽象 |
| 用 ERP 的权限代替我们的 PDP | 会丢掉 fail-closed 第一道；底座必须能先拒绝，ERP 权限只做兜底（§4） |
| 把 live 未跑通说成完成 | 判据 1 明写「不是 mock 返回值」；回放夹具是测试手段，不是接入证据 |
| 顺手清理 M1 遗留 | 见 §1.3，M2 只接管「存储」一项 |

### 1.3 M1 带过来的遗留，M2 只接管一项

| 遗留 | M2 是否处理 |
|---|---|
| `IActionRunStore` / `ITraceSink` 内存实现，重启即丢 | ✅ **处理**（不处理则 live 验证没有证据可查） |
| 前端审批页缺失 | ❌ 留 M3 |
| 10 条既有预期红（F2.8 / F2.9 / F2.10 / F3.5 / F4.7） | ❌ 属工具层与既有模块，不在 M2 |
| 独立 `ArchitectureTests` 未重建（M0 L2） | ❌ 待用户裁决；四条门禁仍挂在 `DomainSwapAc01Tests` |
| §10 四项待确认（预检索 / 错误信封 / 多动作 / 本文转正） | ❌ 原样挂着，M2 不碰 |

---

## 2 前提与采集：先拿真相，再写代码

### 2.1 前提清单

| # | 前提 | 现状（2026-11-08） | 谁提供 | 不满足怎么办 |
|---|---|---|---|---|
| 1 | ERPNext 自托管实例（Docker，自带 demo 数据） | **未就位** | 用户 | 停在 §10 第 1 步，不写代码 |
| 2 | 集成账号：只读角色，仅有 Purchase Order / Supplier / Item / Company 的 read | 未创建 | 部署方 | 见 §4「账号权限最小化」 |
| 3 | API Key / Secret（`Authorization: token key:secret`） | 未生成 | 部署方 | 缺失即 `configuration_missing`，**不回落假 ERP** |
| 4 | 站点时区（`System Settings`） | 未知 | 采集 | 取不到则配置项显式给，**不许猜** |
| 5 | 站点与 Frappe / ERPNext 版本号 | 未知 | 采集 | 记进证据文件；版本漂移是 `schema_mismatch` 的来源 |

### 2.2 采集清单（§10 第 1 步的唯一产出）

**必须先采集、后写解析**。理由不是流程洁癖：错误码、时间格式、状态取值、filters 行为**不采集就是猜**，而猜出来的失败分支 100% 是错的——M1 里「`document_id` + `amount`」这种字段是我们自己编的，真系统里 `amount` 根本不存在，只有 `grand_total` / `net_total` / 明细行上的 `rate`。

采集项：

| 类 | 项 | 为什么必须 |
|---|---|---|
| 站点 | 版本号、时区、默认公司、默认币种、币种精度 | 时间与金额的正确性全依赖它 |
| 字段 | 一张真实 PO 的完整响应（Draft） | 认领字段，见 §7 |
| 字段 | 一张 Submitted、一张 Cancelled | `docstatus` 与 `status` 的对应关系 |
| 失败 | 不存在的单号 | 拿真实状态码与 body（官方文档未列状态码） |
| 失败 | 无权限账号读同一张单 | 区分 `forbidden` 与 `not_found`（**这两个混淆是最常见的越权探测漏洞**） |
| 失败 | 错误凭据 | 确认 401 的具体形态 |
| 参数 | `filters` 的 JSON 编码方式、`limit_page_length` 上限、`fields` 投影行为 | 决定列表回退怎么发 |
| 时间 | `modified` / `creation` 的原始字符串格式 | 官方示例是 `2019-06-03 14:19:00.281026`——**无时区偏移** |

产出：`docs/验证与现状/erpnext-采集-YYYYMMDD.md`（人读）+ `tests/.../Fixtures/erpnext/*.json`（机读，带 `captured_at` / `site_version` / `time_zone` 三个元数据字段）。**夹具里每一条都必须能追到一次真实响应**，禁止手写。

---

## 3 技术形状：四个新件，全在 `Domains/Erp`

```
src/Domains/Erp/
  ErpNext/
    IErpTransport.cs          ← 只做 HTTP，返回 (statusCode, body, elapsed)。不含业务语义
    HttpErpTransport.cs       ← 真网络；token 头；超时/重试；不记录 Authorization
    RecordedErpTransport.cs   ← 回放夹具（测试用），按 (method, path, query) 命中
    ErpNextQueryAdapter.cs    ← IActionExecutor 的 Query 分支：解析、映射、判定、组装 Data
    ErpNextActionSet.cs       ← IActionSet：真 Specs + 四维 PDP + 真 Executor（只读）
    ErpNextOptions.cs         ← BaseUrl / ApiKey / ApiSecret / TimeZone / Timeout / Doctype 名
    ErpErrorCodes.cs          ← §6 的失败码常量
```

### 3.1 四个决定，各自有理由

| # | 决定 | 理由 |
|---|---|---|
| 1 | **先抽象 `IErpTransport`，再写 adapter** | 不是为了「可测试」。是为了让**同一套解析逻辑既跑真网络、也跑真实采集到的夹具**——否则「回放通过」和「真接能跑」是两段不同的代码，回放绿了也证明不了真接 |
| 2 | **真域独立成 `ErpNextActionSet`，不复用 `ErpActionSet`** | 否则「现在跑的是真 ERP 还是假 ERP」只能靠记忆。独立后 `Name` 直接出现在待办响应与健康检查里，**看得见** |
| 3 | **禁止静默回落** | 配置缺失 / 实例不可达 → 明确 `configuration_missing` / `upstream_unavailable`，启动不崩但**不假装成功**。任何「自动 fallback 到内存假 ERP」都是把 M2 变成 M1 的化装舞会 |
| 4 | **动作域不引 DI / Options 包** | `ErpNextOptions` 是 plain record，由组合根从 `IConfiguration` 构造后传入。`Domains` 只允许依赖 `Core.Ports` + BCL（`HttpClient` 在 BCL 里，无需 `FrameworkReference`） |

### 3.2 只读域里，写动作必须显式拒绝

`ErpNextActionSet` 仍然注册 4 个 `ActionSpec`（否则模型会认为工具不存在，这比报错更糟），但 `Executor` 对 `Draft` / `Submit` / `Cancel` 一律返回：

```
Status = failed, Code = not_supported_in_m2, 零 HTTP 调用
```

**这条要能被计数断言证伪**（T10：transport 调用次数 = 0）。这不是保守，是把「半真半假的写路径」提前掐死——M2 里唯一比「不能写」更糟的，是「以为写了其实写进了内存」。

副作用提示：M1 的 S2 / S3 / S6 三条判据测试仍指向假域 `ErpActionSet`，**不动**。真域另有自己的测试。两套动作集并行存在，正是 AC-01 想要的形状。

### 3.3 宿主侧最小改动

| 落点 | 改动 |
|---|---|
| `Actions/ActionServiceCollectionExtensions.cs` | `Actions:ActionSet` 支持第三个取值 `ErpNext`；`ErpNext` 分支构造 `HttpClient` + `ErpNextOptions`，注入 `ErpNextActionSet` |
| `appsettings.json` | **显式写出** `"Actions": { "ActionSet": "Erp" }` —— 现在这个键根本不存在，靠代码默认值兜着；生产环境靠默认值决定跑真跑假，是不可接受的可观测性缺口 |
| `Controllers/HealthController.cs` | 响应加 `actionSet` 与 `erp`（`configured` / `mode`）两个字段。一条 curl 就能知道「我在跑哪一套」 |
| `Program.cs` | 无需改动（`AddNitroActions` 已注册） |

---

## 4 权限四维（M2 的读语义版本）

正本 §1.4 与 §5.1 B 定义四维：**业务对象 × 操作 × 数据范围 × 金额阈值**。M1 只用了一个 subject 白名单，那是占位。M2 要把它做实——但只读动作没有「审批层级」，所以四维在 M2 里是**可见性**语义，不是批准语义。

| 维 | M2 判据 | 数据来源 | 拒绝码 | 判定时机 |
|---|---|---|---|---|
| 1 业务对象 | 目标 doctype ∈ 允许集合（M2 只有 Purchase Order） | 策略 | `object_not_allowed` | 发请求前 |
| 2 操作 | `Query` 允许；`Draft` / `Submit` / `Cancel` 拒绝 | 策略 + spec | `operation_not_allowed` / `not_supported_in_m2` | 发请求前 |
| 3 数据范围 | 单据 `company` ∈ subject 的可见公司集合 | 策略（subject → companies） | `out_of_scope` | 拿到单据后 |
| 4 金额阈值 | 单据金额 ≤ subject 的可见上限 | 策略（subject → ceiling） | `amount_above_visibility_ceiling` | 拿到单据后 |

### 4.1 两条铁律

**铁律一：ERP 账号权限只做兜底，不做判据。**
即使集成账号能读到，我们也要拒。否则「我们的 PDP」就退化成「ERP 权限的转发层」，换域时这套判定全部作废（AC-01 立刻破）。

**铁律二：后置维度的拒绝，不得泄露被拒的内容。**
对象维与操作维在发请求前就拒（零调用）。范围维与金额维必须**先拿到单据才能判**，这是读语义的固有顺序，躲不掉。因此：

- 拒绝时 `ActionResult.Data` 只放拒绝码与 `document_id`，**不放** 供应商、金额、明细；
- 审计里只记 `document_id` + 判定结果 + 命中的策略 id，**不记** 金额与供应商；
- 否则「拒绝」本身成了信息泄露通道（能据此枚举别家公司有哪些单、金额多大）。

这条在 T11 / T12 里各有一条断言：**拒绝响应的 JSON 里不得出现金额数字与供应商名**。

### 4.2 策略从哪来

M2 用**动作域内的 plain JSON 策略文件**：`src/Domains/Erp/erp-policy.json`，形如

```json
{
  "subjects": {
    "wang": { "objects": ["Purchase Order"], "operations": ["Query"],
              "companies": ["华北智造"], "amountCeiling": 500000 }
  }
}
```

**不建数据库表**。理由：第一个真实客户都还没有，此刻设计出来的权限表结构必然错（正本 §4.2「不证明业务口径对」）。策略文件在业务域里，改它不碰 Core，AC-01 不受影响。

`subject` 来自登录名（M1 已修正：`User.Identity.Name`，不是 GUID）；与 Identity 的 `tenant_id` claim 做一致性检查，不一致按拒绝处理（不放行）。

---

## 5 审计五字段与落库

### 5.1 现状与缺口

M1 的 `InMemoryActionRunStore` / `InMemoryTraceStore` 一重启就空。M2 要走 live 验证，如果证据还在内存里，**「能反查」这句话在真环境里不成立**。

### 5.2 落点

新增 `src/Core/Engineering/AgentWorkbench.ActionStore.Sqlite/`（依赖 `Core.Ports` + `Persistence`，不依赖 `Domains`），迁移 `M030_CreateActionStore`：

| 表 | 关键列 | 为什么单独一张 |
|---|---|---|
| `action_runs` | `request_id` PK、`action_key`、`subject`、`state`、`code`、`external_id`、`approver`、`arguments_json`、`created_at`、`updated_at` | 一次 run 一行，供按 id 查终态 |
| `action_traces` | `id`、`request_id`、`stage`、`outcome`、`intent`、`arguments`、`approver`、`result`、`external_id`、`at` | 轨迹是追加流，不与 run 同行 |
| `action_idempotency` | `request_id` PK、`idempotency_key`、`claimed_at` | **去重表必须与结果表分开** |

最后一条是 M1 负控抓出来的教训：S3 那条测试原本是假绿，因为第二次调用在 `LoadResultAsync` 就被终态回放掉了，`TryClaimIdempotencyAsync` **从未执行**。合成一张表会让同一个假绿以另一种形式复活。

### 5.3 五字段的诚实说明

正本 §4.1 第 5 句写「任取一笔执行，五个字段齐全」。只读动作**没有审批人**，这是结构性的，不是实现漏了。

处理方式：**不伪造**。`approver` 落 `null`，`outcome` 标 `not_applicable:read_only`。M2 能证明的是：

| 问题 | M2 证据 |
|---|---|
| 谁让做的 | `subject`（登录名） |
| 什么时候 | `at`（带偏移的 ISO8601） |
| 看到了什么 | `arguments` 原文 + ERP 响应快照 hash |
| 依据哪条规则 | 权限判定的策略 id + `RuleDecision.Evidence` |
| ERP 里变成了什么 | `external_id` + `docstatus` + `status`（只读不改，故为「读到了什么」） |

「谁批的」这一栏在 M2 写 N/A。**严格读法下，§4.1 第 5 句要由 M3 的写动作来证明**——这属于判据解释，列入 §11 待裁决 #1，不自己放宽。

---

## 6 失败语义（唯一映射表）

> 判据 1 的字面要求是「**查不到时明确说查不到**」。所以 `not_found` 与 `failed` 必须分开；「一张单不存在」和「ERP 挂了」对用户是两件完全不同的事。

| 情形 | 现象 | Code | Status | 重试 | 用户措辞 |
|---|---|---|---|---|---|
| 单号不存在 | 404（body 含 `DoesNotExistError`） | `erp_not_found` | Failed | 否 | ERP 里查不到这张单，可能单号错或已删除 |
| 无权限 | 403（`PermissionError`） | `erp_forbidden` | Failed | 否 | 集成账号无权读该单据（部署问题，不是你的权限） |
| 凭据失效 | 401 | `erp_auth_failed` | Failed | **否** | 集成凭据失效 |
| 上游错误 | 5xx | `erp_upstream_error` | Failed | **读可重试 2 次，指数退避** | ERP 暂时不可用 |
| 超时 | `TaskCanceled` / 超时 | `erp_timeout` | Unknown | 读重试 1 次，仍失败转人工 | 状态未知 |
| 限流 | 429 | `erp_rate_limited` | Failed | 退避重试 1 次 | ERP 限流 |
| 200 但 body 含 `exc` / `_server_messages` | Frappe 的错误载荷 | `erp_error_payload` | Failed | 否 | 带上 `exc_type` 原文 |
| 字段缺失 | 缺 `docstatus` / `name` 等必填 | `erp_schema_mismatch` | Failed | 否 | 缺字段 x，站点版本可能不匹配 |
| 配置缺失 | 无 BaseUrl / 凭据 | `configuration_missing` | Rejected | 不发请求 | 未配置 ERPNext |

**读可以重试，写不可以。** 这是 M2 与 M3 的唯一分界线，也是全项目唯一允许自动重试的地方。写进这里，是为了让 M3 实现写动作时**不能顺手复用这套重试策略**——超时后重放写调用会建两张单（正本 §5.3 明令）。

---

## 7 数据契约最小集（字段认领表）

底座对参数与结果都是**不透明 JSON**，所以「字段」是动作域的事。但 M2 必须把「哪些字段是我们的硬依赖」写死，否则缺字段时会悄悄用 null 兜底，最后算出一张金额为 0 的单。

| 字段 | 用途 | 缺失行为 |
|---|---|---|
| `name` | `ExternalId` | **必填**，缺 → `erp_schema_mismatch` |
| `docstatus`（0/1/2） | 权威状态（Frappe 定义的提交态） | **必填**，缺 → `erp_schema_mismatch` |
| `status`（字符串） | 展示 + 业务规则输入 | **必填但按 opaque string 处理**：不硬编码取值（`To Receive and Bill` / `To Bill` / `Completed` 等随版本变） |
| `company` | 数据范围维 | **必填**，缺 → `erp_schema_mismatch`（无法判范围就不能放行） |
| `grand_total` + `currency` | 金额维 + 展示 | **必填**，缺 → `erp_schema_mismatch` |
| `supplier` | 展示 + 后续映射 | 可空 |
| `transaction_date` | 业务日期（date，无时区） | 可空 |
| `modified` | 状态变更时间 | **必填**，缺 → `erp_schema_mismatch` |
| `creation` / `owner` | 溯源 | 可空 |

### 7.1 时间：必须带时区，否则等于没给

ERPNext 返回的日期是**站点时区的 naive 字符串**（官方示例 `2019-06-03 14:19:00.281026`，无偏移）。直接当本地时间或 UTC 都会错，判据 1 要求「带时间戳」，给一个没偏移的字符串不算给。

做法：

1. 站点时区来自采集结果或配置项 `ErpNext:TimeZone`；两者都没有 → `configuration_missing`，**不猜本机时区**。
2. 转换后返回三个字段：`erp_modified_raw`（原样）、`erp_modified_at`（ISO8601 带偏移）、`erp_time_zone`。
3. T3 用「同一条 naive 串 + 两个不同站点时区」验转换，负控就是改时区让它红。

### 7.2 金额：不假设两位小数

Frappe 的数字是浮点，精度由 `Currency` 配置决定（取 `fraction_units` 一类字段，具体字段名以采集为准）。返回原始数值 + `currency` + `precision`（取不到就 `null`，**不编**）。M3 的金额档位判定必须基于这个精度，不能用 `decimal.Round(x, 2)` 想当然。

---

## 8 幂等能力查证（正本 §5.4 前提 #3）

正本原话：「有与没有是两套架构，**不能假设**」。M2 的任务是出结论，不是实现。

要回答三个问题（每条都要真实响应作证据）：

| # | 问题 | 怎么验 | 两种答案各自意味着什么 |
|---|---|---|---|
| 1 | 能否按「外部请求号」反查单据？ | 默认没有这个字段；查 `Purchase Order` 的字段表，确认无 `request_id` / `external_ref` 类字段 | 无 → M3 要么加自定义字段，要么只能靠我们侧记录 |
| 2 | 同一 payload 连发两次会怎样？ | 真实 POST 两次（**只在专用测试实例上做，不在客户库做**） | 会生成两张单 → ERP 无原生幂等 |
| 3 | 能否指定单号写入？ | 查 naming 规则与 `name` 字段可写性 | 能指定 → 有强幂等落点；不能 → 只能别处兜 |

结论二选一，交用户裁决（§11 #4）：

- **方案 A（强幂等）**：在 ERP 加 `custom_request_id` + 唯一索引，写入前按它查重。代价：改 ERP 结构，客户环境要迁移。
- **方案 B（弱幂等）**：只在 C# 侧 `action_idempotency` 去重 + 写后回写单号。代价：确认超时窗口内仍可能重复，需要人工对账兜底。

**M2 不选，只给证据与推荐。** 选错的代价不是在代码里，是在客户账上。

---

## 9 验收设计

### 9.1 判据挂载

| 判据 | 挂在哪 | 最小验证 |
|---|---|---|
| §4.1 第 1 句 真实 API 查到状态 | `ErpNextQueryAdapter.Query` | live 证据文件里有一条真实响应的 `docstatus` + `modified`；回放测试走同一解析器 |
| §4.1 第 1 句 后半「查不到明确说查不到」 | §6 `erp_not_found` | T4：404 → `erp_not_found`，措辞不是「系统错误」 |
| §4.1 第 5 句 五字段 | §5.3 | T13：重启后按 `request_id` 反查轨迹；`approver` 为 N/A 有明确标记（严格读法见 §11 #1） |
| AC-01 换域 diff 为空 | 与 M1 同一装置 | T17：`Erp` / `FakeActionSet` / `ErpNext` 三轮，`src/Core` 哈希不变 |
| AC-02 禁词 | 同一装置 | `ERPNext` 在 ADR 0017 §六 词表内，`src/Core` 命中数必须为 0 |

### 9.2 新增测试清单（每条都写「改坏哪行会红」）

| # | 测试 | 关键断言 | 负控（改坏什么会红） |
|---|---|---|---|
| T1 | 解析 Draft 真实响应 | `docstatus=0`、`external_id` 正确 | 把 `docstatus` 读成字符串比较 → 红 |
| T2 | 解析 Submitted | `docstatus=1`、`status` 原样保留 | 把 `status` 硬编码成枚举 → 红 |
| T3 | 时间转换 | naive 串 + 站点时区 → 正确偏移 | 改 `TimeZone` 配置 → 红 |
| T4 | 404 | `erp_not_found`，且**不**归 `erp_upstream_error` | 把 404 并进 5xx 分支 → 红 |
| T5 | 403 | `erp_forbidden`，与 `not_found` 不混 | 两种都映射成 `not_found` → 红（这会让越权探测变可行） |
| T6 | 401 | `erp_auth_failed`，transport 调用次数 **= 1**（不重试） | 给 401 加重试 → 红 |
| T7 | 5xx | 读重试后失败，调用次数 **= 3**（1 + 2 次重试） | 去掉重试 → 红 |
| T8 | 缺字段 | `erp_schema_mismatch` | 加默认值兜底 → 红 |
| T9 | 对象维 | 未注册 doctype → 拒绝且 transport 调用 **= 0** | 把判定放到请求之后 → 红 |
| T10 | 操作维 | 真域 `Submit` → `not_supported_in_m2` 且调用 **= 0** | 让写原语走假实现 → 红 |
| T11 | 范围维 | 其他 company → `out_of_scope`，响应 JSON **不含**金额与供应商名 | 在拒绝响应里带上原单据 → 红 |
| T12 | 金额维 | 超上限 → `amount_above_visibility_ceiling`，同样不泄露 | 同上 |
| T13 | 审计落库 | 新进程实例按 `request_id` 取回完整轨迹 | 换回内存实现 → 红 |
| T14 | 审计写失败 | DB 不可写 → **拒绝**，不执行 | 改成「写失败也放行」 → 红 |
| T15 | 凭据不落库 | 全表扫描不到 API Secret 字符串 | 把 header 记进 trace → 红 |
| T16 | 配置缺失 | `configuration_missing` 且调用 **= 0** | 回落到假 ERP → 红 |
| T17 | AC-01 三轮 | `src/Core` 哈希前后一致 | 在 `Core` 加一个探针文件 → 红（M1 已自证扫描器可红） |

### 9.3 负控要求（沿用 M1 §8.3，不放松）

每条新测试必须**实跑一次变异自证**：备份 → 精确改坏一处 → 跑全量 → 看到该条变红 → 还原 → 校验源码哈希一致。

M1 的教训照抄：**只断言「不为空 / 不抛异常」的不算测试**；两条假绿（S3 被终态缓存掩盖、AC01 同一次运行内前后快照天然相等）说明「测试自己也得被验一次」。

### 9.4 live 证据（是证据，不是测试）

`scripts/erp_live_probe.py`（Python，不用 PowerShell：本机只有 5.1，UTF-8 无 BOM 的中文脚本会解析失败）：

```
python scripts/erp_live_probe.py --verify   # 只验证，不写文件；退出码即结论（默认模式）
python scripts/erp_live_probe.py --record   # 采集 + 生成夹具 + 证据文件
python scripts/erp_live_probe.py --record --probe-bad-auth   # 额外采集 401 形态（只发一次）
python scripts/erp_live_probe.py --record --redact-host      # 证据里隐去主机名
python scripts/erp_live_probe.py --verify --insecure         # 自签名证书（仅本地实例）
```

`--verify` 的检查项（每项都能变红，见下表）：

| 项 | 内容 | 失败意味着 |
|---|---|---|
| V1 | 身份可达（`frappe.auth.get_logged_user`） | 地址/凭据/网络不通 |
| V2 | 真实单据可读且 `name` / `docstatus` / `modified` 齐全 | 字段漂移或权限不足，§7 的必填项假设错了 |
| V3 | 不存在的单号被明确拒绝（404/403） | 返回 200 说明「查不到」得靠 body 判，§6 映射表要改 |
| V4 | `filters` 真生效（返回行 `docstatus` 与请求一致） | filters 编码写错时 Frappe 常照样返回 200，只过滤没生效 |
| V5 | 无权限与不存在可区分（需 `ERPNEXT_FORBIDDEN_API_KEY/SECRET`） | 两者混淆会让「枚举单据」变得可行 |

产出 `docs/验证与现状/erpnext-live-YYYYMMDD.json`：`base_url`（脱敏，去掉凭据）、`site_version`、`time_zone`、各用例状态码与关键字段、`captured_at`；另有同批的 `erpnext-采集-YYYYMMDD.md`（人读，含字段表、时间原始串、失败形态、`filters` 生效性、待人工判读 4 条）。

三条规矩：

1. **live 未跑 = M2 未完成**，夹具不能替代。
2. 夹具必须由 `--record` 从真实实例生成，每份带 `__meta__`（`captured_at` / `site_version` / `time_zone` / `kind` / `document_name`），禁止手写。
3. 凭据只从环境变量读（`ERPNEXT_API_KEY` / `ERPNEXT_API_SECRET`），脚本不打印、不落文件；落盘前对所有内容再做一次字符串擦除。

**脚本自身的可红性已实证（2026-11-08）**：本机无 Docker、无实例，用一个临时假 Frappe 服务跑通 4/4；再用两个**故意做坏**的假服务证明检查项会红：

| 场景 | 结果 |
|---|---|
| 正常假服务 | V1–V4 全 PASS，退出码 0 |
| 忽略 `filters` + 缺失单号返 200 | **V3、V4 变红**，退出码 1，并打印「返回 200，失败语义要改」 |
| 错误凭据 | **V1 变红**（HTTP 401 + `exc_type=AuthenticationError`），退出码 1 |
| 端口不通 | **V1 变红**（`URLError: 目标计算机积极拒绝`），退出码 1 |

这次自检**只验证脚本，不产生任何夹具**：临时产物（7 个夹具 + 2 份证据）已全部删除，`git status` 只剩脚本本身。**不要把自检当成接入证据**——假服务的数据和真 ERPNext 无关。

---

## 10 实施依赖（7 步，依赖顺序不是排期）

| 步 | 内容 | 前置 | 完成标志 |
|---|---|---|---|
| 1 | 起实例 + 采集 + 生成夹具 | 无（**需要用户提供实例**） | §2.2 产出物齐；`--verify` 退出码 0 |
| 2 | `IErpTransport` + `RecordedErpTransport` + 解析器 | 1 | T1–T3 绿 |
| 3 | 失败语义 + 读重试 | 2 | T4–T8 绿 |
| 4 | 权限四维策略 | 2 | T9–T12 绿 |
| 5 | 审计 / 状态落库 + `M030` | 无（可与 2 并行） | T13–T15 绿 |
| 6 | 组合根接 `ErpNextActionSet` + 配置 fail-closed + 健康字段 | 3 / 4 / 5 | T16 绿；一条 curl 能看出在跑哪套 |
| 7 | AC-01 三轮 + 全量回归 + live 验证 + 台账 | 6 | T17 绿；live 证据落档 |

顺序理由只有两条：

- **先采集后编码**：字段与错误码是外部定的，猜一次就要返工一周。
- **落库必须早于接真域**（第 5 步先于第 6 步）：接上真域后审计还在内存里，live 验证跑完证据就没了，等于白跑。

### 10.1 文件清单

| 类型 | 路径 |
|---|---|
| 新增工程 | `src/Core/Engineering/AgentWorkbench.ActionStore.Sqlite/` |
| 新增迁移 | `Persistence/Migrations/M030_CreateActionStore.cs` |
| 新增（动作域） | `src/Domains/Erp/ErpNext/*.cs`、`src/Domains/Erp/erp-policy.json` |
| 修改 | `Actions/ActionServiceCollectionExtensions.cs`、`appsettings.json`、`HealthController.cs`、`AgentWorkbench.Erp.csproj`、`AgentWorkbench.slnx`、`AgentWorkbench.ApplicationTests.csproj`（夹具复制） |
| 新增测试 | `tests/AgentWorkbench.ApplicationTests/ActionGateway/ErpNext*.cs`（T1–T17）、`tests/.../Fixtures/erpnext/*.json` |
| 新增脚本 | `scripts/erp_live_probe.py` |
| 新增证据 | `docs/验证与现状/erpnext-采集-YYYYMMDD.md`、`erpnext-live-YYYYMMDD.json` |

---

## 11 待确认项

| # | 事项 | 为什么不能现在定 |
|---|---|---|
| 1 | **§4.1 第 5 句在只读里程碑怎么算过**：`approver` 允许记 N/A，还是必须等 M3 写动作才算过 | 这是**判据解释**，不是实现细节。按严格读法，M2 退出条件 #5 其实过不了；按「结构齐全 + 不适用有标记」的读法可以过。**需用户裁决，本文不自行放宽** |
| 2 | 金额阈值用哪个字段：`grand_total` / `net_total` / 含税 | 业务口径，正本 §4.2 明写「不证明业务口径对」 |
| 3 | 数据范围粒度：公司 / 成本中心 / 部门 | 取决于实例的 User Permission 配置与客户组织形态 |
| 4 | 幂等方案 A（改 ERP 加字段）还是 B（C# 侧去重） | 等 §8 的真实证据；选错代价在客户账上 |
| 5 | 是否允许改 ERP 结构 | 影响客户可迁移性与交付成本 |
| 6 | 实例从哪来（本机 Docker / 客户沙箱）、版本是否锁 15 | 决定采集结果是否可复现 |
| 7 | 独立 `ArchitectureTests` 是否重建 | M0 L2 仍开着，M2 不擅自处理 |

---

## 12 执行台账（模板，执行时逐行填）

> 只记「哪步做完、证据在哪、提交是哪个」，不新增判据。

| 步 | 状态 | 证据路径 | 提交 | 日期 |
|---|---|---|---|---|
| 1 采集 | 采集器就绪 / 实例未就位 | `scripts/erp_live_probe.py`（自检 4/4 + 3 个反向变红，见 §9.4）；真实产物待跑 | — | — |
| 2 传输 + 解析 | 未开始 | — | — | — |
| 3 失败语义 | 未开始 | — | — | — |
| 4 权限四维 | 未开始 | — | — | — |
| 5 审计落库 | 未开始 | — | — | — |
| 6 组合根 | 未开始 | — | — | — |
| 7 验收 + 台账 | 未开始 | — | — | — |

---

## 13 一句话

**M2 交付的不是「能查一张单」，是一条被外部系统改造过的链路**：字段是 ERP 定的、状态是 Frappe 定的、错误码是实测出来的、时间带时区、拒绝对用户和对审计都说得出原因。如果采集完发现我们原先假设的字段错了三处，那是 M2 成功的证据。
