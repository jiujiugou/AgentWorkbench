<script setup lang="ts">
import { computed } from 'vue'
import { RouterView, useRoute } from 'vue-router'

const route = useRoute()
const active = computed(() => {
  const path = route.path
  if (path.startsWith('/documents')) return '/documents'
  if (path.startsWith('/workflow')) return '/workflow'
  return path
})
</script>

<template>
  <el-container class="app">
    <el-header class="app-header">
      <div class="brand">
        <span class="dot" />
        AgentWorkbench
      </div>
      <el-menu :default-active="active" mode="horizontal" router :ellipsis="false" class="nav">
        <el-menu-item index="/">对话</el-menu-item>
        <el-menu-item index="/documents">文档</el-menu-item>
        <el-menu-item index="/knowledge">知识库</el-menu-item>
        <el-menu-item index="/workflow">工作流</el-menu-item>
        <el-menu-item index="/settings">设置</el-menu-item>
      </el-menu>
    </el-header>
    <el-main class="app-main">
      <RouterView />
    </el-main>
  </el-container>
</template>

<style scoped>
.app {
  height: 100%;
}

.app-header {
  display: flex;
  align-items: center;
  gap: 24px;
  background: #fff;
  border-bottom: 1px solid #e5e7eb;
  padding: 0 20px;
}

.brand {
  font-weight: 600;
  font-size: 16px;
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: linear-gradient(135deg, #409eff, #7c3aed);
}

.nav {
  border-bottom: none;
  flex: 1;
}

.app-main {
  padding: 16px;
  height: calc(100vh - 60px);
  overflow: hidden;
}
</style>
