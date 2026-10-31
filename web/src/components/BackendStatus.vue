<script setup lang="ts">
import { computed } from 'vue'
import type { BackendHealthStatus } from '@/stores/health'

interface Props {
  status: BackendHealthStatus
  lastCheck?: Date | null
  error?: string | null
  modelConfigured?: boolean
  showLastCheck?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  lastCheck: null,
  error: null,
  modelConfigured: false,
  showLastCheck: false,
})

const statusText = computed(() => {
  switch (props.status) {
    case 'connected':
      return '已连接'
    case 'connecting':
      return '连接中'
    case 'error':
      return '连接失败'
    default:
      return '未连接'
  }
})

const dotClass = computed(() => {
  switch (props.status) {
    case 'connected':
      return 'dot-ok'
    case 'connecting':
      return 'dot-wait'
    case 'error':
      return 'dot-bad'
    default:
      return 'dot-off'
  }
})

const tooltip = computed(() => {
  const lines = [`服务：${statusText.value}`]
  if (props.showLastCheck && props.lastCheck) lines.push(`检查于 ${props.lastCheck.toLocaleTimeString()}`)
  if (props.error) lines.push(`错误：${props.error}`)
  lines.push(`模型：${props.modelConfigured ? '已配置' : '未配置'}`)
  return lines.join('\n')
})
</script>

<template>
  <el-tooltip :content="tooltip" placement="top" effect="dark" :show-after="300">
    <span class="backend-status">
      <span class="dot" :class="dotClass" />
      <span class="text">{{ statusText }}</span>
    </span>
  </el-tooltip>
</template>

<style scoped>
.backend-status {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--aw-text-2);
  cursor: default;
  white-space: nowrap;
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
  transition: background-color var(--aw-dur) var(--aw-ease), box-shadow var(--aw-dur) var(--aw-ease);
}

.dot-ok {
  background: var(--aw-success);
  box-shadow: 0 0 0 3px rgb(16 185 129 / 0.16);
}

.dot-wait {
  background: var(--aw-warning);
  animation: aw-pulse 1.2s ease-in-out infinite;
}

.dot-bad {
  background: var(--aw-danger);
  box-shadow: 0 0 0 3px rgb(239 68 68 / 0.16);
}

.dot-off {
  background: var(--aw-text-3);
}

@keyframes aw-pulse {
  50% {
    opacity: 0.35;
  }
}
</style>
