/**
 * 身份 · /api/auth（Cookie 认证）。
 *
 * 契约由后端 `api/routes/identity.py` 定：登录 `POST {email, password}` →
 * `{id, email, role, status, tenantId, levelRank}`；`GET /me` 未登录是 401。
 *
 * **这里曾经有一个「这个后端没有身份机制」的占位用户**：`/me` 返回 404 时
 * 伪造一个 `local` 身份放行。它在 F1 落地之前是对的（那时确实没有身份端点），
 * 落地之后就成了一个洞——任何一次部署错误（反代把 `/api/auth/*` 吃掉）
 * 都会静默变成「所有人都是本机管理员」，而界面上一切正常。
 * 现在 404 就是一个普通的失败：说不清就说不清，别假装登录着。
 */

import { handle, toApiError } from './http'

export interface AuthUser {
  // C# AuthController.ToViewAsync 返回这些
  userId: string
  userName: string
  displayName: string
  tenantName?: string | null
  role: string
  tenantId: string
  // 以下不是 C# 后端返回的字段，是 v1（Python 侧）契约的残留。
  // 标可选是为了让编译通过，不代表它们有值——用到之前先确认后端真的返回。
  id?: string
  email?: string
  status?: string
  mustChangePassword?: boolean
}

// 登录态缓存：null = 还没查过。登录/登出必须同步改它，否则路由守卫会用旧结果。
let signedIn: boolean | null = null
let me: AuthUser | null = null
let pending: Promise<AuthUser | null> | null = null

/**
 * 当前登录用户。**界面用它决定「显示哪些按钮」，后端用它决定「放不放行」——
 * 两边都要有，缺一边就是 U-19 那种形态：按钮在，接口也认。**
 *
 * 缓存的是整份用户而不是一个布尔：`/me` 本来就回 `role`，再发一次请求只为读
 * 一个字段，会在每次进页面时多打一次后端，而结果只用来藏一个按钮。
 */
export function cachedUser(): Promise<AuthUser | null> {
  if (signedIn === false) return Promise.resolve(null)
  if (signedIn === true) return Promise.resolve(me)
  pending ??= fetchMe().then((user) => {
    me = user
    signedIn = user !== null
    return user
  })
  return pending
}

export function cachedSignedIn(): Promise<boolean> {
  return cachedUser().then((user) => user !== null)
}

/**
 * 忘掉本地登录态（不调后端）。**由 401 的全局处理调用。**
 *
 * 它把状态退回「还没查过」，而不是钉成「已确定未登录」：401 只说明**这一次**
 * 请求的票不行了，而下一次问的时候答案可能已经变了（用户在另一个标签页里重新登录）。
 * 钉死成 `false` 的版本会让那个标签页再也进不来，直到整个页面刷新——
 * 而现象是「明明登录了，这里是空白页」。
 */
export function forgetSession(): void {
  signedIn = null
  me = null
  pending = null
}

async function fetchMe(): Promise<AuthUser | null> {
  let response: Response
  try {
    response = await fetch('/api/auth/me')
  } catch {
    // 网络层就没通（后端没起）：当作未登录，让守卫把用户送到登录页——
    // 那里会把「连不上」显示出来，比停在一个永远空白的首页强。
    return null
  }

  // 401 是正常路径（还没登录），不是异常：**不走 `handle`**，
  // 否则它会触发全局跳转，而这里正是判断「要不要跳」的地方。
  if (response.status === 401) return null
  if (!response.ok) return null
  return handle<AuthUser>(response)
}

export async function login(email: string, password: string): Promise<AuthUser> {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    // C# LoginRequest 是 {userName, password}；参数名保留 email 不动调用方
    body: JSON.stringify({ userName: email, password }),
  })
  if (!response.ok) {
    // 登录失败也不走 `handle`：它的 401 意思是「口令不对」，
    // 不是「会话过期」，触发全局跳转没有意义（已经在登录页了）。
    throw await toApiError(response)
  }
  const user = await handle<AuthUser>(response)
  signedIn = true
  me = user
  pending = null
  return user
}

export async function logout(): Promise<void> {
  await handle<void>(await fetch('/api/auth/logout', { method: 'POST' }))
  // 先算注销再改本地状态：网络失败时票还在，界面却显示未登录的话，
  // 下一个请求带着有效的票、界面却让人再登一次。
  signedIn = false
  me = null
  pending = null
}

/**
 * 本人改密。**不走 `handle`**：旧口令错的响应也是 401，而 `handle` 会把它
 * 当成“会话过期”清登录态、跳登录页——用户只是输错了旧口令，却被踢出去。
 *
 * 成功后同步清掉本地标记：改密页随后会调 `restore()`，路由守卫不能还用旧值
 * 把人弹回改密页。
 */
export async function changePassword(oldPassword: string, newPassword: string): Promise<void> {
  const response = await fetch('/api/auth/password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ oldPassword, newPassword }),
  })
  if (!response.ok) throw await toApiError(response)
  if (me) me = { ...me, mustChangePassword: false }
}
