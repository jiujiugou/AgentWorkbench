<script setup lang="ts">
/**
 * 一张动作单的完整视图。承载界面契约的四格：
 *
 * - **S05** 这一单是什么：动作名、结构化参数、提交时间、来源
 * - **S06** 四道闸门逐道结论与理由（不许合成一个总状态）
 * - **S08** 外部系统里变成了什么：单号、**回放标记**、**不确定态**
 * - **S10** 终态 + 归类码，与正文分开；轨迹可回放
 *
 * 数据只来自两个只读端点：`GET /api/actions/{id}`（终态）与 `/{id}/audit`（轨迹）。
 * 界面不推断状态、不重算幂等——显示什么完全由服务端给。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, RefreshRight } from '@element-plus/icons-vue'
import { actionsApi, type ActionResultDto, type TraceEvent } from '@/api/actions'

const route = useRoute()
const router = useRouter()

const requestId = computed(() => String(route.params.requestId ?? ''))
const trail = ref<TraceEvent[]>([])
const result = ref<ActionResultDto | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/** 四道闸门的顺序固定：Schema → Permission → BusinessRule → Audit（契约 S06）。 */
const GATES = ['schema', 'permission', 'business_rule', 'audit'] as const

const GATE_LABELS: Record<string, string> = {
  schema: '结构',
  permission: '权限',
  business_rule: '业务规则',
  audit: '审计',
}

/** 一道闸门可能落多条（审计那道既有判定也有写入），取第一条判定为准，全部留在时间线里。 */
const gates = computed(() =>
  GATES.map((stage) => ({
    stage,
    label: GATE_LABELS[stage],
    event: trail.value.find((e) => e.stage === stage) ?? null,
  })),
)

/**
 * 审批结论取**最后一条**，不是第一条。
 * 轨迹里 approval 会落多条：提交时的 `pending`、每次人工决定的 `allow`/`deny`。
 * 取第一条会把「已批准并执行成功」的单显示成「待审批」——实测库里就有这种 4 条记录的样本。
 * 全部记录仍保留在下方完整轨迹里，这里只给最终结论。
 */
const approvalAll = computed(() => trail.value.filter((e) => e.stage === 'approval'))
const approval = computed(() =>
  approvalAll.value.length ? approvalAll.value[approvalAll.value.length - 1] : null,
)
const execution = computed(() => trail.value.filter((e) => e.stage === 'execute'))

/** 动作名取轨迹里的 intent：终态查不到时（如 404）仍能显示这是什么动作。 */
const actionKey = computed(() => trail.value[0]?.intent ?? '—')
const argumentsText = computed(() => trail.value[0]?.arguments ?? '')

/**
 * 状态呈现。三条规则来自契约，不是审美选择：
 *
 * 1. `timeout` / `unknown` 是**不确定态**，文案必须是「结果未知，正在查证」，
 *    既不能显示成功也不能显示失败（S08 / §4.1 第 4 句）。
 * 2. `replayed_from_idempotency` 必须**显式标回放**，否则用户以为办了两件事（§4.1 第 3 句）。
 * 3. 没有终态（null）是「未收口」，不等于成功也不等于失败。
 */
interface StatusView {
  label: string
  tone: 'ok' | 'bad' | 'warn' | 'idle'
  hint: string
  replayed: boolean
}

function statusView(): StatusView {
  const r = result.value
  if (!r) {
    return { label: '未收口', tone: 'idle', hint: '没有终态记录：可能仍待审批、执行中，或进程在收口前退出。', replayed: false }
  }
  const replayed = r.code === 'replayed_from_idempotency'
  switch (r.status) {
    case 'succeeded':
      return replayed
        ? { label: '成功（回放）', tone: 'warn', hint: '这一单此前已执行过，本次没有产生新的外部副作用。', replayed: true }
        : { label: '成功', tone: 'ok', hint: '外部系统已接受并返回单号。', replayed: false }
    case 'failed':
      return { label: '失败', tone: 'bad', hint: `执行失败${r.errorCode ? `：${r.errorCode}` : ''}。`, replayed }
    case 'rejected':
      return { label: '已拒绝', tone: 'bad', hint: '校验或人工拒绝；外部系统什么都没发生。', replayed }
    case 'timeout':
      return { label: '结果未知', tone: 'warn', hint: '外部调用超时。系统只查证、不重放——请按单查证后再决定。', replayed }
    case 'unknown':
      return { label: '结果未知', tone: 'warn', hint: '外部状态不明。系统只查证、不重放——请按单查证后再决定。', replayed }
    case 'pending':
      return { label: '待审批', tone: 'idle', hint: '尚未有人批准；外部系统零调用。', replayed }
    case 'in_progress':
      return { label: '执行中', tone: 'idle', hint: '已开始执行，尚未收口。', replayed }
    default:
      return { label: r.status, tone: 'idle', hint: '', replayed }
  }
}

const view = computed(statusView)

async function load(): Promise<void> {
  if (!requestId.value) return
  loading.value = true
  error.value = null
  try {
    // 轨迹先取：即使终态 404，也还能显示「这一单是什么」。
    trail.value = await actionsApi.audit(requestId.value)
    try {
      const r = await actionsApi.get(requestId.value)
      // 202 返回的是 {requestId,status,code}，没有 data 字段，用类型守卫区分。
      result.value = 'data' in r ? (r as ActionResultDto) : null
    } catch {
      result.value = null
    }
    if (!trail.value.length && !result.value) error.value = '查无此单：请求号不存在，或轨迹尚未落库。'
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    loading.value = false
  }
}

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

function pretty(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw || '{}'
  }
}

/** 闸门结论 → 标签色。`deny` 是拒绝，其余都算通过（`allow` / `written`）。 */
function gateTag(outcome: string | null): { text: string; type: 'success' | 'danger' | 'info' } {
  if (!outcome) return { text: '未落痕', type: 'info' }
  if (outcome === 'deny') return { text: '拒绝', type: 'danger' }
  return { text: outcome, type: 'success' }
}

onMounted(load)
watch(requestId, load)
</script>

<template>
  <div class="ad-page">
    <header class="ad-head">
      <el-button :icon="ArrowLeft" text @click="router.back()">返回</el-button>
      <span class="ad-spacer" />
      <el-button :icon="RefreshRight" :loading="loading" @click="load">刷新</el-button>
    </header>

    <div v-loading="loading">
      <el-alert v-if="error" :title="error" type="warning" :closable="false" show-icon />

      <template v-else>
        <!-- 这一单是什么（S05） -->
        <section class="ad-card">
          <div class="ad-title-row">
            <code class="ad-key">{{ actionKey }}</code>
            <el-tag :type="view.tone === 'ok' ? 'success' : view.tone === 'bad' ? 'danger' : view.tone === 'warn' ? 'warning' : 'info'">
              {{ view.label }}
            </el-tag>
            <el-tag v-if="view.replayed" type="warning" effect="dark">回放 · 非新执行</el-tag>
            <span class="ad-spacer" />
            <span class="ad-meta">{{ fmtTime(trail[0]?.at ?? null) }}</span>
          </div>
          <p class="ad-hint">{{ view.hint }}</p>

          <dl class="ad-grid">
            <dt>请求号</dt>
            <dd><code>{{ requestId }}</code></dd>
            <dt>外部单号</dt>
            <dd>
              <code v-if="result?.externalId">{{ result.externalId }}</code>
              <span v-else class="ad-weak">无（未执行或执行失败）</span>
            </dd>
            <dt>归类码</dt>
            <dd><code v-if="result?.code">{{ result.code }}</code><span v-else class="ad-weak">—</span></dd>
            <dt>错误码</dt>
            <dd><code v-if="result?.errorCode">{{ result.errorCode }}</code><span v-else class="ad-weak">—</span></dd>
          </dl>

          <details class="ad-args">
            <summary>提交参数（对底座不透明，原样展示）</summary>
            <pre>{{ pretty(argumentsText) }}</pre>
          </details>
        </section>

        <!-- 四道闸门（S06） -->
        <section class="ad-card">
          <h2 class="ad-h2">四道闸门</h2>
          <p class="ad-note">
            逐道判定，<strong>缺任何一道就是没走完</strong>。任一道 deny，执行体零调用。
          </p>
          <ul class="ad-gates">
            <li v-for="g in gates" :key="g.stage" class="ad-gate">
              <span class="ad-gate-name">{{ g.label }}</span>
              <el-tag size="small" :type="gateTag(g.event?.outcome ?? null).type">
                {{ gateTag(g.event?.outcome ?? null).text }}
              </el-tag>
              <span class="ad-gate-reason">{{ g.event?.result ?? '—' }}</span>
              <span class="ad-gate-at">{{ fmtTime(g.event?.at ?? null) }}</span>
            </li>
          </ul>
        </section>

        <!-- 审批（S07） -->
        <section class="ad-card">
          <h2 class="ad-h2">审批</h2>
          <p v-if="!approval" class="ad-weak">这一单没有审批记录（动作不需要审批，或尚未提交到审批环节）。</p>
          <dl v-else class="ad-grid">
            <dt>最终结论</dt>
            <dd><el-tag size="small" :type="gateTag(approval.outcome).type">{{ approval.outcome }}</el-tag></dd>
            <dt>审批人</dt>
            <dd>{{ approval.approver ?? '—' }}</dd>
            <dt>时间</dt>
            <dd>{{ fmtTime(approval.at) }}</dd>
            <dt>说明</dt>
            <dd>{{ approval.result ?? '—' }}</dd>
          </dl>
          <p v-if="approvalAll.length > 1" class="ad-weak">
            共 {{ approvalAll.length }} 条审批记录（含提交时的挂起态与每次人工决定），完整顺序见下方轨迹。
          </p>
        </section>

        <!-- 执行（S08） -->
        <section class="ad-card">
          <h2 class="ad-h2">执行结果</h2>
          <p v-if="!execution.length" class="ad-weak">尚未执行（待审批，或校验未通过）。</p>
          <dl v-for="e in execution" :key="e.at + e.outcome" class="ad-grid">
            <dt>结论</dt>
            <dd><el-tag size="small" :type="gateTag(e.outcome).type">{{ e.outcome }}</el-tag></dd>
            <dt>外部单号</dt>
            <dd><code v-if="e.externalId">{{ e.externalId }}</code><span v-else class="ad-weak">—</span></dd>
            <dt>时间</dt>
            <dd>{{ fmtTime(e.at) }}</dd>
            <dt>返回摘要</dt>
            <dd class="ad-wrap">{{ e.result ?? '—' }}</dd>
          </dl>
        </section>

        <!-- 时间线（S10） -->
        <section class="ad-card">
          <h2 class="ad-h2">完整轨迹</h2>
          <p class="ad-note">按发生顺序。这是「谁 / 何时 / 依据哪条规则 / 变成什么」的原始记录。</p>
          <table class="ad-timeline">
            <thead>
              <tr><th>时间</th><th>阶段</th><th>结论</th><th>审批人</th><th>说明</th></tr>
            </thead>
            <tbody>
              <tr v-for="(e, i) in trail" :key="i">
                <td class="ad-nowrap">{{ fmtTime(e.at) }}</td>
                <td><code>{{ e.stage }}</code></td>
                <td>
                  <el-tag size="small" :type="gateTag(e.outcome).type">{{ e.outcome }}</el-tag>
                </td>
                <td>{{ e.approver ?? '—' }}</td>
                <td class="ad-wrap">{{ e.result ?? '—' }}</td>
              </tr>
              <tr v-if="!trail.length">
                <td colspan="5" class="ad-weak">没有轨迹记录。</td>
              </tr>
            </tbody>
          </table>
        </section>
      </template>
    </div>
  </div>
</template>

<style scoped>
.ad-page {
  max-width: 900px;
  margin: 0 auto;
  padding: 20px 24px 48px;
}

.ad-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.ad-spacer {
  flex: 1;
}

.ad-card {
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  background: var(--aw-surface);
  padding: 16px 18px;
  margin-bottom: 12px;
}

.ad-title-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.ad-key {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 15px;
  font-weight: 650;
  color: var(--aw-text);
}

.ad-h2 {
  margin: 0 0 6px;
  font-size: 14px;
  font-weight: 600;
  color: var(--aw-text);
}

.ad-hint,
.ad-note {
  margin: 8px 0 0;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--aw-text);
  opacity: 0.72;
}

.ad-meta,
.ad-weak {
  font-size: 12px;
  color: var(--aw-text);
  opacity: 0.55;
}

.ad-grid {
  display: grid;
  grid-template-columns: 84px 1fr;
  gap: 6px 12px;
  margin: 10px 0 0;
  font-size: 13px;
}

.ad-grid dt {
  color: var(--aw-text);
  opacity: 0.6;
}

.ad-grid dd {
  margin: 0;
  color: var(--aw-text);
  word-break: break-all;
}

.ad-wrap {
  word-break: break-word;
  white-space: pre-wrap;
}

.ad-nowrap {
  white-space: nowrap;
}

.ad-args {
  margin-top: 12px;
  font-size: 12.5px;
}

.ad-args summary {
  cursor: pointer;
  color: var(--aw-text);
  opacity: 0.7;
}

.ad-args pre {
  margin: 8px 0 0;
  padding: 10px 12px;
  background: var(--aw-bg);
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 240px;
  overflow: auto;
}

.ad-gates {
  list-style: none;
  margin: 10px 0 0;
  padding: 0;
}

.ad-gate {
  display: grid;
  grid-template-columns: 76px 64px 1fr auto;
  align-items: center;
  gap: 10px;
  padding: 7px 0;
  border-top: 1px solid var(--aw-border);
  font-size: 12.5px;
}

.ad-gate:first-child {
  border-top: none;
}

.ad-gate-name {
  font-weight: 600;
  color: var(--aw-text);
}

.ad-gate-reason {
  color: var(--aw-text);
  opacity: 0.78;
  word-break: break-word;
}

.ad-gate-at {
  color: var(--aw-text);
  opacity: 0.45;
  font-size: 11.5px;
  white-space: nowrap;
}

.ad-timeline {
  width: 100%;
  border-collapse: collapse;
  margin-top: 10px;
  font-size: 12.5px;
}

.ad-timeline th {
  text-align: left;
  font-weight: 600;
  color: var(--aw-text);
  opacity: 0.65;
  padding: 6px 8px;
  border-bottom: 1px solid var(--aw-border);
  white-space: nowrap;
}

.ad-timeline td {
  padding: 6px 8px;
  border-bottom: 1px solid var(--aw-border);
  color: var(--aw-text);
  vertical-align: top;
}
</style>
