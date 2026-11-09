import { createRouter, createWebHistory } from 'vue-router'
import { cachedUser } from '@/api/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'documents', component: () => import('@/views/DocumentsView.vue') },
    { path: '/chat', name: 'chat', component: () => import('@/views/ChatView.vue') },
    { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
    {
      path: '/admin',
      name: 'admin',
      component: () => import('@/views/AdminView.vue'),
      meta: { admin: true },
    },
    {
      path: '/invite/:token',
      name: 'invite',
      component: () => import('@/views/InviteView.vue'),
      meta: { public: true },
    },
    {
      path: '/change-password',
      name: 'change-password',
      component: () => import('@/views/ChangePasswordView.vue'),
    },
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
  ],
})

// 未登录一律去 /login。登录态查一次后缓存，由 login()/logout() 同步更新。
// 已登录但还没改初始口令 → 只允许待在改密页；这是服务端 403 的界面侧镜像，
// 不替代服务端判定（直接调 API 仍然会被拦）。
router.beforeEach(async (to) => {
  if (to.meta.public) return true
  const user = await cachedUser()
  if (!user) return { path: '/login', query: { redirect: to.fullPath } }
  if (user.mustChangePassword && to.name !== 'change-password') {
    return { path: '/change-password', query: { redirect: to.fullPath } }
  }
  if (to.meta.admin === true && user.role !== 'owner' && user.role !== 'admin') return { path: '/' }
  return true
})

export default router
