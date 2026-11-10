<script setup lang="ts">
/**
 * 动作卡片：把聊天流里的 `propose_action` 从「折叠面板里的一行 JSON」提成看得见的一张单。
 *
 * 为什么必须有：产品承诺是「把模型想做什么变成系统真的做了什么」。
 * 但在此之前，模型递了一张单、单子挂起等审批，界面上只表现为
 * 「执行过程 → #3 propose_action → 返回 {"status":"pending",...}」——
 * 用户根本不知道有一件事正等着他点。审批页要手点刷新才看得到。
 *
 * 这张卡片只做一件事：**让「有一张单」这件事在对话里当场可见，并能一步跳到处置它的地方。**
 * 状态完全照抄服务端返回，界面不推断（不猜「应该成功了吧」）。
 */
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { Right } from '@element-plus/icons-vue'
import type { ToolStepEvent } from '@/types'

const props = defineProps<{ tools?: ToolStepEvent[] }>()
const router = useRouter()

interface ActionCard {
  /** 动作名；解析失败时退回「未知动作」，不猜。 */
  actionKey: string
  requestId: string
  status: string
  code: string
  /** 模型传的参数原文：给「批准前想核实一下」的人看。 */
  argumentsJson: string
  /** 工具本身异常（不是动作被拒），与业务拒绝分开显示。 */
  error?: string | null
}

function parseJson(raw?: string | null): Record<string, unknown> | null {
  if (!raw) return null
  try {
    const v = JSON.parse(raw)
    return typeof v === 'object' && v !== null ? (v as Record<string, unknown>) : null
  } catch {
    // resultPreview 被服务端截断过（Clip 600），截断后就不是合法 JSON 了。
    // 这时不猜内容，交给下面的「未收口」分支显示原文。
    return null
  }
}

const cards = computed<ActionCard[]>(() =>
  (props.tools ?? [])
    .filter((t) => t.name === 'propose_action')
    .map((t) => {
      const args = parseJson(t.arguments)
      const res = parseJson(t.resultPreview)
      return {
        actionKey: typeof args?.actionKey === 'string' ? args.actionKey : '未知动作',
        argumentsJson: typeof args?.argumentsJson === 'string' ? args.argumentsJson : '',
        requestId: typeof res?.requestId === 'string' ? res.requestId : '',
        status: typeof res?.status === 'string' ? res.status : '',
        code: typeof res?.code === 'string' ? res.code : '',
        error: t.error,
      }
    }),
)

interface View {
  label: string
  tone: 'warn' | 'ok' | 'bad' | 'idle'
  hint: string
  /** 这张单还需要人处置 → 跳到审批页；否则跳轨迹。 */
  needsHuman: boolean
}

function view(c: ActionCard): View {
  if (c.error) return { label: '调用失败', tone: 'bad', hint: c.error.slice(0, 160), needsHuman: false }
  switch (c.status) {
    case 'pending':
      return {
        label: '待审批',
        tone: 'warn',
        hint: '外部系统零调用，等你批准。',
        needsHuman: true,
      }
    case 'succeeded':
      return { label: '已执行', tone: 'ok', hint: '外部系统已接受。', needsHuman: false }
    case 'rejected':
      return { label: '被拒绝', tone: 'bad', hint: c.code ? `原因码 ${c.code}` : '校验未通过，零外部调用。', needsHuman: false }
    case 'failed':
      return { label: '执行失败', tone: 'bad', hint: c.code ? `原因码 ${c.code}` : '', needsHuman: false }
    case 'timeout':
    case 'unknown':
      return { label: '结果未知', tone: 'warn', hint: '只查证、不重放；请按单核对。', needsHuman: false }
    default:
      // 没解析出状态：可能是模型没走完，或返回值被截断。不假装成功。
      return { label: '未收口', tone: 'idle', hint: '没有拿到明确状态。', needsHuman: false }
  }
}

function go(c: ActionCard): void {
  const v = view(c)
  if (v.needsHuman) {
    void router.push({ name: 'approvals' })
    return
  }
  if (c.requestId) void router.push({ name: 'action-detail', params: { requestId: c.requestId } })
}
</script>

<template>
  <div v-if="cards.length" class="ac-list">
    <div
      v-for="(c, i) in cards"
      :key="`${c.requestId}-${i}`"
      class="ac-card"
      :class="view(c).tone"
    >
      <div class="ac-head">
        <span class="ac-badge">动作</span>
        <code class="ac-key">{{ c.actionKey }}</code>
        <span class="ac-state" :class="view(c).tone">{{ view(c).label }}</span>
        <span class="ac-spacer" />
        <button class="ac-go" @click="go(c)">
          {{ view(c).needsHuman ? '去审批' : '查看轨迹' }}
          <el-icon><Right /></el-icon>
        </button>
      </div>

      <div v-if="view(c).hint" class="ac-hint">{{ view(c).hint }}</div>

      <div v-if="c.argumentsJson" class="ac-args">
        <span class="ac-args-k">参数</span>
        <code>{{ c.argumentsJson }}</code>
      </div>

      <div v-if="c.requestId" class="ac-rid">
        <span class="ac-args-k">请求号</span>
        <code :title="c.requestId">{{ c.requestId }}</code>
      </div>
    </div>
  </div>
</template>

<style scoped>
.ac-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
  max-width: 720px;
}

.ac-card {
  border: 1px solid var(--aw-border);
  border-left: 3px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-surface-2);
  padding: 9px 12px;
}

.ac-card.warn {
  border-left-color: var(--aw-warning);
}
.ac-card.ok {
  border-left-color: var(--aw-success);
}
.ac-card.bad {
  border-left-color: var(--aw-danger);
}
.ac-card.idle {
  border-left-color: var(--aw-text-3);
}

.ac-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.ac-badge {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface-3);
  color: var(--aw-text-3);
}

.ac-key {
  font-family: var(--aw-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  font-size: 13px;
  font-weight: 650;
  color: var(--aw-text);
}

.ac-state {
  font-size: 11.5px;
  font-weight: 650;
  padding: 1px 8px;
  border-radius: var(--aw-radius-full);
}

.ac-state.warn {
  color: #92590a;
  background: color-mix(in srgb, var(--aw-warning) 16%, transparent);
}
.ac-state.ok {
  color: #0b7a5c;
  background: color-mix(in srgb, var(--aw-success) 14%, transparent);
}
.ac-state.bad {
  color: #b02525;
  background: color-mix(in srgb, var(--aw-danger) 14%, transparent);
}
.ac-state.idle {
  color: var(--aw-text-3);
  background: var(--aw-surface-3);
}

.ac-spacer {
  flex: 1;
}

.ac-go {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  border: 0;
  background: transparent;
  cursor: pointer;
  font-size: 12px;
  color: var(--aw-primary);
  padding: 2px 4px;
}

.ac-go:hover {
  text-decoration: underline;
}

.ac-hint {
  margin-top: 5px;
  font-size: 12px;
  color: var(--aw-text-2);
}

.ac-args,
.ac-rid {
  display: flex;
  gap: 8px;
  margin-top: 4px;
  font-size: 11.5px;
  color: var(--aw-text-2);
}

.ac-args code,
.ac-rid code {
  font-family: var(--aw-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  word-break: break-all;
  opacity: 0.85;
}

.ac-args-k {
  flex: none;
  width: 40px;
  color: var(--aw-text-3);
}
</style>
