/**
 * 前端身份契约（`I-V17`）。
 *
 * 这一层的断言都在「浏览器会因此做什么」，而不是「代码长什么样」：
 * 登录发的是什么字段、401 之后界面会不会跳、空响应会不会被当成解析错误。
 *
 * 十条各自钉一个失效形态：
 * - 登录字段发错（`userName` 而不是 `email`）→ 后端 422，界面上是一句看不懂的话；
 * - `/me` 的 401 被当成「异常」而不是「没登录」→ 守卫永远放行或永远拦；
 * - 业务接口 401 不触发跳转 → 用户停在「每个操作都报错」的界面，像系统坏了；
 * - 空 body 不特判 → 登出 / 改密 / 停用（三个 204）各抛一次 `Unexpected end of JSON input`；
 * - 已经登录页还 push → 历史里堆一串 /login，按后退会闪一下又被弹回来。
 */

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Router } from 'vue-router'

import { cachedSignedIn, changePassword, forgetSession, login, logout } from '@/api/auth'
import { ApiError, handle, onUnauthorized } from '@/api/http'
import { installSessionExpiry } from '@/session'

const USER = {
  id: 'u1',
  email: 'admin@example.com',
  role: 'admin',
  status: 'active',
  tenantId: 't1',
  mustChangePassword: false,
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function empty(status: number): Response {
  return new Response(null, { status })
}

function fakeRouter(name: string, fullPath: string): { router: Router; push: ReturnType<typeof vi.fn> } {
  const push = vi.fn()
  return {
    push,
    router: { currentRoute: { value: { name, fullPath } }, push } as unknown as Router,
  }
}

let fetchMock: ReturnType<typeof vi.fn>

beforeEach(() => {
  forgetSession()
  fetchMock = vi.fn()
  vi.stubGlobal('fetch', fetchMock)
})

describe('登录', () => {
  // 发的是 email 而不是 userName：后端的字段名就是 email，写错是 422，
  // 而 422 的文案对用户毫无意义（它说的是「字段缺失」）
  it('test_login_posts_email_not_username', async () => {
    fetchMock.mockResolvedValueOnce(json(USER))

    const user = await login('admin@example.com', 'correct horse')

    expect(user.email).toBe('admin@example.com')
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/auth/login')
    expect(init.method).toBe('POST')
    expect(JSON.parse(String(init.body))).toEqual({
      email: 'admin@example.com',
      password: 'correct horse',
    })
  })

  // 登录成功后守卫不该再问一次 /me：那一次请求是纯浪费，而它失败时
  // 会把刚登录的人又弹回登录页
  it('test_login_skips_the_extra_me_probe', async () => {
    fetchMock.mockResolvedValueOnce(json(USER))
    await login('admin@example.com', 'correct horse')

    expect(await cachedSignedIn()).toBe(true)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  // 口令错时抛出后端那句文案，而不是「HTTP 401」
  it('test_wrong_password_surfaces_the_backend_message', async () => {
    fetchMock.mockResolvedValueOnce(
      json({ code: 'invalid_credentials', message: '邮箱或口令不正确。', request_id: 'r1' }, 401),
    )

    await expect(login('a@b.c', 'wrong')).rejects.toThrow('邮箱或口令不正确。')
  })

  // 登录响应里的首登标记必须原样保留，LoginView 靠它决定进哪一页
  it('test_login_returns_must_change_flag', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...USER, mustChangePassword: true }))

    const user = await login('admin@example.com', 'correct horse')

    expect(user.mustChangePassword).toBe(true)
  })
})

describe('会话失效', () => {
  // /me 的 401 表示「还没登录」，是一个正常答案，不触发跳转——
  // 这里正是判断「要不要跳」的地方，跳了就是自己跳给自己
  it('test_me_401_means_not_signed_in', async () => {
    const onFail = vi.fn()
    onUnauthorized(onFail)
    fetchMock.mockResolvedValueOnce(empty(401))

    expect(await cachedSignedIn()).toBe(false)
    expect(onFail).not.toHaveBeenCalled()
  })

  // 业务接口 401 → 清登录态 → 跳登录页，并带上原地址
  it('test_business_401_redirects_to_login_with_redirect', async () => {
    const { router, push } = fakeRouter('chat', '/chat?c=1')
    installSessionExpiry(router)

    fetchMock.mockResolvedValueOnce(json(USER))
    expect(await cachedSignedIn()).toBe(true)

    fetchMock.mockResolvedValueOnce(
      json({ code: 'not_authenticated', message: '未登录或登录已失效，请重新登录。' }, 401),
    )
    await expect(handle<unknown>(await fetchMock('/', {}))).rejects.toBeInstanceOf(ApiError)

    expect(push).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/chat?c=1' } })
    // 登录态被退回「还没查过」：下一次守卫重新问 /me，而不是继续以为登录着
    const calls = fetchMock.mock.calls.length
    fetchMock.mockResolvedValueOnce(empty(401))
    expect(await cachedSignedIn()).toBe(false)
    expect(fetchMock.mock.calls.length).toBe(calls + 1)
  })

  // 已经在登录页就不再 push，否则历史里堆一串 /login
  it('test_already_on_login_page_does_not_redirect', async () => {
    const { router, push } = fakeRouter('login', '/login')
    installSessionExpiry(router)

    fetchMock.mockResolvedValueOnce(json({ code: 'not_authenticated', message: '未登录' }, 401))
    await expect(handle<unknown>(await fetchMock('/', {}))).rejects.toBeInstanceOf(ApiError)

    expect(push).not.toHaveBeenCalled()
  })

  // 登出成功会清掉本地状态
  it('test_logout_clears_local_state', async () => {
    fetchMock.mockResolvedValueOnce(json(USER))
    await login('a@b.c', 'p')

    fetchMock.mockResolvedValueOnce(empty(204))
    await logout()

    fetchMock.mockResolvedValueOnce(empty(401))
    expect(await cachedSignedIn()).toBe(false)
  })
})

describe('首登改密', () => {
  // 业务接口 403 password_change_required → 保留登录态，跳改密页
  it('test_password_change_required_403_keeps_session_and_redirects', async () => {
    const { router, push } = fakeRouter('chat', '/chat?c=1')
    installSessionExpiry(router)

    fetchMock.mockResolvedValueOnce(json({ ...USER, mustChangePassword: true }))
    expect(await cachedSignedIn()).toBe(true)

    fetchMock.mockResolvedValueOnce(
      json(
        {
          code: 'password_change_required',
          message: '首次登录后必须先修改初始口令。',
          request_id: 'r1',
        },
        403,
      ),
    )
    await expect(handle<unknown>(await fetchMock('/', {}))).rejects.toBeInstanceOf(ApiError)

    expect(push).toHaveBeenCalledWith({
      path: '/change-password',
      query: { redirect: '/chat?c=1' },
    })
    // 不清登录态：改密页本身需要当前 cookie
    const calls = fetchMock.mock.calls.length
    expect(await cachedSignedIn()).toBe(true)
    expect(fetchMock.mock.calls.length).toBe(calls)
  })

  // 改密时旧口令错也是 401，但它不是“会话过期”，不能触发全局跳登录页
  it('test_change_password_wrong_old_does_not_trigger_global_401', async () => {
    const onFail = vi.fn()
    onUnauthorized(onFail)
    fetchMock.mockResolvedValueOnce(
      json({ code: 'invalid_credentials', message: '当前口令不正确。' }, 401),
    )

    await expect(changePassword('old', 'new password long enough')).rejects.toThrow(
      '当前口令不正确。',
    )
    expect(onFail).not.toHaveBeenCalled()
  })
})

describe('响应处理', () => {
  // 204 没有 body，`response.json()` 会抛 `Unexpected end of JSON input`，
  // 而那句话会被原样显示给用户。四个旧 handle 里有两份是这么写的
  it('test_empty_body_returns_undefined', async () => {
    await expect(handle<void>(empty(204))).resolves.toBeUndefined()
  })

  // 非 JSON 的错误体（网关的 HTML 错误页）用原文当消息，别吞成「HTTP 502」
  it('test_non_json_error_keeps_the_raw_text', async () => {
    const response = new Response('<html>502 Bad Gateway</html>', { status: 502 })

    await expect(handle<unknown>(response)).rejects.toThrow('<html>502 Bad Gateway</html>')
  })

  // 400 不是会话问题，不跳转
  it('test_400_does_not_redirect', async () => {
    const onFail = vi.fn()
    onUnauthorized(onFail)

    await expect(
      handle<unknown>(json({ code: 'validation_failed', message: '文件是空的。' }, 400)),
    ).rejects.toThrow('文件是空的。')
    expect(onFail).not.toHaveBeenCalled()
  })
})
