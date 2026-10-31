<script setup lang="ts">
import { ChatDotSquare, Delete } from '@element-plus/icons-vue'
import { ElMessageBox } from 'element-plus'
import { useAgentStore } from '@/stores/agent'

const agent = useAgentStore()

async function onDelete(id: string, title: string): Promise<void> {
  try {
    await ElMessageBox.confirm(`删除会话「${title}」？删除后不可恢复。`, '确认删除', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return // 用户取消
  }
  await agent.deleteConversation(id)
}
</script>

<template>
  <div class="conv-list">
    <div v-if="agent.conversations.length === 0" class="conv-empty">还没有会话，发条消息就开始一个</div>
    <div
      v-for="c in agent.conversations"
      :key="c.id"
      class="conv-item"
      :class="{ active: c.id === agent.conversationId }"
      role="button"
      tabindex="0"
      :title="agent.titleFor(c)"
      @click="agent.selectConversation(c.id)"
      @keydown.enter="agent.selectConversation(c.id)"
    >
      <el-icon class="conv-icon"><ChatDotSquare /></el-icon>
      <span class="conv-title">{{ agent.titleFor(c) }}</span>
      <button
        class="conv-del"
        title="删除会话"
        @click.stop="onDelete(c.id, agent.titleFor(c))"
      >
        <el-icon><Delete /></el-icon>
      </button>
    </div>
  </div>
</template>

<style scoped>
.conv-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-height: 0;
}

.conv-empty {
  padding: 10px 12px;
  font-size: 12px;
  line-height: 1.6;
  color: var(--aw-text-3);
}

.conv-item {
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 8px 10px;
  border-radius: var(--aw-radius-s);
  cursor: pointer;
  color: var(--aw-text-2);
  font-size: 13.5px;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
  animation: aw-fade-in var(--aw-dur) var(--aw-ease);
}

.conv-item:hover {
  background: var(--aw-surface-3);
  color: var(--aw-text);
}

.conv-item.active {
  background: var(--el-color-primary-light-9);
  color: var(--aw-primary);
  font-weight: 500;
}

.conv-item.active .conv-icon {
  color: var(--aw-primary);
}

.conv-icon {
  flex: none;
  font-size: 15px;
  color: var(--aw-text-3);
}

.conv-item.active .conv-icon,
.conv-item:hover .conv-icon {
  color: inherit;
}

.conv-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conv-del {
  flex: none;
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--aw-text-3);
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.conv-item:hover .conv-del {
  opacity: 1;
}

.conv-del:hover {
  color: var(--aw-danger);
  background: var(--el-color-danger-light-9);
}
</style>
