import { ref, shallowRef } from 'vue'
import * as signalR from '@microsoft/signalr'
import type { RetrievalSource } from '@/api/knowledge'
import { conversationsApi } from '@/api/conversations'

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  pending?: boolean
}

export interface ChatTurn {
  role: 'user' | 'assistant'
  content: string
}

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected'

const CONVERSATION_KEY = 'aw.conversationId'

/**
 * Agent 实时通道：管理 SignalR 连接与流式消息状态。
 * 会话消息持久化在服务端，刷新/切换后按 conversationId 从服务端恢复。
 */
export function useAgent() {
  const connection = shallowRef<signalR.HubConnection | null>(null)
  const status = ref<ConnectionStatus>('disconnected')
  const messages = ref<ChatMessage[]>([])
  const generating = ref(false)
  const conversationId = ref<string | null>(null)
  /** 每条助手消息的检索来源（按 messageId 索引） */
  const sources = ref<Record<string, RetrievalSource[]>>({})

  function findOrCreate(messageId: string): ChatMessage {
    let msg = messages.value.find((m) => m.id === messageId)
    if (!msg) {
      msg = { id: messageId, role: 'assistant', content: '', pending: true }
      messages.value.push(msg)
    }
    return msg
  }

  function setConversation(id: string): void {
    conversationId.value = id
    try {
      localStorage.setItem(CONVERSATION_KEY, id)
    } catch {
      /* ignore storage errors */
    }
  }

  async function connect(): Promise<void> {
    if (connection.value) return
    status.value = 'connecting'

    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/agent')
      .withAutomaticReconnect()
      .build()

    conn.on('ReceiveConversation', (p: { conversationId: string }) => {
      if (p?.conversationId) setConversation(p.conversationId)
    })

    conn.on('ReceiveDelta', (p: { messageId: string; delta: string }) => {
      findOrCreate(p.messageId).content += p.delta
    })

    conn.on('ReceiveSources', (p: { messageId: string; sources: RetrievalSource[] }) => {
      findOrCreate(p.messageId)
      sources.value[p.messageId] = p.sources
    })

    conn.on('ReceiveDone', (p: { messageId: string; conversationId?: string }) => {
      findOrCreate(p.messageId).pending = false
      if (p?.conversationId) setConversation(p.conversationId)
      generating.value = false
    })

    conn.on('Error', (p: { message: string }) => {
      generating.value = false
      const pending = [...messages.value].reverse().find((m) => m.pending)
      if (pending) {
        pending.pending = false
        pending.content += (pending.content ? '\n\n' : '') + '⚠️ ' + p.message
      } else {
        messages.value.push({ id: crypto.randomUUID(), role: 'assistant', content: '⚠️ ' + p.message })
      }
    })

    conn.onreconnecting(() => {
      status.value = 'connecting'
    })
    conn.onreconnected(() => {
      status.value = 'connected'
    })
    conn.onclose(() => {
      status.value = 'disconnected'
    })

    await conn.start()
    connection.value = conn
    status.value = 'connected'
  }

  async function dispatch(method: 'Send' | 'Ask', text: string): Promise<void> {
    const content = text.trim()
    if (!content || generating.value) return

    await connect()

    messages.value.push({ id: crypto.randomUUID(), role: 'user', content })
    generating.value = true

    try {
      // 历史由服务端按 conversationId 从数据库加载，前端不再回传。
      await connection.value!.invoke(method, { conversationId: conversationId.value, text: content, history: [] })
    } catch (e) {
      generating.value = false
      messages.value.push({ id: crypto.randomUUID(), role: 'assistant', content: '⚠️ ' + (e as Error).message })
    }
  }

  const send = (text: string) => dispatch('Send', text)
  const ask = (text: string) => dispatch('Ask', text)

  /** 从服务端恢复上次的会话消息（刷新/切换后调用）。 */
  async function restore(): Promise<void> {
    let saved: string | null = null
    try {
      saved = localStorage.getItem(CONVERSATION_KEY)
    } catch {
      saved = null
    }
    if (!saved) return

    conversationId.value = saved
    try {
      const history = await conversationsApi.messages(saved)
      messages.value = history.map((m) => ({ id: String(m.id), role: m.role, content: m.content }))
    } catch {
      // 会话可能已被删除：清掉本地绑定，下次发送会新建会话。
      conversationId.value = null
      try {
        localStorage.removeItem(CONVERSATION_KEY)
      } catch {
        /* ignore */
      }
    }
  }

  async function stop(): Promise<void> {
    await connection.value?.invoke('Stop')
  }

  async function clear(): Promise<void> {
    const previous = conversationId.value
    messages.value = []
    sources.value = {}
    conversationId.value = null
    try {
      localStorage.removeItem(CONVERSATION_KEY)
    } catch {
      /* ignore */
    }
    if (previous) {
      try {
        await conversationsApi.remove(previous)
      } catch {
        /* best effort */
      }
    }
  }

  return {
    connection,
    status,
    messages,
    generating,
    conversationId,
    sources,
    connect,
    send,
    ask,
    stop,
    clear,
    restore,
  }
}
