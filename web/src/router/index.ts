import { createRouter, createWebHistory } from 'vue-router'
import { cachedUser } from '@/api/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    // 首页是对话，不是资料。产品的主语是「说一句话 → 系统判断该读、该懂还是该改」，
    // 上传文档只是让检索有东西可查的前置准备，属部署期行为（入口在设置页）。
    { path: '/', name: 'chat', component: () => import('@/views/ChatView.vue') },
    // 旧书签与旧链接仍然指向 /chat，重定向而不是报 404。
    { path: '/chat', redirect: '/' },
    { path: '/documents', name: 'documents', component: () => import('@/views/DocumentsView.vue') },
    // 审批页不设 meta.admin：列表所有登录用户可见，批准/拒绝由服务端按角色拦。
    // 界面提前收起按钮只是提示，不替代 `[Authorize(Roles="owner")]`。
    { path: '/approvals', name: 'approvals', component: () => import('@/views/ApprovalsView.vue') },
    // 追溯：产品判据②「任一动作能答谁/何时/看到什么/依据哪条规则/ERP 里变成什么」的入口。
    // 独立一级页面，不从会话里钻进去——它要能脱离某次对话回答「系统在我这里做过什么」。
    { path: '/actions', name: 'action-trace', component: () => import('@/views/ActionTraceView.vue') },
    {
      path: '/actions/:requestId',
      name: 'action-detail',
      component: () => import('@/views/ActionDetailView.vue'),
    },
    { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
    {
      path: '/admin',
      name: 'admin',
      component: () => import('@/views/AdminView.vue'),
      meta: { admin: true },
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
