<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import * as signalR from '@microsoft/signalr'
import { ArrowLeft, Refresh, RefreshRight, VideoPlay, VideoPause } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { workflowApi, type WorkflowRun, type WorkflowStep } from '@/api/workflow'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const route = useRoute()
const router = useRouter()
const id = Number(route.params.id)

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const run = ref<WorkflowRun | null>(null)
const loading = ref(false)
let timer: number | undefined
let connection: signalR.HubConnection | undefined

const steps = computed<WorkflowStep[]>(() => run.value?.steps ?? [])
const active = computed(() => run.value?.status === 'pending' || run.value?.status === 'running')

const runStatusType: Record<string, string> = {
  pending: 'info',
  running: 'warning',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'info',
}

const stepStatusType: Record<string, string> = {
  pending: 'info',
  running: 'warning',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'info',
  stale: 'warning',
  skipped: 'info',
}

const stepStatusText: Record<string, string> = {
  pending: '待执行',
  running: '执行中',
  succeeded: '已完成',
  failed: '失败',
  cancelled: '已取消',
  stale: '待重算',
  skipped: '跳过',
}

const stepTitle: Record<string, string> = {
  agent: '结论（Agent）',
  report: '汇总报告',
}

async function load() {
  loading.value = true
  try {
    run.value = await workflowApi.run(id)
  } catch (e) {
    ElMessage.error('加载失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

function schedule() {
  window.clearTimeout(timer)
  if (!active.value) return
  timer = window.setTimeout(async () => {
    await load()
    schedule()
  }, 1500)
}

async function connectProgress() {
  try {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/workflow')
      .withAutomaticReconnect()
      .build()
    conn.on('ReceiveWorkflowProgress', () => {
      void load()
    })
    await conn.start()
    await conn.invoke('JoinRun', id)
    connection = conn
  } catch {
    // 进度通道不可用时退回轮询
  }
}

onMounted(async () => {
  await load()
  startHealthCheck()
  schedule()
  void connectProgress()
})

onUnmounted(() => {
  window.clearTimeout(timer)
  void connection?.stop()
})

async function rerunStep(step: WorkflowStep) {
  await workflowApi.rerunStep(id, step.stepKey)
  ElMessage.success(`已重跑步骤：${step.stepKey}`)
  await load()
  schedule()
}

async function cancel() {
  await ElMessageBox.confirm(`取消工作流运行 #${id}？`, '确认', { type: 'warning' })
  await workflowApi.cancel(id)
  await load()
  schedule()
}

async function resume() {
  await workflowApi.resume(id)
  ElMessage.success('已从检查点恢复')
  await load()
  schedule()
}

function statusTag(status: string) {
  return runStatusType[status] ?? 'info'
}

function stepTag(status: string) {
  return stepStatusType[status] ?? 'info'
}

function stepText(status: string) {
  return stepStatusText[status] ?? status
}

function statusText(status: string) {
  return (
    {
      pending: '排队中',
      running: '执行中',
      succeeded: '已完成',
      failed: '失败',
      cancelled: '已取消',
    }[status] ?? status
  )
}
</script>

<template>
  <div class="detail" v-loading="loading">
    <div class="toolbar">
      <el-button :icon="ArrowLeft" text @click="router.push('/workflow')">返回</el-button>
      <el-tag effect="light">工作流 #{{ id }}</el-tag>
      <el-tag v-if="run" effect="light">{{ run.workflowKey }}</el-tag>
      <el-tag v-if="run?.promptVersion" effect="plain">提示词 {{ run.promptVersion }}</el-tag>
      <el-tag v-if="run" :type="statusTag(run.status)" effect="light">{{ statusText(run.status) }}</el-tag>
      <span class="spacer" />
      <BackendStatus
        :status="backendHealth.status"
        :last-check="backendHealth.lastCheck"
        :error="backendHealth.error"
        :model-configured="backendHealth.modelConfigured"
        :show-last-check="true"
        size="small"
      />
      <el-button :icon="Refresh" text @click="load">刷新</el-button>
      <el-button v-if="active" :icon="VideoPause" text type="danger" @click="cancel">取消</el-button>
      <el-button v-if="!active && (run?.status === 'failed' || run?.status === 'cancelled')" :icon="VideoPlay" text @click="resume">
        从检查点恢复
      </el-button>
    </div>

    <el-card v-if="run?.error" shadow="never">
      <el-alert :title="run.error" type="error" :closable="false" />
    </el-card>

    <el-card shadow="never">
      <template #header>步骤（检查点）</template>
      <el-steps v-if="steps.length" :active="steps.filter((s) => s.status === 'succeeded').length" align-center class="steps">
        <el-step
          v-for="s in steps"
          :key="s.stepKey"
          :title="stepTitle[s.stepType] ?? s.stepKey"
          :description="stepText(s.status)"
          :status="s.status === 'succeeded' ? 'success' : s.status === 'running' ? 'process' : s.status === 'failed' ? 'error' : 'wait'"
        />
      </el-steps>
    </el-card>

    <el-card shadow="never">
      <template #header>步骤明细</template>
      <el-table :data="steps" row-key="stepKey" empty-text="暂无步骤">
        <el-table-column type="expand">
          <template #default="{ row }">
            <div class="expand">
              <div class="kv"><span class="k">类型</span><span>{{ row.stepType }}</span></div>
              <div class="kv"><span class="k">尝试次数</span><span>{{ row.attempts }}</span></div>
              <div class="kv"><span class="k">开始</span><span>{{ row.startedAt ?? '-' }}</span></div>
              <div class="kv"><span class="k">结束</span><span>{{ row.finishedAt ?? '-' }}</span></div>
              <div v-if="row.error" class="kv"><span class="k">错误</span><span class="err">{{ row.error }}</span></div>
              <pre v-if="row.output" class="output">{{ row.output }}</pre>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="orderIndex" label="#" width="60" />
        <el-table-column label="步骤" width="180">
          <template #default="{ row }">{{ stepTitle[row.stepType] ?? row.stepKey }}（{{ row.stepKey }}）</template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="stepTag(row.status)" effect="light">{{ stepText(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="attempts" label="尝试" width="70" />
        <el-table-column prop="startedAt" label="开始" width="180" show-overflow-tooltip />
        <el-table-column prop="finishedAt" label="结束" width="180" show-overflow-tooltip />
        <el-table-column label="操作" width="110">
          <template #default="{ row }">
            <el-button :icon="RefreshRight" text type="primary" :disabled="active" @click="rerunStep(row)">重跑</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.detail {
  max-width: 1100px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow-y: auto;
  height: 100%;
  padding-bottom: 16px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.spacer {
  flex: 1;
}

.steps {
  padding: 8px 0;
}

.expand {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 12px;
}

.kv {
  display: flex;
  gap: 8px;
  font-size: 13px;
}

.k {
  color: #909399;
  min-width: 64px;
}

.err {
  color: #f56c6c;
}

.output {
  margin: 8px 0 0;
  padding: 10px;
  background: #f7f8fa;
  border-radius: 6px;
  max-height: 260px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
}
</style>
