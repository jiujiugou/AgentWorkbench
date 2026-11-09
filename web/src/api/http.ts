/**
 * HTTP 的唯一出口：错误信封、401 处理、空响应。
 *
 * 在这之前，`documents` / `conversations` / `knowledge` / `settings` 各自有一份
 * `handle`，四个版本只有微小的差别——而差别都不是设计出来的：
 * 一个把响应体原文当错误消息（于是用户看见一整串 JSON），一个不处理 204，
 * 一个不处理空响应。四份实现意味着「改一处」的定义是「记得改四处」。
 *
 * 401 的处理必须是**一处**：会话过期这件事没有「哪个接口」之分。挂在四个
 * `handle` 里的版本，漏掉的那一个会让用户停在一个不停报错、又不敢跳登录页的界面上。
 */

/** 后端错误信封（F6 S-V4 五字段）。`request_id` 是给人拿去对日志的。 */
export interface ErrorEnvelope {
  code?: string
  http_status?: number
  message?: string
  retryable?: boolean
  request_id?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly requestId: string

  constructor(status: number, body: ErrorEnvelope, fallbackText: string) {
    super(body.message || fallbackText || `HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.code = body.code ?? ''
    this.requestId = body.request_id ?? ''
  }
}

let unauthorized: (() => void) | null = null
let passwordChangeRequired: (() => void) | null = null

/**
 * 注册「会话失效」的处理。**由 `main.ts` 装一次**，不是每个调用点各装一次。
 *
 * 不在这里直接跳路由：`api/` 层不认识 vue-router，让它认识的话，
 * 「一个 http 请求要经过几个模块才能变成一次页面跳转」就没人说得清了。
 */
export function onUnauthorized(handler: () => void): void {
  unauthorized = handler
}

/** 注册「首登必须改密」的处理。与 401 分开：**登录态仍然有效**。 */
export function onPasswordChangeRequired(handler: () => void): void {
  passwordChangeRequired = handler
}

/** 从一段响应文本里取「给人看的那句」。

    给拿不到 `Response` 的调用方用：上传走 XHR（要进度条），`load` 事件只给
    `responseText`。它也曾经是四份 `parseError` 的复制品之一。 */
export function messageOf(text: string): string {
  if (!text) return ''
  try {
    const body = JSON.parse(text) as ErrorEnvelope
    return body.message ?? text
  } catch {
    return text
  }
}

/** 把一次失败响应变成 `ApiError`。不触发 401 钩子——调用方决定。 */
export async function toApiError(response: Response): Promise<ApiError> {
  const text = await response.text().catch(() => '')
  let body: ErrorEnvelope = {}
  try {
    body = JSON.parse(text) as ErrorEnvelope
  } catch {
    // 非 JSON（网关的错误页、502 的 HTML）：原文当消息，别把它吞成「HTTP 500」
    body = { message: text }
  }
  return new ApiError(response.status, body, text)
}

/**
 * 成功 → 解析；失败 → 抛 `ApiError`，并在 401 时通知全局。
 *
 * 空响应返回 `undefined`：`204 No Content` 是三种接口的合法返回（登出、改密、
 * 停用），而 `response.json()` 在空 body 上抛的是 `Unexpected end of JSON input`——
 * 那句话会被原样显示给用户。
 */
export async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const error = await toApiError(response)
    if (response.status === 401) unauthorized?.()
    else if (response.status === 403 && error.code === 'password_change_required') {
      passwordChangeRequired?.()
    }
    throw error
  }
  // `204 No Content` 与「200 但没 body」走同一条：没有正文就不解析。
  // **不单独判 204**——单判的那一行删掉之后行为不变（`No Content` 本来就读出空串），
  // 而一行删掉不改变行为的代码，等于一个没人能验证的断言。
  const text = await response.text()
  if (!text) return undefined as T
  return JSON.parse(text) as T
}
