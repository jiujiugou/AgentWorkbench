#!/usr/bin/env node
/**
 * 一条命令跑完 S01–S10，并打印十格对账。
 *
 *   node scripts/e2e-run.mjs                       # 默认指令，自动批准
 *   node scripts/e2e-run.mjs --text "..."          # 换指令
 *   node scripts/e2e-run.mjs --no-approve          # 停在待审批，不动外部系统
 *   node scripts/e2e-run.mjs --api http://host:8080
 *
 * 为什么必须有它：S01–S10 是一条链，但过去每一格的证据都是分开取的——
 * 探针验 Hub、curl 验审批、SQL 验落库。分开取的问题不是慢，是**没人证明它们串得上**。
 *
 * 两条设计原则，比脚本本身重要：
 *
 * 1. **不自己起服务。** 后端是长驻进程，交给脚本托管就会在会话结束时被杀掉，
 *    留下「跑了一半」的错觉。服务没起时它只打印启动命令然后退出。
 *
 * 2. **区分「没走通」和「这次没走到」。** 模型这次没调 propose_action，
 *    S05–S08 就是没发生，不等于坏。全绿和「十格里有四格根本没走到」
 *    必须能被一眼分开——否则这个脚本会变成新的假绿灯来源。
 *
 * 出口码：0 = 全部走到且通过；1 = 有格失败；2 = 有格未走到但没有失败。
 */
import { createRequire } from 'module'
import { fileURLToPath } from 'url'

// 复用前端已装的 @microsoft/signalr，不额外引依赖。
const require = createRequire(fileURLToPath(new URL('../web/package.json', import.meta.url)))
const signalR = require('@microsoft/signalr')

// ── 参数 ──────────────────────────────────────────────────────────
const argv = process.argv.slice(2)
function flag(name) {
  return argv.includes(`--${name}`)
}
function opt(name, fallback) {
  const i = argv.indexOf(`--${name}`)
  return i >= 0 && argv[i + 1] ? argv[i + 1] : fallback
}

const API = (opt('api', process.env.API_BASE ?? 'http://127.0.0.1:8080')).replace(/\/$/, '')
const USER = opt('user', process.env.AW_USER ?? 'wang')
const PASSWORD = opt('password', process.env.AW_PASSWORD ?? 'Demo1234')
const AUTO_APPROVE = !flag('no-approve')
const TIMEOUT_MS = Number(opt('timeout', '180')) * 1000

/**
 * 默认指令针对**当前 Gitea 动作域**：`issue.create` 的标题前缀 `[AW]` 是该域的业务规则，
 * 不带就会被第 3 道闸门 deny。换域时必须一起改这里（或用 --text 覆盖）。
 *
 * 故意不用 `issue.query`：它现在有个已知的参数类型缺陷（模型把 argumentsJson 传成对象），
 * 那属于 S09 的待办，不该混进「整条链是否跑通」的判断里。
 */
const DEFAULT_TEXT = [
  '请严格按顺序做，每一步都必须真的调用工具完成，不要凭记忆或推测回答：',
  '1. 调用 get_current_time 查当前时间；',
  '2. 调用 calculator 计算 (128+72)*3；',
  '3. 用 propose_action 提交 actionKey=repo.query、argumentsJson={} ，查默认仓库信息；',
  '4. 用 propose_action 提交 actionKey=issue.create，argumentsJson 里 title 必须以 [AW] 开头',
  '   （例如 "[AW] e2e 验证"），body 写 "来自 AgentWorkbench 的端到端验收"，创建一条议题。',
  '最后用一句话汇总你实际调用了哪几个工具、各自拿到什么关键信息。',
].join('\n')

const TEXT = opt('text', DEFAULT_TEXT)

// ── 小工具 ────────────────────────────────────────────────────────
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const hr = (t) => console.log(`\n${'─'.repeat(4)} ${t} ${'─'.repeat(Math.max(0, 60 - t.length))}`)

let cookie = ''
async function api(path, init = {}) {
  const res = await fetch(`${API}${path}`, {
    ...init,
    headers: { Cookie: cookie, 'Content-Type': 'application/json', ...(init.headers ?? {}) },
  })
  const text = await res.text()
  let body = null
  try {
    body = text ? JSON.parse(text) : null
  } catch {
    body = text
  }
  return { status: res.status, ok: res.ok, body }
}

// ── 对账表 ────────────────────────────────────────────────────────
// pass = 走到且成立；fail = 走了但不成立；skip = 这次根本没发生。
const checks = []
function record(key, title, state, detail) {
  checks.push({ key, title, state, detail })
  const mark = state === 'pass' ? '✓' : state === 'fail' ? '✗' : '—'
  console.log(`  ${mark} ${key} ${title}${detail ? `：${detail}` : ''}`)
}

// ── 主流程 ────────────────────────────────────────────────────────
async function main() {
  hr('0 前置：服务是否活着')
  try {
    const ping = await fetch(`${API}/api/auth/me`)
    if (ping.status >= 500) throw new Error(`HTTP ${ping.status}`)
    console.log(`  服务在线（${API}，/api/auth/me → ${ping.status}）`)
  } catch (e) {
    console.error(`  连不上 ${API}：${e.message}`)
    console.error('\n  先在你自己的终端启动后端（脚本不托管长驻进程）：')
    console.error('    cd src/Host/AgentWorkbench.Webapi && dotnet run --urls http://localhost:8080')
    console.error('  若跑真实外部动作，另需 Actions__ActionSet / Actions__Gitea__* 等环境变量。')
    process.exit(1)
  }

  hr('0 登录')
  const login = await fetch(`${API}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userName: USER, password: PASSWORD }),
  })
  if (!login.ok) {
    console.error(`  登录失败 ${login.status}：${await login.text()}`)
    process.exit(1)
  }
  cookie = (login.headers.getSetCookie?.() ?? []).map((c) => c.split(';')[0]).join('; ')
  if (!cookie) {
    console.error('  登录成功但没拿到 Set-Cookie')
    process.exit(1)
  }
  const me = (await api('/api/auth/me')).body
  console.log(`  ${me?.displayName ?? USER}（role=${me?.role}）`)

  hr('0 能力位与基线')
  const health = (await api('/api/harness/health')).body
  if (!health?.llm) {
    console.error('  模型未配置（capabilities.llm=false）：跑不了真模型，S04 起全程无从判断。')
    console.error('  在「设置 → 模型」里启用一个 provider，或确认库里的 providers 有 is_active=1 的行。')
    process.exit(1)
  }
  console.log(`  llm=${health.llm} loop=${health.loop} tools=${(health.tools ?? []).length} 个`)

  // 基线：本次之前已存在的动作单。用来把「这次产生的」和「历史遗留」分开，
  // 否则库里已有的 pending 会被误算成本次的成果。
  const beforeActions = (await api('/api/actions?limit=500')).body ?? []
  const beforeIds = new Set(beforeActions.map((a) => a.requestId))
  const beforePending = (await api('/api/actions/pending')).body ?? []
  const beforePendingIds = new Set(beforePending.map((p) => p.requestId))
  console.log(`  基线：历史动作 ${beforeActions.length} 条，其中待审批 ${beforePending.length} 条`)

  // ── S01–S04：经 Hub 发一条输入 ──────────────────────────────
  hr('S01–S04 经 SignalR Hub 发一条输入')
  const clientMessageId = crypto.randomUUID()
  console.log(`  clientMessageId = ${clientMessageId}`)

  const tools = []
  let answer = ''
  let done = null
  let failure = null
  let conversationId = null

  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${API}/hubs/agent`, { headers: { Cookie: cookie } })
    .configureLogging(signalR.LogLevel.Error)
    .build()

  // 前端 agent.ts 注册了 9 个事件，这里必须一一对齐：
  // 漏一个不会报错，只会打一条 "No client method ... found" 的警告，
  // 那条警告很容易被误读成服务端 bug（本仓已经栽过一次）。
  conn.on('ReceiveConversation', (p) => {
    conversationId = p.conversationId
    console.log(`  [会话] ${p.conversationId}`)
  })
  conn.on('ReceiveDelta', (p) => {
    answer += p.delta
  })
  conn.on('ReceiveTool', (p) => {
    tools.push(p)
    console.log(`  [工具] iter=${p.iteration} ${p.name} │ 入参 ${String(p.arguments ?? '-').slice(0, 70)}`)
  })
  conn.on('ReceiveSources', (p) => console.log(`  [来源] ${p.sources?.length ?? 0} 条`))
  conn.on('ReceiveCitations', () => {})
  conn.on('ReceiveRefusal', (p) => console.log(`  [拒答] ${JSON.stringify(p).slice(0, 120)}`))
  conn.on('ReceiveDone', (p) => {
    done = p
  })
  conn.on('Error', (p) => {
    failure = p.message
    console.log(`  [错误] ${p.message}`)
  })
  conn.on('ReceiveStopped', (p) => console.log(`  [停止] ${p.status}`))

  await conn.start()
  const t0 = Date.now()
  // Hub 的 Send 跑完整条 run 才返回；超时兜底避免模型卡死时脚本一直挂着。
  await Promise.race([
    conn.invoke('Send', { conversationId: null, text: TEXT, clientMessageId }),
    sleep(TIMEOUT_MS).then(() => {
      throw new Error(`超过 ${TIMEOUT_MS / 1000}s 未返回`)
    }),
  ])
  const elapsed = Date.now() - t0
  // Send 返回后事件可能还在管道里，给它一点时间落地。
  for (let i = 0; i < 20 && !done && !failure; i++) await sleep(150)
  await conn.stop()

  const run = done?.run ?? null
  console.log(`  ${elapsed} ms，answer: ${answer.trim().slice(0, 120) || '(空)'}`)

  record('S01', '输入一句话', failure ? 'fail' : 'pass', failure ?? `clientMessageId 被接受，未触发 client_message_id_required`)
  record('S02', '验身份取会话', conversationId ? 'pass' : 'fail', conversationId ? `session=${conversationId.slice(0, 12)}… subject=${me?.displayName ?? USER}` : '没拿到会话 id')

  // S03/S04 不能拿「有没有调工具」当判据：契约 S04 明写「回纯文本时跳过 S05–S09」，
  // 所以模型选择不调工具是**合法的决策**，不是没走到。有 ReceiveDone 就说明这次模型调用真的发生过。
  record('S03', '组上下文', done || tools.length ? 'pass' : 'fail',
    tools.length
      ? `能力清单已注入，本轮工具调用 ${tools.length} 次`
      : `能力清单 ${(health.tools ?? []).length} 个（健康检查可见）；本轮无工具调用，注入无法从外部单独确认`)
  record('S04', '模型决策', done ? 'pass' : 'fail',
    tools.length
      ? `发了 ${tools.length} 次工具调用`
      : '选择回纯文本（契约 S04：此时跳过 S05–S09，直接进 S10）')

  // ── S05–S07：这次新出现的动作单 ─────────────────────────────
  hr('S05–S07 填单、四道闸门、审批')
  await sleep(400)
  const afterActions = (await api('/api/actions?limit=500')).body ?? []
  const newActions = afterActions.filter((a) => !beforeIds.has(a.requestId))
  const pendingNow = (await api('/api/actions/pending')).body ?? []
  const newPending = pendingNow.filter((p) => !beforePendingIds.has(p.requestId))

  if (!newActions.length && !newPending.length) {
    record('S05', 'propose_action 填单', 'skip', '模型没调 propose_action，本轮没有动作单')
    record('S06', '四道闸门', 'skip', '无动作单')
    record('S07', '审批', 'skip', '无动作单')
  } else {
    const allNew = [...newActions]
    for (const p of newPending) {
      if (!allNew.some((a) => a.requestId === p.requestId)) {
        allNew.push({ ...p, status: null, code: null, externalId: null })
      }
    }
    console.log(`  本轮新增 ${allNew.length} 单：`)
    for (const a of allNew) console.log(`    ${a.actionKey}  ${a.requestId.slice(0, 12)}…  status=${a.status ?? 'pending'}`)

    record('S05', 'propose_action 填单', 'pass', `${allNew.length} 个 requestId：${allNew.map((a) => a.actionKey).join(', ')}`)

    // 四道闸门：逐单核 trace 里有没有这四道，且都没 deny。
    let gateOk = 0
    let gateBad = []
    for (const a of allNew) {
      const trail = (await api(`/api/actions/${encodeURIComponent(a.requestId)}/audit`)).body ?? []
      const gates = ['schema', 'permission', 'business_rule', 'audit'].map((s) => trail.find((e) => e.stage === s))
      const denied = gates.find((g) => g?.outcome === 'deny')
      const missing = gates.filter((g) => !g)
      console.log(`    ${a.actionKey}: ${gates.map((g) => `${g?.stage ?? '?'}=${g?.outcome ?? '缺'}`).join(' ')}`)
      if (denied) gateBad.push(`${a.actionKey} 第 ${denied.stage} 道 deny：${denied.result}`)
      else if (missing.length) gateBad.push(`${a.actionKey} 缺 ${missing.length} 道闸门记录`)
      else gateOk++
    }
    record('S06', '四道闸门', gateBad.length ? 'fail' : 'pass',
      gateBad.length ? gateBad.join('；') : `${gateOk} 单全部四道 allow 且逐道落痕`)

    // S07：有新增待办 = 挂起了；没有待办但动作进了终态 = 直接放行或者已批过。
    if (newPending.length) {
      console.log(`  ${newPending.length} 单挂起等审批：`)
      for (const p of newPending) console.log(`    ${p.actionKey}  ${p.requestId.slice(0, 12)}…  ${p.actionSet}  提交人 ${p.subject}`)
      if (AUTO_APPROVE) {
        console.log('  --approve（默认）：由脚本代为批准。产品路径上这一步必须人手点，脚本只是验收通道。')
        for (const p of newPending) {
          const r = await api(`/api/actions/${encodeURIComponent(p.requestId)}/approve`, { method: 'POST' })
          console.log(`    批准 ${p.actionKey} → HTTP ${r.status} ${r.body?.status ?? ''} ${r.body?.code ?? ''} ${r.body?.externalId ? `ext=${r.body.externalId}` : ''}`)
        }
        await sleep(600)
        record('S07', '审批挂起→批准', 'pass', `${newPending.length} 单曾挂起 pending，随后批准并执行`)
      } else {
        record('S07', '审批挂起', 'pass', `${newPending.length} 单停在 pending（--no-approve，未批准）`)
      }
    } else {
      record('S07', '审批', 'skip', '本轮没有新增待办：单子可能在提交时就进了终态，或该动作不需要审批')
    }
  }

  // ── S08–S10：外部结果与落库 ────────────────────────────────
  hr('S08–S10 执行、工具环、落库')
  const finalActions = (await api('/api/actions?limit=500')).body ?? []
  const finalNew = finalActions.filter((a) => !beforeIds.has(a.requestId))

  const executed = finalNew.filter((a) => a.externalId)
  if (finalNew.length) {
    const bad = finalNew.filter((a) => a.status === 'failed' || a.status === 'rejected')
    const unknown = finalNew.filter((a) => a.status === 'timeout' || a.status === 'unknown')
    const pendingLeft = finalNew.filter((a) => !a.status)
    console.log('  本轮动作终态：')
    for (const a of finalNew) {
      console.log(`    ${a.actionKey.padEnd(14)} status=${String(a.status ?? 'pending').padEnd(10)} code=${String(a.code ?? '-').padEnd(24)} ext=${a.externalId ?? '-'}`)
    }
    if (bad.length) record('S08', '执行', 'fail', bad.map((b) => `${b.actionKey} ${b.status}/${b.code}`).join('；'))
    else if (unknown.length) record('S08', '执行', 'fail', `出现不确定态：${unknown.map((u) => u.actionKey).join(', ')}（系统只查证不重放，需人工核对）`)
    else if (pendingLeft.length) record('S08', '执行', 'skip', `${pendingLeft.length} 单仍未收口（待审批）`)
    else if (executed.length) record('S08', '执行', 'pass', `${executed.length} 单带外部单号（ext=${executed.map((e) => e.externalId).join(', ')}）`)
    else record('S08', '执行', 'skip', '没有产生外部单号（可能只有读动作）')
  } else {
    record('S08', '执行', 'skip', '无动作单')
  }

  record('S09', '工具环', run ? 'pass' : 'fail',
    run ? `iterations=${run.iterations}，status=${run.status}${run.code ? `，code=${run.code}` : ''}` : '没收到 ReceiveDone')

  // S10：轨迹条数 + 三笔落库
  let traceTotal = 0
  for (const a of finalNew) {
    const trail = (await api(`/api/actions/${encodeURIComponent(a.requestId)}/audit`)).body ?? []
    traceTotal += trail.length
  }
  record('S10', '流式输出与落库', done ? 'pass' : 'fail',
    done ? `run ${run?.runId} 落库；本轮动作轨迹 ${traceTotal} 条` : '没有终态事件')

  // ── 汇总 ──────────────────────────────────────────────────
  hr('十格对账')
  const pass = checks.filter((c) => c.state === 'pass').length
  const fail = checks.filter((c) => c.state === 'fail').length
  const skip = checks.filter((c) => c.state === 'skip').length
  for (const c of checks) {
    const mark = c.state === 'pass' ? '✓' : c.state === 'fail' ? '✗' : '—'
    console.log(`  ${mark} ${c.key} ${c.title.padEnd(14, ' ')} ${c.state === 'pass' ? '' : c.state === 'fail' ? '失败：' : '未走到：'}${c.detail}`)
  }
  console.log(`\n  ${pass} 通过 / ${fail} 失败 / ${skip} 未走到`)

  if (fail) {
    console.log('\n  有失败格：这不是模型的问题就先查，模型的问题就先换大模型再看一次。')
    return 1
  }
  if (skip) {
    console.log('\n  没有失败，但有格这次没走到——这几格今天的证据是空的，不能算通过。')
    return 2
  }
  console.log('\n  S01–S10 全部走到且通过。外部副作用已发生，按上面打印的单号自行核对或收尾。')
  return 0
}

main()
  .then((code) => process.exit(code))
  .catch((e) => {
    console.error('\n脚本异常：', e?.message ?? e)
    process.exit(1)
  })
