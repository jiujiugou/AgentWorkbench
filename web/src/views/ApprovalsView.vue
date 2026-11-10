<script setup lang="ts">
/**
 * 待审批动作。承载界面契约的 **S06 + S07**。
 *
 * 设计依据（业界 HITL 审批的共识，逐条对着改过）：
 *
 * 1. **「Book this flight? $412, non-refundable」是审批；「Continue?」不是。**
 *    所以这里显示「创建议题 / 标题 / 内容 / 会不会留下真实变更」，不显示 `{"title": ...}`。
 * 2. **把 JSON 当审批依据就是演戏**（"an approval card that hides the payload is theater"）。
 *    参数要可读、可核对，但按字段排出来，不是让审批人自己解析括号。
 * 3. **说清能否撤销**：不能撤销的必须先说。本版没有一键撤销，就明说没有。
 * 4. **技术细节折叠，后果不许折叠**。四道闸门收成一行「4 项自动检查已通过」可展开——
 *    审批人要的是「有没有拦下来」，不是每道闸门的英文名。
 * 5. **不提供「改参数后批准」**：改参数等于换一张单，必须重走 S05–S06。
 *    想改就拒绝并说明，让模型重提。
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Check, CloseBold, RefreshRight, Right, Warning } from '@element-plus/icons-vue'
import { actionsApi, type PendingAction, type TraceEvent } from '@/api/actions'
import { cachedUser, type AuthUser } from '@/api/auth'
import { useRouter } from 'vue-router'

const me = ref<AuthUser | null>(null)
const rows = ref<PendingAction[]>([])
const trails = ref<Record<string, TraceEvent[]>>({})
const loading = ref(false)
const busy = ref<string | null>(null)
const router = useRouter()

const canDecide = computed(() => me.value?.role === 'owner')

const GATES = ['schema', 'permission', 'business_rule', 'audit'] as const
const GATE_LABELS: Record<string, string> = {
  schema: '格式',
  permission: '权限',
  business_rule: '业务规则',
  audit: '留痕',
}

/** 风险等级只展示，不参与任何判定（`RiskLevels` 的契约注明了这一点）。 */
const RISK: Record<string, { text: string; type: 'success' | 'warning' | 'danger' | 'info' }> = {
  low: { text: '低风险', type: 'success' },
  medium: { text: '中风险', type: 'warning' },
  high: { text: '高风险', type: 'danger' },
  critical: { text: '极高风险', type: 'danger' },
}

/**
 * 参数键的**显示词汇表**（只影响排版，不是业务语义）。
 *
 * 底座仍然不认识参数——这里只是把 `title` 排成「标题」，让人一眼能读。
 * 认不出的键原样显示，不猜含义；长对象仍可展开看原始 JSON。
 */
const FIELD_LABELS: Record<string, string> = {
  title: '标题',
  body: '内容',
  name: '名称',
  repo: '仓库',
  owner: '归属',
  number: '编号',
  amount: '金额',
  currency: '币种',
  date: '日期',
  supplier: '供应商',
  item: '物料',
  qty: '数量',
  price: '单价',
  remark: '备注',
  message: '消息',
  to: '收件人',
  subject: '主题',
  reason: '原因',
}

interface Field {
  key: string
  label: string
  value: string
  long: boolean
}

function fieldsOf(args: unknown): Field[] {
  if (!args || typeof args !== 'object' || Array.isArray(args)) return []
  return Object.entries(args as Record<string, unknown>).map(([key, raw]) => {
    const value = typeof raw === 'string' ? raw : JSON.stringify(raw)
    return {
      key,
      label: FIELD_LABELS[key] ?? key,
      value,
      long: value.length > 60,
    }
  })
}

async function refresh(silent = false): Promise<void> {
  if (!silent) loading.value = true
  try {
    const next = await actionsApi.pending()
    // 静默轮询时，新冒出来的单要能让人察觉——否则页面「看起来没变」，
    // 用户会以为模型没提交，实际是它正等着被点。
    if (silent && next.length > rows.value.length) {
      ElMessage.info(`有 ${next.length - rows.value.length} 条新的待审批动作`)
    }
    rows.value = next
    // 依据与列表一起取：审批人看不到「有没有被拦」，就不该做决定。
    const pairs = await Promise.all(
      rows.value.map(async (r) => {
        try {
          return [r.requestId, await actionsApi.audit(r.requestId)] as const
        } catch {
          return [r.requestId, [] as TraceEvent[]] as const
        }
      }),
    )
    trails.value = Object.fromEntries(pairs)
  } catch (e) {
    if (!silent) ElMessage.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

function gatesOf(requestId: string) {
  const trail = trails.value[requestId] ?? []
  return GATES.map((stage) => ({
    stage,
    label: GATE_LABELS[stage],
    event: trail.find((e) => e.stage === stage) ?? null,
  }))
}

/** 一行结论：审批人只想知道「有没有被拦下来」。 */
function checkSummary(requestId: string): { ok: boolean; text: string } {
  const gates = gatesOf(requestId)
  const denied = gates.find((g) => g.event?.outcome === 'deny')
  if (denied) return { ok: false, text: `${denied.label}检查未通过` }
  const missing = gates.filter((g) => !g.event)
  if (missing.length) return { ok: false, text: `${missing.length} 项检查没有留下记录` }
  return { ok: true, text: `${gates.length} 项自动检查已通过` }
}

function gateTag(outcome: string | null): { text: string; type: 'success' | 'danger' | 'info' } {
  if (!outcome) return { text: '未落痕', type: 'info' }
  if (outcome === 'deny') return { text: '拒绝', type: 'danger' }
  return { text: '通过', type: 'success' }
}

/**
 * 只显示描述的第一句。
 *
 * 动作域的 `Description` 是**写给模型看的**（"什么时候该用、参数是什么"），
 * 常带「原语是 Draft 但 SideEffecting=true」这类实现说明——那对签字的人是噪音。
 * 第一句通常正好是「这个动作做什么」，其余用 tooltip 兜住，不丢信息也不挡视线。
 */
function shortDesc(desc: string | null): string {
  if (!desc) return ''
  const first = desc.split('。')[0]
  return first ? `${first}。` : desc
}

/** 撤销说明：没有的能力就直说没有，不给人「应该能撤回」的错觉。 */
function undoHint(row: PendingAction): string {
  if (!row.sideEffecting) return '这类操作不改动外部系统，不需要撤销。'
  return `会在 ${row.actionSet} 里留下真实变更。要恢复请到该系统里处理——本版没有一键撤销。`
}

function describe(r: { status: string; code: string | null; externalId: string | null }): string {
  const parts = [r.status]
  if (r.code) parts.push(r.code)
  if (r.externalId) parts.push(`外部编号 ${r.externalId}`)
  return parts.join(' / ')
}

async function approve(row: PendingAction): Promise<void> {
  busy.value = row.requestId
  try {
    const r = await actionsApi.approve(row.requestId)
    ElMessage.success(`已批准并执行：${describe(r)}`)
    await refresh()
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = null
  }
}

async function reject(row: PendingAction): Promise<void> {
  let reason = ''
  try {
    const r = await ElMessageBox.prompt(
      '拒绝原因（必填）：这句话会进审计，也是让模型重做时唯一的依据。',
      `拒绝「${row.displayName}」`,
      {
        inputPlaceholder: '例如：标题不符合命名规范，请重写后再提交',
        confirmButtonText: '拒绝',
        cancelButtonText: '取消',
        // 必填在界面这一层强制：后端允许空并回退成 "rejected by human"，
        // 但那样审计里就只有一句套话，答不了「依据什么」。
        inputValidator: (v: string) => (v && v.trim().length > 0 ? true : '请填写拒绝原因'),
      },
    )
    reason = r.value?.trim() ?? ''
  } catch {
    return // 用户取消，不算失败
  }

  busy.value = row.requestId
  try {
    const r = await actionsApi.reject(row.requestId, reason)
    ElMessage.success(`已拒绝：${describe(r)}`)
    await refresh()
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = null
  }
}

function fmtTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

function openDetail(row: PendingAction): void {
  void router.push({ name: 'action-detail', params: { requestId: row.requestId } })
}

/**
 * 轮询而不是推送：Hub 目前没有「挂起」事件，模型提交一单时前端收不到通知。
 * 8 秒是这一版最省的补法；真正的解是在 Hub 上加一条事件，那是另一件事。
 * 页面不可见时停轮询，别在后台空转。
 */
const POLL_MS = 8000
let timer: number | undefined

function startPolling(): void {
  stopPolling()
  timer = window.setInterval(() => {
    if (document.visibilityState === 'visible' && !busy.value) void refresh(true)
  }, POLL_MS)
}

function stopPolling(): void {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

onMounted(async () => {
  me.value = await cachedUser()
  await refresh()
  startPolling()
})

onUnmounted(stopPolling)
</script>

<template>
  <div class="ap-page">
    <header class="ap-head">
      <div>
        <h1 class="ap-title">
          待审批
          <span v-if="rows.length" class="ap-count">{{ rows.length }}</span>
        </h1>
        <p class="ap-sub">
          AI 提交的写操作停在这里，<strong>批准前外部系统零调用</strong>。批准人以你当前登录的身份记录。
        </p>
      </div>
      <el-button :icon="RefreshRight" :loading="loading" @click="refresh()">刷新</el-button>
    </header>

    <div v-loading="loading" class="ap-body">
      <el-empty
        v-if="!rows.length && !loading"
        description="没有待审批的动作。AI 提交需要人工确认的写操作时，会出现在这里。"
      />

      <article v-for="row in rows" :key="row.requestId" class="ap-card">
        <!-- 标题：用人话的动作名，内部 key 退到第二行 -->
        <div class="ap-card-head">
          <h2 class="ap-name">{{ row.displayName }}</h2>
          <el-tag
            v-if="row.riskLevel && RISK[row.riskLevel]"
            size="small"
            :type="RISK[row.riskLevel].type"
          >
            {{ RISK[row.riskLevel].text }}
          </el-tag>
          <el-tag v-if="row.sideEffecting" size="small" type="info" effect="plain">会改动外部数据</el-tag>
          <span class="ap-spacer" />
          <code class="ap-sub-key">{{ row.actionKey }} · {{ row.actionSet }}</code>
        </div>

        <!-- 将要发生什么：按字段排，不放 JSON -->
        <div class="ap-do">
          <div class="ap-do-title">将要发生</div>
          <dl v-if="fieldsOf(row.arguments).length" class="ap-fields">
            <template v-for="f in fieldsOf(row.arguments)" :key="f.key">
              <dt>{{ f.label }}</dt>
              <dd :class="{ long: f.long }">{{ f.value }}</dd>
            </template>
          </dl>
          <p v-else class="ap-weak">这个动作没有参数。</p>
        </div>

        <p v-if="row.description" class="ap-desc" :title="row.description">{{ shortDesc(row.description) }}</p>

        <div class="ap-undo">
          <el-icon><Warning /></el-icon>
          <span>{{ undoHint(row) }}</span>
        </div>

        <!-- 自动检查：结论一行，细节可展开 -->
        <details class="ap-checks">
          <summary>
            <span class="ap-check-mark" :class="checkSummary(row.requestId).ok ? 'ok' : 'bad'">
              {{ checkSummary(row.requestId).ok ? '✓' : '!' }}
            </span>
            {{ checkSummary(row.requestId).text }}
            <span class="ap-checks-more">展开细节</span>
          </summary>
          <ul class="ap-gate-list">
            <li v-for="g in gatesOf(row.requestId)" :key="g.stage" class="ap-gate">
              <span class="ap-gate-name">{{ g.label }}</span>
              <el-tag size="small" :type="gateTag(g.event?.outcome ?? null).type">
                {{ gateTag(g.event?.outcome ?? null).text }}
              </el-tag>
              <span class="ap-gate-reason">{{ g.event?.result ?? '—' }}</span>
            </li>
          </ul>
        </details>

        <div class="ap-card-foot">
          <span class="ap-meta">AI 助手提交 · {{ row.subject }} · {{ fmtTime(row.at) }}</span>
          <code class="ap-rid" :title="row.requestId">{{ row.requestId.slice(0, 8) }}…</code>
          <el-button text :icon="Right" @click="openDetail(row)">详情与轨迹</el-button>

          <div v-if="canDecide" class="ap-actions">
            <el-button
              type="success"
              :icon="Check"
              :loading="busy === row.requestId"
              @click="approve(row)"
            >
              批准并执行
            </el-button>
            <el-button
              type="danger"
              plain
              :icon="CloseBold"
              :loading="busy === row.requestId"
              @click="reject(row)"
            >
              拒绝
            </el-button>
          </div>
          <span v-else class="ap-meta">只有 owner 可以批准或拒绝</span>
        </div>
      </article>
    </div>
  </div>
</template>

<style scoped>
.ap-page {
  max-width: 820px;
  margin: 0 auto;
  padding: 28px 24px 48px;
}

.ap-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 20px;
}

.ap-title {
  margin: 0 0 6px;
  font-size: 22px;
  font-weight: 650;
  color: var(--aw-text);
}

.ap-count {
  display: inline-block;
  min-width: 20px;
  margin-left: 6px;
  padding: 0 7px;
  border-radius: var(--aw-radius-full);
  background: var(--aw-warning);
  color: #fff;
  font-size: 12.5px;
  line-height: 20px;
  text-align: center;
  vertical-align: 3px;
}

.ap-sub {
  margin: 0;
  font-size: 13px;
  line-height: 1.6;
  color: var(--aw-text-weak, var(--aw-text));
  opacity: 0.72;
}

.ap-body {
  min-height: 160px;
}

.ap-card {
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  background: var(--aw-surface);
  padding: 16px 18px;
  margin-bottom: 14px;
}

.ap-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.ap-name {
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  color: var(--aw-text);
}

.ap-spacer {
  flex: 1;
}

.ap-sub-key {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11.5px;
  color: var(--aw-text);
  opacity: 0.4;
}

.ap-do {
  margin-top: 12px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-bg);
  padding: 10px 12px;
}

.ap-do-title {
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: var(--aw-text);
  opacity: 0.55;
  margin-bottom: 6px;
}

.ap-fields {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 6px 14px;
  margin: 0;
  font-size: 13.5px;
}

.ap-fields dt {
  color: var(--aw-text);
  opacity: 0.6;
  white-space: nowrap;
}

.ap-fields dd {
  margin: 0;
  color: var(--aw-text);
  word-break: break-word;
}

.ap-fields dd.long {
  line-height: 1.55;
}

.ap-desc {
  margin: 10px 0 0;
  font-size: 12.5px;
  line-height: 1.65;
  color: var(--aw-text);
  opacity: 0.7;
}

.ap-undo {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin-top: 10px;
  padding: 8px 10px;
  border-radius: var(--aw-radius-m);
  background: color-mix(in srgb, var(--aw-warning) 8%, transparent);
  font-size: 12.5px;
  line-height: 1.55;
  color: var(--aw-text);
}

.ap-undo .el-icon {
  flex: none;
  margin-top: 2px;
  color: var(--aw-warning);
}

.ap-checks {
  margin-top: 10px;
  font-size: 12.5px;
}

.ap-checks summary {
  display: flex;
  align-items: center;
  gap: 7px;
  cursor: pointer;
  color: var(--aw-text);
  list-style: none;
}

.ap-checks summary::-webkit-details-marker {
  display: none;
}

.ap-check-mark {
  display: inline-grid;
  place-items: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  font-size: 11px;
  font-weight: 700;
  color: #fff;
}

.ap-check-mark.ok {
  background: var(--aw-success);
}

.ap-check-mark.bad {
  background: var(--aw-danger);
}

.ap-checks-more {
  color: var(--aw-primary);
  font-size: 11.5px;
}

.ap-gate-list {
  list-style: none;
  margin: 8px 0 0;
  padding: 0 0 0 2px;
}

.ap-gate {
  display: grid;
  grid-template-columns: 64px 56px 1fr;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
  font-size: 12px;
}

.ap-gate-name {
  color: var(--aw-text);
  opacity: 0.75;
}

.ap-gate-reason {
  color: var(--aw-text);
  opacity: 0.6;
  word-break: break-word;
}

.ap-card-foot {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--aw-border);
}

.ap-meta {
  font-size: 12px;
  color: var(--aw-text);
  opacity: 0.55;
}

.ap-rid {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11px;
  color: var(--aw-text);
  opacity: 0.35;
  cursor: help;
}

.ap-weak {
  margin: 0;
  font-size: 12.5px;
  color: var(--aw-text);
  opacity: 0.5;
}

.ap-actions {
  margin-left: auto;
  display: flex;
  gap: 8px;
}
</style>
