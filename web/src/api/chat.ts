/**
 * 通用对话 · `POST /api/chat`（F7）
 *
 * 与 `knowledge.ts` 分开，不是为了少写一个 if：两边的合同不同。通用对话
 * 没有 sources / citations，也不接受“资料不足”这个终态；把这些字段塞进
 * 同一个响应类型，会让前端看起来两边可以互换，而它们不能。
 */

import { handle } from './http'

export interface GeneralChatResponse {
  conversationId: string
  runId: string
  status: string
  /** 恒为 general。界面据此显示“未使用企业资料”。 */
  mode: 'general'
  answer: string
  /** 服务端显式给空数组；界面不需要猜字段缺失的含义。 */
  sources: unknown[]
  citations: unknown[]
}

export async function chatGeneral(
  question: string,
  conversationId?: string | null,
  signal?: AbortSignal,
): Promise<GeneralChatResponse> {
  const response = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question, conversationId: conversationId || null }),
    signal,
  })
  return handle<GeneralChatResponse>(response)
}
