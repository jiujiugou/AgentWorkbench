/**
 * 停止一次运行 · `POST /api/runs/cancel`（REQ-16）。
 *
 * 与 SignalR 的 `Stop` 不同，这是 Python 后端真实存在的 HTTP 入口：
 * 作答请求本身是普通 fetch，停止必须另发一条请求把 run 收口成 `cancelled`。
 * `conversationId` 能定位到当前会话，`runId` 用于已经拿到 id 的调用方；
 * 两个都不给时，服务端只收口当前用户最新的 `running`。
 */

import { handle } from './http'

export interface CancelRunResponse {
  /** 被收口的那次运行；没有在跑时为 null。 */
  runId: string | null
  /** 新会话首问时，前端还不知道会话 id，靠这次响应把它接回来。 */
  conversationId: string | null
  /** 收口后的真实状态；已经结束时原样返回终态。 */
  status: string | null
  /** 只有真的从 running 迁到 cancelled 才为 true。 */
  cancelled: boolean
}

export async function cancelRun(
  options: { runId?: string | null; conversationId?: string | null } = {},
): Promise<CancelRunResponse> {
  const response = await fetch('/api/runs/cancel', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      runId: options.runId ?? null,
      conversationId: options.conversationId ?? null,
    }),
  })
  return handle<CancelRunResponse>(response)
}
