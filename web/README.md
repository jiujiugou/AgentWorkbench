# AgentWorkbench 前端（web）

Vue 3 + Vite + TypeScript + Element Plus，通过 `/api`（HTTP）与 `/hubs/agent`（SignalR）与后端交互。

## 功能

| 文件 | 说明 |
| --- | --- |
| `src/views/ChatView.vue` | 流式对话（普通对话 / RAG 问答） |
| `src/views/DocumentsView.vue` | 文档上传与列表（P0） |
| `src/views/DocumentDetailView.vue` | 文档解析结果预览：每页 raw + cleaned、切分块 |
| `src/views/KnowledgeView.vue` | 知识库：检索 / 带引用问答 / 规则管理（P1） |
| `src/views/SettingsView.vue` | 供应商与模型在线配置、连通测试 |
| `src/composables/useAgent.ts` | SignalR 对话封装（发送、接收增量、停止） |
| `src/api/documents.ts` | 文档接口请求封装 |
| `src/api/knowledge.ts` | 知识库接口请求封装 |
| `src/api/settings.ts` | 设置接口请求封装 |
| `src/utils/markdown.ts` | Markdown 渲染工具 |
| `src/router/index.ts` | 路由 |
| `src/App.vue` / `src/main.ts` | 应用入口与根组件 |

## 开发与构建

```bash
cd web
npm install
npm run dev      # http://localhost:5173，/api 与 /hubs 代理到 http://localhost:8080
npm run build    # 产物输出到 web/dist
```

> 生产构建产物 `web/dist` 由 Dockerfile 拷入后端 `wwwroot` 提供静态服务。

本模板基于 Vue 3 + TypeScript + Vite；`<script setup>` SFC 用法见 [script setup docs](https://v3.vuejs.org/api/sfc-script-setup.html)，项目配置与 IDE 支持见 [Vue Docs TypeScript Guide](https://vuejs.org/guide/typescript/overview.html#project-setup)。
