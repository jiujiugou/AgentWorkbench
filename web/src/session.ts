/**
 * 会话失效的接线：401 → 清本地状态 → 去登录页。
 *
 * 单独成一个模块，是为了让它**能被测**：写在 `main.ts` 里的话，任何针对
 * 「401 之后界面做了什么」的断言都得先挂载整个应用，而那种测试最后只会
 * 断言「`onUnauthorized` 被调用过」——它证明的是接线存在，不是接线对。
 *
 * 这里做两件事，顺序不能换：
 * 1. 清掉本地登录态。**必须在跳转之前**：路由守卫读的就是它，先跳后清的话，
 *    守卫会把这次跳转又当成「已登录的合法导航」再放回来。
 * 2. 跳到登录页，并把当前地址塞进 `redirect`——登录成功后回到原来的位置，
 *    而不是让人从首页重新找一遍。
 */

import type { Router } from 'vue-router'

import { forgetSession } from './api/auth'
import { onPasswordChangeRequired, onUnauthorized } from './api/http'

export function installSessionExpiry(router: Router): void {
  onUnauthorized(() => {
    forgetSession()
    const current = router.currentRoute.value
    // 已经在登录页就不再跳：重复 push 同一个地址会在历史里堆一串，
    // 用户按「后退」会看到自己刚离开的那一页闪一下又被弹回来。
    if (current.name === 'login') return
    void router.push({ path: '/login', query: { redirect: current.fullPath } })
  })

  onPasswordChangeRequired(() => {
    // 首登改密不是会话失效：**不清登录态**，否则改密页自己也会 401。
    const current = router.currentRoute.value
    if (current.name === 'change-password') return
    void router.push({
      path: '/change-password',
      query: { redirect: current.fullPath },
    })
  })
}
