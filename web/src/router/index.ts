import { createRouter, createWebHistory } from 'vue-router'
import { cachedSignedIn } from '@/api/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'documents', component: () => import('@/views/DocumentsView.vue') },
    { path: '/chat', name: 'chat', component: () => import('@/views/ChatView.vue') },
    { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
  ],
})

// 未登录一律去 /login。登录态查一次后缓存，由 login()/logout() 同步更新。
router.beforeEach(async (to) => {
  if (to.meta.public) return true
  const signedIn = await cachedSignedIn()
  if (signedIn) return true
  return { path: '/login', query: { redirect: to.fullPath } }
})

export default router
