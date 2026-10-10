<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Delete, Refresh, VideoPlay } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { knowledgeApi } from '@/api/knowledge'
import { workflowApi, type PromptVersion, type WorkflowCapabilities, type WorkflowDefinition, type WorkflowRun } from '@/api/workflow'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const router = useRouter()

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const PROMPT_KEY = 'workflow.answer'

const capabilities = ref<WorkflowCapabilities | null>(null)
const documents = ref<{ id: string; fileName: string; status: string; chunkCount: number }[]>([])
const definitions = ref<WorkflowDefinition[]>([])
const prompts = ref<PromptVersion[]>([])
const runs = ref<WorkflowRun[]>([])
const loading = ref(false)
const submitting = ref(false)

const documentId = ref('')
const workflowKey = ref('')
const promptVersion = ref('')

const newVersion = ref('')
const newContent = ref('')

const availableDocuments = computed(() => documents.value.filter((d) => d.status === 'parsed' && d.chunkCount > 0))

const statusType: Record<string, string> = {
  pending: 'info',
  running: 'warning',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'info',
}

const statusText: Record<string, string> = {
  pending: '排队中',
  running: '执行中',
  succeeded: '已完成',
  failed: '失败',
  cancelled: '已取消',
}

async function load() {
  loading.value = true
  try {
    const [caps, docs, defs, ps, list] = await Promise.all([
      workflowApi.health(),
      knowledgeApi.documents(),
      workflowApi.definitions(),
      workflowApi.prompts(PROMPT_KEY),
      workflowApi.runs(),
    ])
    capabilities.value = caps
    documents.value = docs
    definitions.value = defs
    prompts.value = ps
    runs.value = list
    if (!workflowKey.value && defs.length) workflowKey.value = defs[0].key
  } catch (e) {
    ElMessage.error('加载失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  startHealthCheck()
})

async function startRun() {
  if (!documentId.value) {
    ElMessage.warning('请选择要处理的文档')
    return
  }
  submitting.value = true
  try {
    const { runId } = await workflowApi.create(documentId.value, workflowKey.value || undefined, promptVersion.value || undefined)
    ElMessage.success('已创建工作流运行')
    void router.push(`/workflow/${runId}`)
  } catch (e) {
    ElMessage.error('创建失败：' + (e as Error).message)
  } finally {
    submitting.value = false
  }
}

function openRun(row: WorkflowRun) {
  void router.push(`/workflow/${row.id}`)
}

async function remove(row: WorkflowRun) {
  await ElMessageBox.confirm(`删除工作流运行 #${row.id}？`, '确认', { type: 'warning' })
  await workflowApi.remove(row.id)
  await load()
}

async function activate(p: PromptVersion) {
  if (p.active === 1) return
  await workflowApi.activatePrompt(p.promptKey, p.version)
  ElMessage.success(`已设为默认：${p.version}`)
  await load()
}

async function addPrompt() {
  if (!newVersion.value.trim() || !newContent.value.trim()) {
    ElMessage.warning('请填写版本号与内容')
    return
  }
  try {
    await workflowApi.addPrompt(PROMPT_KEY, newVersion.value.trim(), newContent.value.trim(), true)
    ElMessage.success('已新增提示词版本并设为默认')
    newVersion.value = ''
    newContent.value = ''
    await load()
  } catch (e) {
    ElMessage.error('新增失败：' + (e as Error).message)
  }
}
</script>

<template>
  <div class="workflow" v-loading="loading">
    <el-card shadow="never">
      <template #header>
        <div class="header">
          <span>工作流 · Workflow Loop（P4）</span>
          <BackendStatus
            :status="backendHealth.status"
            :last-check="backendHealth.lastCheck"
            :error="backendHealth.error"
            :model-configured="backendHealth.modelConfigured"
            :show-last-check="true"
            size="small"
          />
          <el-button :icon="Refresh" text @click="load">刷新</el-button>
        </div>
      </template>
      <div class="tags" v-if="capabilities">
        <el-tag type="success" effect="plain">工作流已启用</el-tag>
        <el-tag v-for="s in capabilities.steps" :key="s" type="info" effect="plain">{{ s }}</el-tag>
      </div>

      <div class="form">
        <el-select v-model="documentId" placeholder="选择已解析文档" filterable style="width: 300px">
          <el-option v-for="d in availableDocuments" :key="d.id" :label="d.fileName" :value="d.id" />
        </el-select>
        <el-select v-model="workflowKey" placeholder="工作流" style="width: 200px">
          <el-option v-for="d in definitions" :key="d.key" :label="d.name" :value="d.key" />
        </el-select>
        <el-select v-model="promptVersion" placeholder="提示词版本（默认）" clearable style="width: 200px">
          <el-option v-for="p in prompts" :key="p.version" :label="p.active === 1 ? `${p.version}（默认）` : p.version" :value="p.version" />
        </el-select>
        <el-button type="primary" :icon="VideoPlay" :loading="submitting" @click="startRun">运行工作流</el-button>
      </div>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>提示词版本（{{ PROMPT_KEY }}）</template>
      <el-table :data="prompts" empty-text="暂无版本">
        <el-table-column prop="version" label="版本" width="160" />
        <el-table-column prop="content" label="内容" min-width="260" show-overflow-tooltip />
        <el-table-column label="默认" width="90">
          <template #default="{ row }">
            <el-tag v-if="row.active === 1" type="success" effect="light">默认</el-tag>
            <el-button v-else text type="primary" @click="activate(row)">设为默认</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div class="form prompt-add">
        <el-input v-model="newVersion" placeholder="版本号，如 base-0.2" style="width: 200px" />
        <el-input v-model="newContent" placeholder="提示词内容" style="width: 420px" />
        <el-button @click="addPrompt">新增版本（设为默认）</el-button>
      </div>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>工作流运行</template>
      <el-table :data="runs" empty-text="暂无运行记录">
        <el-table-column prop="id" label="#" width="70" />
        <el-table-column prop="workflowKey" label="工作流" width="160" />
        <el-table-column prop="documentId" label="文档" min-width="220" show-overflow-tooltip />
        <el-table-column prop="promptVersion" label="提示词版本" width="150" />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="statusType[row.status] ?? 'info'" effect="light">{{ statusText[row.status] ?? row.status }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="步骤" width="110">
          <template #default="{ row }">
            {{ row.steps.filter((s: { status: string }) => s.status === 'succeeded').length }}/{{ row.steps.length }}
          </template>
        </el-table-column>
        <el-table-column prop="latencyMs" label="耗时(ms)" width="100" />
        <el-table-column prop="createdAt" label="时间" width="200" show-overflow-tooltip />
        <el-table-column label="操作" width="160">
          <template #default="{ row }">
            <el-button text type="primary" @click="openRun(row)">查看</el-button>
            <el-button :icon="Delete" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.workflow {
  max-width: 1100px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow-y: auto;
  height: 100%;
  padding-bottom: 16px;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}

.form {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
  align-items: center;
}

.prompt-add {
  margin-top: 12px;
}

.block {
  flex: none;
}
</style>
