<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Delete, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { reviewApi, type ReviewRule } from '@/api/review'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const rules = ref<ReviewRule[]>([])
const loading = ref(false)

const form = ref({
  category: '买卖合同',
  name: '',
  ruleType: 'clause_required',
  severity: 'medium',
  targetKey: '',
  expected: '',
  matchHint: '',
  body: '',
})

async function load() {
  loading.value = true
  try {
    rules.value = await reviewApi.rules()
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

async function add() {
  if (!form.value.name.trim()) {
    ElMessage.warning('请填写规则名称')
    return
  }
  await reviewApi.addRule({
    category: form.value.category,
    name: form.value.name,
    ruleType: form.value.ruleType,
    severity: form.value.severity,
    targetKey: form.value.targetKey || undefined,
    expected: form.value.expected || undefined,
    matchHint: form.value.matchHint || undefined,
    body: form.value.body,
  })
  ElMessage.success('已保存')
  form.value.name = ''
  form.value.targetKey = ''
  form.value.expected = ''
  form.value.matchHint = ''
  form.value.body = ''
  await load()
}

async function remove(row: ReviewRule) {
  await ElMessageBox.confirm(`删除规则「${row.name}」？`, '确认', { type: 'warning' })
  await reviewApi.removeRule(row.id)
  await load()
}
</script>

<template>
  <div class="rules" v-loading="loading">
    <el-card shadow="never">
      <template #header>
        <div class="header">
          <span>规则 / 模板库（比对依据）</span>
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

      <div class="form">
        <el-input v-model="form.category" placeholder="分类/模板" style="width: 150px" />
        <el-input v-model="form.name" placeholder="规则名称" style="width: 180px" />
        <el-select v-model="form.ruleType" style="width: 180px">
          <el-option label="应有条款" value="clause_required" />
          <el-option label="禁止条款" value="clause_forbidden" />
          <el-option label="必备要素" value="element_required" />
          <el-option label="取值规则" value="value_rule" />
        </el-select>
        <el-select v-model="form.severity" style="width: 110px">
          <el-option label="高" value="high" />
          <el-option label="中" value="medium" />
          <el-option label="低" value="low" />
          <el-option label="提示" value="info" />
        </el-select>
        <el-input v-model="form.targetKey" placeholder="目标要素 key" style="width: 150px" />
        <el-input v-model="form.expected" placeholder="期望值" style="width: 130px" />
        <el-input v-model="form.matchHint" placeholder="匹配关键词" style="width: 150px" />
        <el-input v-model="form.body" placeholder="说明" style="width: 200px" />
        <el-button type="primary" @click="add">新增</el-button>
      </div>

      <el-table :data="rules" empty-text="暂无规则">
        <el-table-column prop="category" label="分类" width="120" />
        <el-table-column prop="name" label="名称" width="160" />
        <el-table-column prop="ruleType" label="类型" width="140" />
        <el-table-column prop="severity" label="严重级" width="90" />
        <el-table-column prop="targetKey" label="目标 key" width="110" />
        <el-table-column prop="matchHint" label="关键词" width="110" />
        <el-table-column prop="body" label="说明" show-overflow-tooltip />
        <el-table-column label="操作" width="90">
          <template #default="{ row }">
            <el-button :icon="Delete" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.rules {
  max-width: 1100px;
  margin: 0 auto;
  overflow-y: auto;
  height: 100%;
  padding-bottom: 16px;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.form {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 12px;
}
</style>
