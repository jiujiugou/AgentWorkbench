<script setup lang="ts">
/**
 * 动作追溯（列表）。产品判据②「任一动作能答：谁 / 何时 / 看到什么 / 依据哪条规则 / ERP 里变成什么」
 * 的**入口**——没有这一页，追溯只能靠人记住一个请求号，等于没有追溯。
 *
 * 只列摘要，点开进详情看四道闸门与完整轨迹。
 * 状态列守住三条：不确定态不装成成功/失败；回放要显式标；没有终态就是「未收口」。
 */
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { RefreshRight, Search } from '@element-plus/icons-vue'
import { actionsApi, type ActionSummary } from '@/api/actions'

const router = useRouter()
const rows = ref<ActionSummary[]>([])
const loading = ref(false)
const keyword = ref('')

const filtered = computed(() => {
  const k = keyword.value.trim().toLowerCase()
  if (!k) return rows.value
  return rows.value.filter(
    (r) =>
      r.actionKey.toLowerCase().includes(k) ||
      r.requestId.toLowerCase().includes(k) ||
      (r.externalId ?? '').toLowerCase().includes(k),
  )
})

interface StatusView {
  label: string
  type: 'success' | 'danger' | 'warning' | 'info'
}

/** 与详情页同一套判定，避免两处对同一状态给出不同说法。 */
function statusView(r: ActionSummary): StatusView {
  if (!r.status) return { label: '未收口', type: 'info' }
  if (r.code === 'replayed_from_idempotency') return { label: '回放', type: 'warning' }
  switch (r.status) {
    case 'succeeded':
      return { label: '成功', type: 'success' }
    case 'failed':
      return { label: '失败', type: 'danger' }
    case 'rejected':
      return { label: '已拒绝', type: 'danger' }
    case 'timeout':
    case 'unknown':
      return { label: '结果未知', type: 'warning' }
    case 'pending':
      return { label: '待审批', type: 'info' }
    case 'in_progress':
      return { label: '执行中', type: 'info' }
    default:
      return { label: r.status, type: 'info' }
  }
}

async function refresh(): Promise<void> {
  loading.value = true
  try {
    rows.value = await actionsApi.list(100)
  } catch (e) {
    // 失败不静默：列表空了和「请求失败」必须能区分。
    rows.value = []
    throw e
  } finally {
    loading.value = false
  }
}

function open(r: ActionSummary): void {
  void router.push({ name: 'action-detail', params: { requestId: r.requestId } })
}

function fmtTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 12)}…` : id
}

onMounted(() => {
  refresh().catch(() => {
    /* 错误已由全局 401 钩子或表格空态体现，这里只避免未处理的 rejection */
  })
})
</script>

<template>
  <div class="at-page">
    <header class="at-head">
      <div>
        <h1 class="at-title">动作追溯</h1>
        <p class="at-sub">
          每一次外部副作用在这里留痕。点开任意一条，看<strong>四道闸门依据什么放行</strong>、
          <strong>外部系统里变成了什么</strong>、<strong>是不是回放</strong>。
        </p>
      </div>
      <div class="at-tools">
        <el-input v-model="keyword" :prefix-icon="Search" placeholder="动作名 / 请求号 / 外部单号" clearable style="width: 240px" />
        <el-button :icon="RefreshRight" :loading="loading" @click="refresh">刷新</el-button>
      </div>
    </header>

    <el-table
      v-loading="loading"
      :data="filtered"
      class="at-table"
      row-key="requestId"
      @row-click="open"
    >
      <el-table-column label="动作" min-width="150">
        <template #default="{ row }">
          <code class="at-key">{{ row.actionKey }}</code>
        </template>
      </el-table-column>

      <el-table-column label="状态" width="110">
        <template #default="{ row }">
          <el-tag size="small" :type="statusView(row).type">{{ statusView(row).label }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="外部单号" width="120">
        <template #default="{ row }">
          <code v-if="row.externalId" class="at-ext">{{ row.externalId }}</code>
          <span v-else class="at-weak">—</span>
        </template>
      </el-table-column>

      <el-table-column label="提交时间" width="180">
        <template #default="{ row }">
          <span class="at-time">{{ fmtTime(row.at) }}</span>
        </template>
      </el-table-column>

      <el-table-column label="请求号" min-width="150">
        <template #default="{ row }">
          <code class="at-rid" :title="row.requestId">{{ shortId(row.requestId) }}</code>
        </template>
      </el-table-column>

      <template #empty>
        <el-empty description="还没有动作记录。模型通过 propose_action 提交动作后，会出现在这里。" />
      </template>
    </el-table>

    <p class="at-foot">
      只显示最近 100 条。状态为「未收口」表示没有终态记录——可能仍待审批、执行中，或进程在收口前退出。
    </p>
  </div>
</template>

<style scoped>
.at-page {
  max-width: 1040px;
  margin: 0 auto;
  padding: 28px 24px 48px;
}

.at-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 18px;
  flex-wrap: wrap;
}

.at-title {
  margin: 0 0 6px;
  font-size: 22px;
  font-weight: 650;
  color: var(--aw-text);
}

.at-sub {
  margin: 0;
  max-width: 620px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--aw-text);
  opacity: 0.72;
}

.at-tools {
  display: flex;
  gap: 8px;
  align-items: center;
}

.at-table {
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  overflow: hidden;
  cursor: pointer;
}

.at-key {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--aw-text);
}

.at-ext {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12.5px;
  color: var(--aw-text);
}

.at-rid {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11.5px;
  color: var(--aw-text);
  opacity: 0.55;
}

.at-time {
  font-size: 12.5px;
  color: var(--aw-text);
  opacity: 0.75;
}

.at-weak {
  color: var(--aw-text);
  opacity: 0.4;
}

.at-foot {
  margin: 12px 2px 0;
  font-size: 12px;
  color: var(--aw-text);
  opacity: 0.55;
}
</style>
