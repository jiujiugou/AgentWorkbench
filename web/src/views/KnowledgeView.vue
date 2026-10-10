<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Delete, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  knowledgeApi,
  type KnowledgeDocument,
  type KnowledgeStatus,
  type RetrievalSource,
  type Rule,
} from '@/api/knowledge'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const router = useRouter()

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const status = ref<KnowledgeStatus | null>(null)
const documents = ref<KnowledgeDocument[]>([])
const rules = ref<Rule[]>([])
const loading = ref(false)

const query = ref('')
const topK = ref(5)
const searching = ref(false)
const results = ref<RetrievalSource[]>([])

const newRule = ref({ category: '标准条款', name: '', body: '' })

async function load() {
  loading.value = true
  try {
    const [s, docs, ruleList] = await Promise.all([
      knowledgeApi.health(),
      knowledgeApi.documents(),
      knowledgeApi.rules(),
    ])
    status.value = s
    documents.value = docs
    rules.value = ruleList
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

async function runSearch() {
  if (!query.value.trim()) return
  searching.value = true
  try {
    results.value = await knowledgeApi.retrieve(query.value.trim(), topK.value)
  } catch (e) {
    ElMessage.error('检索失败：' + (e as Error).message)
  } finally {
    searching.value = false
  }
}

async function reindex(row: KnowledgeDocument) {
  await knowledgeApi.reindex(row.id)
  ElMessage.success('已重建索引')
  await load()
}

function openSource(source: RetrievalSource) {
  void router.push(`/documents/${source.documentId}?chunk=${source.seq}`)
}

async function addRule() {
  if (!newRule.value.name.trim()) {
    ElMessage.warning('请填写规则名称')
    return
  }
  await knowledgeApi.addRule(newRule.value.category, newRule.value.name, newRule.value.body)
  newRule.value = { category: '标准条款', name: '', body: '' }
  ElMessage.success('已保存')
  rules.value = await knowledgeApi.rules()
}

async function removeRule(row: Rule) {
  await ElMessageBox.confirm(`删除规则「${row.name}」？`, '确认', { type: 'warning' })
  await knowledgeApi.removeRule(row.id)
  rules.value = await knowledgeApi.rules()
}

function score(value?: number | null) {
  return value === null || value === undefined ? '-' : value.toFixed(4)
}
</script>

<template>
  <div class="knowledge" v-loading="loading">
    <el-card shadow="never">
      <template #header>
        <div class="header">
          <span>知识库 · 引擎状态</span>
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
      <div class="tags" v-if="status">
        <el-tag effect="light">引擎：{{ status.provider }}</el-tag>
        <el-tag :type="status.embeddingConfigured ? 'success' : 'info'" effect="light">
          Embedding：{{ status.embeddingProvider }} / {{ status.embeddingModel || '未配置' }}
        </el-tag>
        <el-tag effect="light">已索引块：{{ status.indexedChunkCount }}</el-tag>
        <el-tag effect="light">文档：{{ status.documentCount }}</el-tag>
        <el-tag v-if="status.capabilities.retrieve" type="success" effect="plain">检索可用</el-tag>
        <el-tag v-if="status.capabilities.ask" type="success" effect="plain">问答可用</el-tag>
      </div>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>已入库文档</template>
      <el-table :data="documents" empty-text="暂无文档">
        <el-table-column prop="fileName" label="文件名" min-width="240" show-overflow-tooltip />
        <el-table-column label="索引" width="90">
          <template #default="{ row }">
            <el-tag :type="row.indexed ? 'success' : 'info'" effect="light">
              {{ row.indexed ? '已索引' : '未索引' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="pageCount" label="页数" width="80" />
        <el-table-column prop="chunkCount" label="块数" width="80" />
        <el-table-column label="操作" width="140">
          <template #default="{ row }">
            <el-button text type="primary" @click="reindex(row)">重建索引</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>检索调试</template>
      <div class="search-bar">
        <el-input
          v-model="query"
          placeholder="输入问题，例如：违约金是多少"
          @keyup.enter="runSearch"
          clearable
        />
        <el-input-number v-model="topK" :min="1" :max="20" controls-position="right" />
        <el-button type="primary" :icon="Search" :loading="searching" @click="runSearch">检索</el-button>
      </div>

      <el-empty v-if="results.length === 0" description="暂无命中" />
      <div v-else class="hits">
        <article v-for="(hit, index) in results" :key="hit.chunkId" class="hit">
          <div class="hit-head">
            <span class="rank">#{{ index + 1 }}</span>
            <el-link type="primary" @click="openSource(hit)">
              《{{ hit.fileName }}》· 片段#{{ hit.seq }}
            </el-link>
            <span v-if="hit.heading" class="heading">{{ hit.heading }}</span>
            <span class="scores">
              p{{ hit.pageFrom }}–{{ hit.pageTo }} · 融合 {{ score(hit.fusedScore) }} · 向量
              {{ score(hit.vectorScore) }} · 全文 {{ score(hit.textScore) }}
            </span>
          </div>
          <pre class="hit-text">{{ hit.content }}</pre>
        </article>
      </div>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>规则库（P1 占位，内容 P2 填充）</template>
      <div class="rule-form">
        <el-input v-model="newRule.category" placeholder="分类" style="width: 160px" />
        <el-input v-model="newRule.name" placeholder="规则名称" style="width: 240px" />
        <el-input v-model="newRule.body" placeholder="规则内容" />
        <el-button type="primary" @click="addRule">新增</el-button>
      </div>
      <el-table :data="rules" empty-text="暂无规则">
        <el-table-column prop="category" label="分类" width="140" />
        <el-table-column prop="name" label="名称" width="220" />
        <el-table-column prop="body" label="内容" show-overflow-tooltip />
        <el-table-column label="操作" width="90">
          <template #default="{ row }">
            <el-button :icon="Delete" text type="danger" @click="removeRule(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.knowledge {
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
  align-items: center;
}

.block {
  flex: none;
}

.search-bar {
  display: flex;
  gap: 10px;
  margin-bottom: 12px;
}

.search-bar .el-input {
  flex: 1;
}

.hits {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.hit {
  border: 1px solid #eef0f3;
  border-radius: 6px;
  padding: 8px 10px;
}

.hit-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 13px;
  margin-bottom: 6px;
}

.rank {
  color: #409eff;
  font-weight: 600;
}

.heading {
  font-weight: 600;
}

.scores {
  color: #909399;
  margin-left: auto;
}

.hit-text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: "Microsoft YaHei", monospace;
  font-size: 13px;
  line-height: 1.7;
  color: #303133;
  max-height: 200px;
  overflow-y: auto;
}

.rule-form {
  display: flex;
  gap: 10px;
  margin-bottom: 12px;
}
</style>
