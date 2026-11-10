// 端到端探针：连 /hubs/agent，发一句会触发工具调用的消息，打印 Hub → 浏览器的全部事件。
// 目的：验证 ReceiveTool / ReceiveDone.run 这些新payload真的到达客户端，而不是只编译通过。
import { createRequire } from 'module'
import { fileURLToPath } from 'url'
// 复用前端已装的 @microsoft/signalr，不额外引依赖
const require = createRequire(fileURLToPath(new URL('../web/package.json', import.meta.url)))
const signalR = require('@microsoft/signalr')

const text = process.argv[2] ?? '现在几点了？请调用工具查一下当前时间。'
const hubUrl = process.env.HUB_URL ?? 'http://127.0.0.1:8080/hubs/agent'

// Hub 两道门都得先过，缺一样就 100% 连不上：
//   1) 握手层要 aw.auth cookie（FallbackPolicy 默认拒 + Hub 内 IsAuthenticated 判空）；
//   2) Send 的 ChatRequest.ClientMessageId 必填，缺了 Hub 直接拒、不起 run。
// 这两条以前都没有，脚本等于从来没跑通过 —— 所以它证明不了任何事。
const apiBase = process.env.API_BASE ?? hubUrl.replace(/\/hubs\/.*$/, '')
const userName = process.env.AW_USER ?? 'wang'
const password = process.env.AW_PASSWORD ?? 'Demo1234'
const loginRes = await fetch(`${apiBase}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ userName, password }),
})
if (!loginRes.ok) {
  console.error('[Auth] login failed', loginRes.status, await loginRes.text())
  process.exit(1)
}
const cookie = (loginRes.headers.getSetCookie?.() ?? [])
  .map((c) => c.split(';')[0])
  .join('; ')
if (!cookie) {
  console.error('[Auth] 登录成功但没拿到 Set-Cookie')
  process.exit(1)
}
// 本次输入的标识：一次输入一个值，后端用它派生动作请求号。
const clientMessageId = process.env.AW_CLIENT_MESSAGE_ID ?? crypto.randomUUID()
console.log('[Auth]', userName, '│ clientMessageId =', clientMessageId)

const conn = new signalR.HubConnectionBuilder()
  .withUrl(hubUrl, { headers: { Cookie: cookie } })
  .configureLogging(signalR.LogLevel.Warning)
  .build()

const tools = []
const sources = []
let answer = ''
let done = null
let error = null
let stopStatus = null

conn.on('ReceiveConversation', (p) => console.log('[Conversation]', p.conversationId))
conn.on('ReceiveDelta', (p) => {
  answer += p.delta
})
conn.on('ReceiveTool', (p) => {
  tools.push(p)
  console.log('[Tool]', `iter=${p.iteration}`, p.name, '│ 入参:', p.arguments ?? '-', '│ 返回:', (p.resultPreview ?? '').slice(0, 120))
})
conn.on('ReceiveSources', (p) => {
  sources.push(...p.sources)
  console.log('[Sources]', p.sources.length, '条', p.sources.map((s) => s.fileName).join(', '))
})
// 前端 agent.ts 注册了 9 个事件，这里必须一一对齐。
// 漏注册不会报错，SignalR 只会打一条 "No client method ... found" 的警告，
// 很容易被当成服务端 bug —— 这恰恰是本脚本以前误导过一次的原因。
conn.on('ReceiveCitations', (p) => console.log('[Citations]', JSON.stringify(p).slice(0, 200)))
conn.on('ReceiveRefusal', (p) => console.log('[Refusal]', JSON.stringify(p).slice(0, 200)))
conn.on('ReceiveDone', (p) => {
  done = p
  console.log('[Done]', JSON.stringify(p))
})
conn.on('Error', (p) => {
  error = p.message
  console.log('[Error]', p.message)
})
conn.on('ReceiveStopped', (p) => {
  stopStatus = p.status
  console.log('[Stopped]', p.status, '│', p.error)
})

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

await conn.start()
const method = process.env.HUB_METHOD ?? 'Send'
console.log(`connected; invoking ${method}:`, text)
if (process.env.CANCEL === '1') {
  // 取消路径：不 await（服务端方法要跑完整条 run 才返回），中途 Stop。
  conn.invoke(method, { conversationId: null, text, history: [], clientMessageId }).catch(() => {})
  await new Promise((r) => setTimeout(r, Number(process.env.CANCEL_AFTER_MS ?? 2500)))
  console.log('-> Stop()')
  await conn.invoke('Stop')
} else {
  await conn.invoke(method, { conversationId: null, text, history: [], clientMessageId })
}

const deadline = Date.now() + 120_000
while (!done && !error && Date.now() < deadline) await sleep(300)

console.log('\n===== 结论 =====')
console.log('answer:', answer.trim().slice(0, 200) || '(空)')
console.log('tool events:', tools.length, '│ source events:', sources.length)
console.log('done:', done ? JSON.stringify(done.run) : null)
console.log('stopped:', stopStatus)
console.log('error:', error)
await conn.stop()
process.exit(done?.run || stopStatus ? 0 : 1)
