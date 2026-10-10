import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'chat', component: () => import('@/views/ChatView.vue') },
    { path: '/documents', name: 'documents', component: () => import('@/views/DocumentsView.vue') },
    { path: '/knowledge', name: 'knowledge', component: () => import('@/views/KnowledgeView.vue') },
    { path: '/workflow', name: 'workflow', component: () => import('@/views/WorkflowView.vue') },
    {
      path: '/workflow/:id',
      name: 'workflow-detail',
      component: () => import('@/views/WorkflowDetailView.vue'),
    },
    {
      path: '/documents/:id',
      name: 'document-detail',
      component: () => import('@/views/DocumentDetailView.vue'),
    },
    { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
  ],
})

export default router
