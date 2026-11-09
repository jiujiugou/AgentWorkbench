<script setup lang="ts">
import { computed, ref } from 'vue'
import { ArrowRight, Cpu, Warning } from '@element-plus/icons-vue'
import type { RunSummary, ToolStepEvent } from '@/types'

const props = defineProps<{
  tools?: ToolStepEvent[]
  run?: RunSummary
  /** 中止终态（cancelled / timeout / failed）：取消路径没有 run 摘要，靠它把轨迹条留住。 */
  stopStatus?: string
}>()

const open = ref(false)

const steps = computed(() => props.tools ?? [])
const hasAnything = computed(() => steps.value.length > 0 || !!props.run || !!props.stopStatus)

/** 后端 AgentRunStatus 取值 → 展示文案 + 语气（取值见 Shared/Execution/AgentRunStatus.cs）。 */
const STATUS: Record<string, { text: string; tone: 'ok' | 'warn' | 'bad' | 'live' }> = {
  succeeded: { text: '正常收尾', tone: 'ok' },
  iteration_limit: { text: '跑满迭代，强制收口', tone: 'warn' },
  loop_detected: { text: '检测到循环，已终止', tone: 'warn' },
  budget_exceeded: { text: '超预算，已终止', tone: 'warn' },
  cancelled: { text: '已取消', tone: 'warn' },
  timeout: { text: '超时，已终止', tone: 'bad' },
  failed: { text: '失败', tone: 'bad' },
  // 拒答是一次**完成了**的运行，不是失败，也不是"没跑"：语气要与失败分开，
  // 否则用户会把"资料里没有"当成系统坏了（C-V13 的界面一侧）
  refused: { text: '拒答（资料不足）', tone: 'warn' },
  // 停在 running 的 run 有两种可能：真在跑，或进程已经不在了。文案不替它二选一。
  running: { text: '运行中', tone: 'live' },
}

const status = computed(() => {
  const s = props.run?.status ?? props.stopStatus
  if (!s) return null
  return STATUS[s] ?? { text: s, tone: 'warn' as const }
})

const latency = computed(() => {
  const ms = props.run?.latencyMs
  if (ms == null) return ''
  return ms >= 1000 ? (ms / 1000).toFixed(1) + 's' : ms + 'ms'
})

/**
 * 四段流水线 vs agent 循环。
 *
 * Python 版的作答是「预检索 → 判定 → 生成 → 校验」，**没有迭代**：一次就是一轮。
 * 照 C# 的模板说"1 轮迭代"会让用户以为模型在反复调工具，而实际上它一次都没选过工具
 * （工具是系统在生成前调好的）。同一个组件服务两种形态，所以由数据说自己是哪种。
 */
const pipeline = computed(() => props.run?.pipeline === true)

const headline = computed(() => {
  const parts: string[] = []
  if (props.run) {
    parts.push(pipeline.value ? '四段流水线' : `${props.run.iterations} 轮迭代`)
    parts.push(`${steps.value.length || props.run.tools} 次工具调用`)
    if (latency.value) parts.push(latency.value)
  } else if (props.stopStatus) {
    parts.push(`已执行 ${steps.value.length} 次工具调用`)
  } else {
    parts.push(`执行中 · 已调用 ${steps.value.length} 次工具`)
  }
  return parts.join(' · ')
})

/** 工具结果是 JSON 片段时压成一行，避免轨迹面板被大段文本撑爆。 */
function oneLine(text?: string | null, max = 220): string {
  if (!text) return ''
  const t = text.replace(/\s+/g, ' ').trim()
  return t.length > max ? t.slice(0, max) + '…' : t
}
</script>

<template>
  <div v-if="hasAnything" class="trace" :class="{ live: !run && !stopStatus }">
    <button class="trace-head" @click="open = !open">
      <el-icon class="trace-icon"><Cpu /></el-icon>
      <span class="trace-title">执行过程</span>
      <span class="trace-metrics">{{ headline }}</span>
      <span v-if="status" class="trace-status" :class="status.tone">{{ status.text }}</span>
      <span v-else class="trace-status running">运行中</span>
      <el-icon class="chevron" :class="{ open }"><ArrowRight /></el-icon>
    </button>

    <div v-if="open" class="trace-body">
      <div v-if="!steps.length" class="trace-empty">
        <template v-if="stopStatus">在调用任何工具前就已中止。</template>
        <template v-else>
          本次 run 没有调用任何工具（模型直接作答）。
          <template v-if="run && pipeline">流水线跑完了，没有单独的工具步。</template>
          <template v-else-if="run">共 {{ run.iterations }} 轮迭代，耗时 {{ latency }}。</template>
        </template>
      </div>

      <div v-for="(s, i) in steps" :key="i" class="step" :class="{ failed: !!s.error }">
        <div class="step-head">
          <span class="step-idx">#{{ i + 1 }}</span>
          <span class="step-name">{{ s.name }}</span>
          <span v-if="!pipeline" class="step-iter">第 {{ s.iteration + 1 }} 轮</span>
          <el-icon v-if="s.error" class="step-warn"><Warning /></el-icon>
        </div>
        <div v-if="s.arguments" class="step-row">
          <span class="step-k">入参</span>
          <code class="step-v">{{ oneLine(s.arguments) }}</code>
        </div>
        <div v-if="s.error" class="step-row">
          <span class="step-k">异常</span>
          <code class="step-v error">{{ oneLine(s.error, 400) }}</code>
        </div>
        <div v-else-if="s.resultPreview" class="step-row">
          <span class="step-k">返回</span>
          <code class="step-v">{{ oneLine(s.resultPreview) }}</code>
        </div>
      </div>

      <div v-if="run" class="trace-foot">
        run #{{ String(run.runId).slice(0, 8) }}
        <template v-if="run.model"> · {{ run.model }}</template>
        · {{ run.steps }} 步轨迹
        <template v-if="run.code"> · 原因码 {{ run.code }}</template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.trace {
  margin-top: 8px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-surface-2);
  overflow: hidden;
  max-width: 720px;
}

.trace.live {
  border-color: color-mix(in srgb, var(--aw-primary) 35%, var(--aw-border));
}

.trace-head {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  background: transparent;
  border: 0;
  cursor: pointer;
  font-size: 12.5px;
  color: var(--aw-text-2);
  text-align: left;
}

.trace-head:hover {
  background: var(--aw-surface-3);
}

.trace-icon {
  color: var(--aw-primary);
}

.trace-title {
  font-weight: 600;
  color: var(--aw-text);
}

.trace-metrics {
  color: var(--aw-text-2);
}

.trace-status {
  margin-left: auto;
  padding: 1px 8px;
  border-radius: var(--aw-radius-full);
  font-size: 11.5px;
  font-weight: 600;
}

.trace-status.ok {
  color: #0b7a5c;
  background: color-mix(in srgb, var(--aw-success) 14%, transparent);
}

.trace-status.warn {
  color: #92590a;
  background: color-mix(in srgb, var(--aw-warning) 16%, transparent);
}

.trace-status.bad {
  color: #b02525;
  background: color-mix(in srgb, var(--aw-danger) 14%, transparent);
}

.trace-status.running,
.trace-status.live {
  color: var(--aw-primary);
  background: color-mix(in srgb, var(--aw-primary) 12%, transparent);
}

.chevron {
  transition: transform var(--aw-dur) var(--aw-ease);
  color: var(--aw-text-3);
}

.chevron.open {
  transform: rotate(90deg);
}

.trace-body {
  padding: 4px 12px 10px;
  border-top: 1px dashed var(--aw-border);
}

.trace-empty {
  padding: 8px 0;
  font-size: 12.5px;
  color: var(--aw-text-3);
}

.step {
  padding: 8px 0;
  border-bottom: 1px solid var(--aw-border);
}

.step:last-of-type {
  border-bottom: 0;
}

.step-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
}

.step-idx {
  color: var(--aw-text-3);
  font-variant-numeric: tabular-nums;
}

.step-name {
  font-weight: 600;
  font-family: var(--aw-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  color: var(--aw-text);
}

.step-iter {
  font-size: 11.5px;
  color: var(--aw-text-3);
}

.step.failed .step-name {
  color: var(--aw-danger);
}

.step-warn {
  color: var(--aw-danger);
}

.step-row {
  display: flex;
  gap: 8px;
  margin-top: 4px;
  font-size: 12px;
  line-height: 1.5;
}

.step-k {
  flex: none;
  width: 28px;
  color: var(--aw-text-3);
}

.step-v {
  color: var(--aw-text-2);
  font-family: var(--aw-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  word-break: break-all;
}

.step-v.error {
  color: var(--aw-danger);
}

.trace-foot {
  padding-top: 8px;
  font-size: 11.5px;
  color: var(--aw-text-3);
  font-variant-numeric: tabular-nums;
}
</style>
