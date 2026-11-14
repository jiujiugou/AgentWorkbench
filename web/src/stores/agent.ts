import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import * as signalR from '@microsoft/signalr'
import { ElMessage } from 'element-plus'
import type { CitationReport, RefusalInfo, RetrievalSource, RunSummary, ToolStepEvent } from '@/types'
import { historyReport, historySources } from '@/api/knowledge'
import {
  conversationsApi,
  toRunSummary,
  toToolSteps,
  type Conversation,
  type ConversationKind,
} from '@/api/conversations'
import { settingsApi, type Provider } from '@/api/settings'

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  pending?: boolean
  /**
   * 历史消息带回来的那次运行终态。只读：它来自 `agent_runs`，不在前端重算。
   * `running` + 空正文 = 那一轮没跑完（进程可能已中断），与“跑完但空”分开显示。
   */
  runStatus?: string | null
  /**
   * 这条回答有没有引用企业资料。**是跑完之后的事实，不是用户选的模式**：
   * 全程没有来源、也不是拒答，才标 general，界面据此显示「直答 · 未使用企业资料」。
   */
  mode?: ConversationKind
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
  /** 拒答：文案与归类码由服务端给，前端只负责把它渲染成"系统的结论"而不是模型的话。 */
  const refusals = ref<Record<string, RefusalInfo>>({})
  /** 每条助手消息的引用体检报告（角标 / 底纹 / cleanedAnswer 的唯一来源），Done 前后到都行。 */
  const citations = ref<Record<string, CitationReport>>({})
  /** 每条助手消息的工具调用时间线（执行中逐步追加）。 */
  const toolSteps = ref<Record<string, ToolStepEvent[]>>({})
  /** 每条助手消息的 run 摘要（迭代数/耗时/结束原因），Done 时才到。 */
  const runs = ref<Record<string, RunSummary>>({})
  /** 中止时的终态（cancelled / timeout / failed）；取消路径没有 run 摘要，靠它标状态。 */
  const stops = ref<Record<string, string>>({})
  const loadingConversation = ref(false)
  /**
   * 新建对话弹窗带过来的第一句。**只在这里过一手，不在弹窗里直接发**：
   * 发送要落在 ChatView（它管滚动、自动增高、generating 态），弹窗只管收集意图。
   * ChatView 读走之后必须清空——不清的话，从别的页面回到对话页会再发一次。
   */
  const pendingQuestion = ref('')

  const providers = ref<Provider[]>([])
  const selectedModelId = ref<number | null>(null)

  // ---------- 会话标题 ----------

  /**
   * 标题**只从库里来**。
   *
   * 以前这里是 `derivedTitles[id] || c.title`：前端拿首条消息自己截前 18 字，
   * 再盖在服务端标题上。两份恰好一样时看不出问题，一旦规则改动（比如改成 20 字），
   * 两台设备会因为各自的会话加载顺序不同而显示不同的名字——而库里那份是对的。
   * 标题现在由 `derive_title` 在服务端写定一次（C-V9），前端不再派生。
   */
  function titleFor(c: Conversation): string {
    return c.title || DEFAULT_TITLE
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
      const rows: ChatMessage[] = history.map((m) => ({
        id: String(m.id),
        role: m.role,
        content: m.content,
        runStatus: m.runStatus,
      }))
      messages.value = rows

      // 逐条恢复"答案 + 依据 + 过程"三块，而不是只恢复正文。
      //
      // 以前的写法是读完正文就把 sources / citations / toolSteps 全清空：
      // 界面上历史回答变成"一段没有出处的文字"，用户会以为引用没存下来。
      // 现在这三块都从库里读回来——引用读的是**当时的快照**，不回表查 chunks。
      const nextSources: Record<string, RetrievalSource[]> = {}
      const nextCitations: Record<string, CitationReport> = {}
      const nextRefusals: Record<string, RefusalInfo> = {}
      for (const m of history) {
        if (m.role !== 'assistant') continue
        const key = String(m.id)
        // C# 的 /messages 只有正文，没有 citations 快照：没有就不重建
        const cards = m.citations?.length ? historySources(m) : []
        if (cards.length) nextSources[key] = cards
        const report = m.citations?.length ? historyReport(m) : null
        if (report) nextCitations[key] = report

        // 「未使用企业资料」是事后结论，不是用户选项：库里留着引用快照才算有依据。
        // 拒答不打这个标——它已经在说「资料不足」，再叠一句会变成两个相反的解释。
        const row = rows.find((r) => r.id === key)
        if (row && m.runStatus !== 'refused') {
          row.mode = cards.length ? 'knowledge' : 'general'
        }

        // 拒答要在重开之后**还是拒答**（C-V13）。
        //
        // 判据用的是服务端带回来的 `runStatus`——那次运行的终态，写一次就不变。
        // 刻意不做的两件事：拿正文和那句拒答文案比（改文案那天全部历史会一起
        // 变成普通回答，而没有任何东西会红）；拿当前阈值重新判一遍（等于让
        // "当时"随参数改口）。正文直接用库里那份，一字不改。
        if (m.runStatus === 'refused') {
          nextRefusals[key] = { text: m.content, code: m.runReason }
        }
      }

      sources.value = nextSources
      citations.value = nextCitations
      refusals.value = nextRefusals
      // 过程要按 run 一条条取。先清空：切会话时旧消息的 key 还在，不清就会
      // 让新会话的某条消息意外继承另一条的轨迹（key 都是数字 id，很容易撞）。
      toolSteps.value = {}
      runs.value = {}
      stops.value = {}

      persistConversationId(id)

      // 轨迹逐条拉，**不阻塞正文与引用的显示**：它们已经在上面设好了，
      // 过程那一块晚几百毫秒到不影响阅读。
      await Promise.all(
        history
          .filter((m) => m.role === 'assistant' && m.runId)
          .map((m) => loadTrace(String(m.id), String(m.runId))),
      )
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
    refusals.value = {}
    citations.value = {}
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
      refusals.value = {}
      citations.value = {}
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

  /**
   * 建 SignalR 连接。C# 后端靠 `/hubs/agent` 逐字推流，
   * ChatView 挂载时调一次；下面全部 ReceiveXxx 处理器与 Hub 事件一一对应。
   */
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

    // 引用体检：句级统计 + 逐条校验结论 + 剥掉越界标注后的正文。
    // 比正文流晚到（run 结束才算）——角标会在答案渲染完之后补挂，闪一下属预期。
    conn.on('ReceiveCitations', (p: { messageId: string } & CitationReport) => {
      findOrCreate(p.messageId)
      citations.value[p.messageId] = {
        sentenceCount: p.sentenceCount,
        citedSentenceCount: p.citedSentenceCount,
        uncitedSentenceCount: p.uncitedSentenceCount,
        outOfRangeCount: p.outOfRangeCount,
        unsupportedCount: p.unsupportedCount,
        uncitedRatio: p.uncitedRatio,
        supportVerdict: p.supportVerdict,
        cleanedAnswer: p.cleanedAnswer,
        citations: p.citations,
      }
    })

    // 拒答：服务端在工具返回后判定"资料不足"，直接终止生成并给出固定文案。
    // 这里要把正文替换掉——模型可能先吐过一两句（比如"我先查一下"），拒答之后那些话不该留在界面上；
    // 同时清空来源：判定不足时那五块仍是"检索命中的内容"，但用它当出处等于给假担保。
    conn.on('ReceiveRefusal', (p: { messageId: string; text: string; code?: string | null }) => {
      const message = findOrCreate(p.messageId)
      message.content = p.text
      refusals.value[p.messageId] = { text: p.text, code: p.code ?? null }
      delete sources.value[p.messageId]
      delete citations.value[p.messageId]
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
      const msg = findOrCreate(p.messageId)
      msg.pending = false
      // 收尾时才判「这条回答有没有企业资料依据」：跑完仍没有来源、也不是拒答，
      // 就是直答。放在这里而不是发送时，是因为发送时还不知道模型会不会检索。
      if (!refusals.value[p.messageId] && !sources.value[p.messageId]?.length) msg.mode = 'general'
      else if (sources.value[p.messageId]?.length) msg.mode = 'knowledge'
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

  /**
   * 取一次运行的轨迹，填进"过程"那一块。
   *
   * 只在**回看历史**时走这条路：正在跑的 run 靠 `ReceiveTool` 增量攒过程，
   * 已结束的 run 只能从 `GET /api/harness/runs/{id}` 读回（与实时事件同源，都是 `agent_steps`）。
   *
   * 用展开赋值而不是就地改对象：`toolSteps` 是 `ref`，Vue 只追踪通过 `.value`
   * 赋进去的那个代理。在异步回调里改 `nextSteps[key]` 改的是**原始对象**，
   * 界面不会更新——而它不会报错，只是永远显示"没有过程"。
   *
   * 失败静默：答案与引用已经在手，缺的只是过程。为它弹一个错，
   * 会让人以为这次回答本身出了问题。
   */
  async function loadTrace(messageId: string, runId: string): Promise<void> {
    try {
      const trace = await conversationsApi.runDetail(runId)
      toolSteps.value = { ...toolSteps.value, [messageId]: toToolSteps(trace) }
      runs.value = { ...runs.value, [messageId]: toRunSummary(trace) }
    } catch {
      /* 过程取不到不影响答案 */
    }
  }

  /**
   * 发送。唯一入口，走 SignalR `/hubs/agent` 的 `Send`。
   *
   * 曾经这里有 `Send` / `Ask` 两条：用户自己选「通用对话」还是「知识库问答」。
   * 两者走的是同一个工具环、同一个模型、同一条流，唯一差别是一句系统提示——
   * 用户以为选的是通道，实际只选了措辞。开关已删，语言里说什么由系统判断。
   * 接收侧（connect 里的全部 ReceiveXxx）一行不改。
   */
  async function dispatch(text: string): Promise<void> {
    const content = text.trim()
    if (!content || generating.value) return

    await connect()
    messages.value.push({ id: crypto.randomUUID(), role: 'user', content })
    generating.value = true

    try {
      await connection.value!.invoke('Send', {
        conversationId: conversationId.value,
        text: content,
        // 历史不随请求传：由服务端 ChatHistoryProvider 从会话存储恢复。
        // 本次输入的唯一标识：每次点击发送生成新值，后端用它派生动作请求号。
        // 当前没有自动重发；若引入断线补发，复用同一次输入的这个值只是必要条件之一——
        // 服务端还必须先有输入级去重（action_idempotency 只护动作、不护整段 run），
        // 且要把 clientMessageId 带进终态事件，否则界面无法确认哪次输入已送达。
        // 缺失时后端直接拒绝（client_message_id_required）。
        clientMessageId: crypto.randomUUID(),
      })
    } catch (e) {
      generating.value = false
      messages.value.push({
        id: crypto.randomUUID(),
        role: 'assistant',
        content: '⚠️ ' + (e as Error).message,
      })
    }
  }

  const send = (text: string) => dispatch(text)

  /** 停止走 Hub 的 Stop；Python 侧那条 POST /api/runs/cancel 在 C# 后端不存在。 */
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
    refusals,
    citations,
    toolSteps,
    runs,
    stops,
    conversationId,
    connect,
    send,
    stop,
    // 会话
    conversations,
    loadingConversation,
    pendingQuestion,
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
