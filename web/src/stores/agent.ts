import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import * as signalR from '@microsoft/signalr'
import { ElMessage } from 'element-plus'
import type { RetrievalSource, RunSummary, ToolStepEvent } from '@/types'
import { conversationsApi, type Conversation, type ConversationMessage } from '@/api/conversations'
import { settingsApi, type Provider } from '@/api/settings'

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  pending?: boolean
}

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected'

const CONVERSATION_KEY = 'aw.conversationId'
const DEFAULT_TITLE = '新会话'

/**
 * Agent 全局状态（单例 store）：
 * - SignalR 连接与流式消息
 * - 多会话管理（新建懒创建 / 切换 / 删除；服务端持久化）
 * - 模型供应商与当前模型（对话页与设置页共享）
 */
export const useAgentStore = defineStore('agent', () => {
  const connection = shallowRef<signalR.HubConnection | null>(null)
  const status = ref<ConnectionStatus>('disconnected')
  const messages = ref<ChatMessage[]>([])
  const generating = ref(false)
  const conversationId = ref<string | null>(null)
  const conversations = ref<Conversation[]>([])
  const sources = ref<Record<string, RetrievalSource[]>>({})
  /** 每条助手消息的工具调用时间线（执行中逐步追加）。 */
  const toolSteps = ref<Record<string, ToolStepEvent[]>>({})
  /** 每条助手消息的 run 摘要（迭代数/耗时/结束原因），Done 时才到。 */
  const runs = ref<Record<string, RunSummary>>({})
  /** 中止时的终态（cancelled / timeout / failed）；取消路径没有 run 摘要，靠它标状态。 */
  const stops = ref<Record<string, string>>({})
  const loadingConversation = ref(false)
  /** 服务端标题为默认值时，用首条用户消息派生的显示标题 */
  const derivedTitles = ref<Record<string, string>>({})
  /** 等待服务端回传 conversationId 时，随第一条消息带上标题 */
  let pendingTitle: string | null = null

  const providers = ref<Provider[]>([])
  const selectedModelId = ref<number | null>(null)

  // ---------- 会话标题 ----------

  function titleFor(c: Conversation): string {
    return derivedTitles.value[c.id] || c.title || DEFAULT_TITLE
  }

  function deriveTitleFrom(text: string): string {
    const t = text.trim().replace(/\s+/g, ' ')
    return t.length > 18 ? t.slice(0, 18) + '…' : t
  }

  // ---------- 会话列表 ----------

  async function refreshConversations(): Promise<void> {
    try {
      const list = await conversationsApi.list()
      conversations.value = list
    } catch {
      /* 侧栏列表加载失败不打扰主流程 */
    }
  }

  async function selectConversation(id: string): Promise<void> {
    if (generating.value) {
      ElMessage.warning('生成中，请先停止或等待完成')
      return
    }
    loadingConversation.value = true
    try {
      const history = await conversationsApi.messages(id)
      conversationId.value = id
      messages.value = history.map((m: ConversationMessage) => ({
        id: String(m.id),
        role: m.role,
        content: m.content,
      }))
      sources.value = {}
      toolSteps.value = {}
      runs.value = {}
      stops.value = {}
      const firstUser = messages.value.find((m) => m.role === 'user')
      if (firstUser) derivedTitles.value[id] = deriveTitleFrom(firstUser.content)
      persistConversationId(id)
    } catch {
      ElMessage.error('加载会话失败')
    } finally {
      loadingConversation.value = false
    }
  }

  function newChat(): void {
    if (generating.value) {
      ElMessage.warning('生成中，请先停止或等待完成')
      return
    }
    conversationId.value = null
    messages.value = []
    sources.value = {}
    toolSteps.value = {}
    runs.value = {}
    stops.value = {}
    persistConversationId(null)
  }

  async function deleteConversation(id: string): Promise<void> {
    if (generating.value && id === conversationId.value) {
      ElMessage.warning('生成中，请先停止或等待完成')
      return
    }
    try {
      await conversationsApi.remove(id)
      if (conversationId.value === id) newChat()
      await refreshConversations()
      ElMessage.success('已删除会话')
    } catch {
      ElMessage.error('删除失败')
    }
  }

  /** 清空 = 删除当前会话并开新的（保留旧语义）。 */
  async function clearCurrent(): Promise<void> {
    if (conversationId.value) {
      const id = conversationId.value
      conversationId.value = null
      messages.value = []
      sources.value = {}
      toolSteps.value = {}
      runs.value = {}
      stops.value = {}
      persistConversationId(null)
      try {
        await conversationsApi.remove(id)
      } catch {
        /* best effort */
      }
      await refreshConversations()
    } else {
      newChat()
    }
  }

  function persistConversationId(id: string | null): void {
    try {
      if (id) localStorage.setItem(CONVERSATION_KEY, id)
      else localStorage.removeItem(CONVERSATION_KEY)
    } catch {
      /* ignore storage errors */
    }
  }

  // ---------- 模型 ----------

  async function loadProviders(): Promise<Provider[]> {
    try {
      const list = await settingsApi.listProviders()
      providers.value = list
      resolveSelection(list)
      return list
    } catch {
      providers.value = []
      selectedModelId.value = null
      return []
    }
  }

  /** 默认选中：当前启用供应商的默认模型（无则取首个），否则清空无效选择。 */
  function resolveSelection(list: Provider[]): void {
    const active = list.find((p) => p.isActive) ?? null
    const activeModels = active?.models ?? []
    const def = activeModels.find((m) => m.isDefault) ?? activeModels[0]
    if (def) {
      selectedModelId.value = def.id
    } else if (!list.some((p) => p.models.some((m) => m.id === selectedModelId.value))) {
      selectedModelId.value = null
    }
  }

  async function switchModel(id: number): Promise<void> {
    const option = modelOptions.value.find((o) => o.modelId === id)
    if (!option) return
    const provider = providers.value.find((p) => p.id === option.providerId)
    if (!provider) return
    try {
      if (!provider.isActive) await settingsApi.activateProvider(provider.id)
      await settingsApi.setDefaultModel(provider.id, id)
      ElMessage.success(`已切换模型：${option.label}`)
    } catch (e) {
      ElMessage.error('切换失败：' + (e as Error).message)
    } finally {
      await loadProviders()
    }
  }

  /** 所有供应商下的模型拍平，供下拉分组切换（模型 id 全局唯一）。 */
  const modelOptions = computed(() =>
    providers.value.flatMap((p) =>
      p.models.map((m) => ({
        modelId: m.id,
        providerId: p.id,
        providerName: p.name,
        label: m.displayName?.trim() || m.model,
      })),
    ),
  )

  const currentModel = computed(() => {
    const o = modelOptions.value.find((x) => x.modelId === selectedModelId.value) ?? null
    return o ? { providerName: o.providerName, label: o.label } : null
  })

  // ---------- SignalR ----------

  function findOrCreate(messageId: string): ChatMessage {
    let msg = messages.value.find((m) => m.id === messageId)
    if (!msg) {
      msg = { id: messageId, role: 'assistant', content: '', pending: true }
      messages.value.push(msg)
    }
    return msg
  }

  async function connect(): Promise<void> {
    if (connection.value && status.value === 'connected') return
    if (connection.value) {
      try {
        await connection.value.stop()
      } catch {
        /* ignore */
      }
      connection.value = null
    }
    status.value = 'connecting'

    const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/agent').withAutomaticReconnect().build()

    conn.on('ReceiveConversation', (p: { conversationId: string }) => {
      if (p?.conversationId) {
        conversationId.value = p.conversationId
        persistConversationId(p.conversationId)
        if (pendingTitle) {
          derivedTitles.value[p.conversationId] = pendingTitle
          pendingTitle = null
        }
        void refreshConversations()
      }
    })

    conn.on('ReceiveDelta', (p: { messageId: string; delta: string }) => {
      findOrCreate(p.messageId).content += p.delta
    })

    conn.on('ReceiveSources', (p: { messageId: string; sources: RetrievalSource[] }) => {
      findOrCreate(p.messageId)
      sources.value[p.messageId] = p.sources
    })

    // 执行过程：每次工具调用完成推一条，按到达顺序拼成时间线（不依赖 Done，执行中就能看见）。
    conn.on('ReceiveTool', (p: { messageId: string } & ToolStepEvent) => {
      findOrCreate(p.messageId)
      const list = toolSteps.value[p.messageId] ?? (toolSteps.value[p.messageId] = [])
      list.push({
        iteration: p.iteration,
        name: p.name,
        arguments: p.arguments,
        resultPreview: p.resultPreview,
        error: p.error,
      })
    })

    conn.on('ReceiveDone', (p: { messageId: string; conversationId?: string; run?: RunSummary | null }) => {
      findOrCreate(p.messageId).pending = false
      if (p.run) runs.value[p.messageId] = p.run
      if (p?.conversationId) setConversationFromDone(p.conversationId)
      generating.value = false
      void refreshConversations()
    })

    // 中止（用户停止 / 超时 / 出错）：轨迹条要能标出终态，否则「取消」看起来像「答完了」。
    conn.on('ReceiveStopped', (p: { messageId: string; status?: string | null; error?: string | null }) => {
      findOrCreate(p.messageId)
      if (p.status) stops.value[p.messageId] = p.status
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

  function setConversationFromDone(id: string): void {
    if (conversationId.value !== id) {
      conversationId.value = id
      persistConversationId(id)
    }
  }

  async function dispatch(method: 'Send' | 'Ask', text: string): Promise<void> {
    const content = text.trim()
    if (!content || generating.value) return

    await connect()

    messages.value.push({ id: crypto.randomUUID(), role: 'user', content })
    generating.value = true
    if (!conversationId.value) pendingTitle = deriveTitleFrom(content)

    try {
      await connection.value!.invoke(method, {
        conversationId: conversationId.value,
        text: content,
        history: [],
      })
    } catch (e) {
      generating.value = false
      pendingTitle = null
      messages.value.push({ id: crypto.randomUUID(), role: 'assistant', content: '⚠️ ' + (e as Error).message })
    }
  }

  const send = (text: string) => dispatch('Send', text)
  const ask = (text: string) => dispatch('Ask', text)

  async function stop(): Promise<void> {
    try {
      await connection.value?.invoke('Stop')
    } catch {
      /* 连接可能已断开 */
    }
  }

  // ---------- 初始化 ----------

  /** 刷新/重新打开页面后恢复：会话列表 + 模型列表 + 上次的会话。 */
  async function restore(): Promise<void> {
    await Promise.all([refreshConversations(), loadProviders()])
    let saved: string | null = null
    try {
      saved = localStorage.getItem(CONVERSATION_KEY)
    } catch {
      saved = null
    }
    if (saved && conversations.value.some((c) => c.id === saved)) {
      await selectConversation(saved)
    } else {
      persistConversationId(null)
      newChat()
    }
  }

  return {
    // 连接与消息
    connection,
    status,
    messages,
    generating,
    sources,
    toolSteps,
    runs,
    stops,
    conversationId,
    connect,
    send,
    ask,
    stop,
    // 会话
    conversations,
    loadingConversation,
    titleFor,
    refreshConversations,
    selectConversation,
    newChat,
    deleteConversation,
    clearCurrent,
    restore,
    // 模型
    providers,
    modelOptions,
    selectedModelId,
    currentModel,
    loadProviders,
    switchModel,
  }
})
