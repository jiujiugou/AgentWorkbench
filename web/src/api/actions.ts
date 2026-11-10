/**
 * 动作（Host `/api/actions`）：提交、待办、审批、历史、审计反查。
 *
 * 为什么必须有这个模块：`propose_action` 递交的写动作在 `RequiresApproval=true` 时
 * 会停在 `pending_approval`，**批准前零外部调用**。没有界面的话，模型一挂起就没有下一步——
 * 后端 `/pending`、`/approve`、`/reject` 三个端点齐全，但产品里走不到。
 *
 * 权限：列表所有登录用户可读；批准/拒绝只对 owner 生效（服务端 `[Authorize(Roles="owner")]`）。
 * 审批人由服务端从登录态取，请求体不带——客户端自报审批人会把「谁批的」交给客户端。
 */
import { handle } from './http'

/**
 * 一条待审批动作，字段与 `GET /api/actions/pending` 一一对应。
 *
 * 前五个「给人看」的字段由动作域提供（`ActionSpec`），不是前端拼的：
 * 审批人看不懂 `issue.create` 和一段 JSON，他只看得懂「创建议题 / 中风险 / 会留下真实变更」。
 */
export interface PendingAction {
  requestId: string
  /** 内部动作名，工程排障用；面向审批人显示 `displayName`。 */
  actionKey: string
  /** 给人看的动作名（如「创建议题」）。域没写时退回 `actionKey`，不编。 */
  displayName: string
  /** 域写的说明：这类动作做什么、有什么后果。可能为 null。 */
  description: string | null
  /** `low` / `medium` / `high` / `critical`。**只展示，不参与任何判定**。 */
  riskLevel: string | null
  /** 是否会在外部系统留下变更。用来决定要不要提示「撤销方式」。 */
  sideEffecting: boolean
  /** `Query` / `Draft` / `Submit` / `Cancel`：操作类型，不是风险等级。 */
  primitive: string | null
  subject: string
  /** 对底座不透明：原样透传的参数 JSON，界面只负责展示。 */
  arguments: unknown
  at: string
  /** 当前动作集名（`Gitea` / `Erp` / `Fake`），换域时告诉人这一单属于哪个外部系统。 */
  actionSet: string
}

/** 批准/拒绝后的动作终态。`externalId` 是外部系统编号（如 Gitea issue 号）。 */
export interface ActionResultDto {
  requestId: string
  status: string
  code: string | null
  externalId: string | null
  errorCode: string | null
  data: unknown
}

/**
 * 动作历史摘要（`GET /api/actions`）。**只有摘要**，细节走 `audit()`。
 *
 * `status` 为 `null` 表示这一单尚未收口（待审批 / 执行中 / 进程中断）——
 * 界面不得把它显示成成功，也不得显示成失败。
 */
export interface ActionSummary {
  requestId: string
  actionKey: string
  at: string
  status: string | null
  code: string | null
  externalId: string | null
}

/**
 * 审计轨迹的一条（`GET /api/actions/{id}/audit`），五字段在这里齐全：
 * 谁（`approver`）/ 何时（`at`）/ 看到什么（`intent` + `arguments`）/
 * 依据哪条规则（`stage` + `outcome` + `result`）/ 变成什么（`externalId`）。
 */
export interface TraceEvent {
  requestId: string
  /** 四道闸门之一（`schema` / `permission` / `business_rule` / `audit`）或 `approval` / `execute`。 */
  stage: string
  /** `allow` / `deny` / `written` / `pending` / `succeeded` / `failed` / … */
  outcome: string
  /** 业务动作名（写入时即 `ActionKey`）。 */
  intent: string
  arguments: string
  approver: string | null
  /** 这一格的结论说明：通过理由、拒绝理由、外部返回摘要。 */
  result: string | null
  externalId: string | null
  at: string
}

export const actionsApi = {
  pending: () => fetch('/api/actions/pending').then((r) => handle<PendingAction[]>(r)),

  /** 最近的动作摘要，按提交时间倒序。只读，不启动任何东西。 */
  list: (limit = 50) =>
    fetch(`/api/actions?limit=${limit}`).then((r) => handle<ActionSummary[]>(r)),

  /**
   * 按请求号查终态。**200 = 已有终态，202 = 仍待审**，404 = 查无此单。
   * 202 不是失败，所以这里不抛错，由调用方按 `status` 分支。
   */
  get: (requestId: string) =>
    fetch(`/api/actions/${encodeURIComponent(requestId)}`).then((r) =>
      handle<ActionResultDto | { requestId: string; status: string; code: string }>(r),
    ),

  /** 审计反查：完整轨迹，按发生顺序。查无记录返回空数组，不报错。 */
  audit: (requestId: string) =>
    fetch(`/api/actions/${encodeURIComponent(requestId)}/audit`).then((r) =>
      handle<TraceEvent[]>(r),
    ),

  /** 批准并执行。外部调用在批准之后才发生。 */
  approve: (requestId: string) =>
    fetch(`/api/actions/${encodeURIComponent(requestId)}/approve`, { method: 'POST' }).then((r) =>
      handle<ActionResultDto>(r),
    ),

  /** 拒绝：零外部调用，直接进终态。理由由界面强制必填（见 `ApprovalsView`）。 */
  reject: (requestId: string, reason: string) =>
    fetch(`/api/actions/${encodeURIComponent(requestId)}/reject`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ reason }),
    }).then((r) => handle<ActionResultDto>(r)),
}
