<script setup lang="ts">
import { computed } from 'vue'
import { ElTooltip, ElTag } from 'element-plus'
import { Connection, Monitor, CircleCheck, CircleClose, Loading } from '@element-plus/icons-vue'
import type { BackendHealthStatus } from '@/composables/useBackendHealth'

interface Props {
  status: BackendHealthStatus
  lastCheck: Date | null
  error: string | null
  modelConfigured: boolean
  showModelConfigured?: boolean
  showLastCheck?: boolean
  size?: 'default' | 'small' | 'large'
}

const props = withDefaults(defineProps<Props>(), {
  showModelConfigured: true,
  showLastCheck: false,
  size: 'default',
})

const statusType = computed(() => {
  switch (props.status) {
    case 'connected': return 'success'
    case 'connecting': return 'warning'
    case 'error': return 'danger'
    default: return 'info'
  }
})

const statusText = computed(() => {
  switch (props.status) {
    case 'connected': return '已连接'
    case 'connecting': return '连接中…'
    case 'error': return '连接失败'
    default: return '未连接'
  }
})

const statusIcon = computed(() => {
  switch (props.status) {
    case 'connected': return CircleCheck
    case 'connecting': return Loading
    case 'error': return CircleClose
    default: return Monitor
  }
})

const tooltipContent = computed(() => {
  const lines = [
    `状态：${statusText.value}`,
    props.lastCheck ? `最近检查：${props.lastCheck.toLocaleTimeString()}` : '最近检查：—',
  ]
  if (props.error) lines.push(`错误：${props.error}`)
  if (props.showModelConfigured) lines.push(`模型配置：${props.modelConfigured ? '已配置' : '未配置'}`)
  return lines.join('\n')
})
</script>

<template>
  <el-tooltip :content="tooltipContent" placement="top" effect="light">
    <el-tag
      :type="statusType"
      :size="size"
      effect="light"
      class="backend-status-tag"
      style="display: inline-flex; align-items: center; gap: 4px; cursor: default;"
    >
      <component :is="statusIcon" style="animation: spin 1s linear infinite;" v-if="status === 'connecting'" />
      <component :is="statusIcon" v-else />
      <span>{{ statusText }}</span>
      <component :is="Connection" v-if="showModelConfigured && modelConfigured" style="margin-left: 4px;" />
    </el-tag>
  </el-tooltip>
</template>

<style scoped>
@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
.backend-status-tag {
  white-space: nowrap;
}
.backend-status-tag :deep(.el-tag__content) {
  display: flex;
  align-items: center;
  gap: 4px;
}
</style>