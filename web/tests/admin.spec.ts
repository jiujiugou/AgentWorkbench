/**
 * 管理页的 API 契约：成员/邀请/账号状态与后端路由逐条对齐。
 *
 * 这些断言不测样式，只钉住“点按钮会发到哪、带什么字段”。端点和字段写错时
 * 页面通常仍会显示，只是操作静默无效或 422。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { acceptInvite, cachedUser, forgetSession } from '@/api/auth'
import { membersApi } from '@/api/members'
import { archiveKnowledgeBase, unarchiveKnowledgeBase } from '@/api/knowledgeBases'
import { listAuditEvents } from '@/api/audit'

const USER = {
  id: 'u1',
  email: 'admin@example.com',
  role: 'admin',
  status: 'active',
  tenantId: 't1',
  levelRank: 3,
  mustChangePassword: false,
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

let fetchMock: ReturnType<typeof vi.fn>

beforeEach(() => {
  forgetSession()
  fetchMock = vi.fn()
  vi.stubGlobal('fetch', fetchMock)
})

describe('成员管理', () => {
  it('test_member_routes_and_payloads_match_the_backend', async () => {
    fetchMock
      .mockResolvedValueOnce(json([USER]))
      .mockResolvedValueOnce(json(USER, 201))
      .mockResolvedValueOnce(json({ ...USER, role: 'member', levelRank: 2 }))

    await membersApi.list()
    await membersApi.create({
      email: 'bob@example.com',
      password: 'long-enough-password',
      role: 'member',
      levelRank: 2,
    })
    await membersApi.update('u2', { role: 'member', levelRank: 2 })

    const calls = fetchMock.mock.calls as [string, RequestInit][]
    expect(calls[0][0]).toBe('/api/members')
    expect(calls[1][0]).toBe('/api/members')
    expect(calls[1][1].method).toBe('POST')
    expect(JSON.parse(String(calls[1][1].body))).toEqual({
      email: 'bob@example.com',
      password: 'long-enough-password',
      role: 'member',
      levelRank: 2,
    })
    expect(calls[2][0]).toBe('/api/members/u2')
    expect(calls[2][1].method).toBe('PATCH')
    expect(JSON.parse(String(calls[2][1].body))).toEqual({ role: 'member', levelRank: 2 })
  })

  it('test_reset_and_status_routes', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    await membersApi.resetPassword('u2', 'new-long-password')
    await membersApi.disable('u2')
    await membersApi.enable('u2')

    const calls = fetchMock.mock.calls as [string, RequestInit][]
    expect(calls.map(([url]) => url)).toEqual([
      '/api/members/u2/reset-password',
      '/api/members/u2/disable',
      '/api/members/u2/enable',
    ])
    expect(JSON.parse(String(calls[0][1].body))).toEqual({ password: 'new-long-password' })
  })

  it('test_invite_returns_the_one_time_token', async () => {
    fetchMock.mockResolvedValueOnce(json({ inviteId: 'i1', token: 'token-1' }, 201))

    const result = await membersApi.invite({ role: 'member', email: null })

    expect(result.token).toBe('token-1')
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toEqual({ role: 'member', email: null })
  })
})

describe('接受邀请', () => {
  it('test_accept_invite_posts_token_and_sets_session', async () => {
    fetchMock.mockResolvedValueOnce(json(USER, 201))

    const user = await acceptInvite('a b', 'admin@example.com', 'long-enough-password')

    expect(user.email).toBe('admin@example.com')
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/auth/invites/a%20b')
    expect(JSON.parse(String(init.body))).toEqual({
      email: 'admin@example.com',
      password: 'long-enough-password',
    })
    expect((await cachedUser())?.id).toBe('u1')
  })
})

describe('知识库归档', () => {
  it('test_archive_and_unarchive_routes', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    await archiveKnowledgeBase('kb-1')
    await unarchiveKnowledgeBase('kb-1')

    const calls = fetchMock.mock.calls as [string, RequestInit][]
    expect(calls.map(([url]) => url)).toEqual([
      '/api/knowledge-bases/kb-1/archive',
      '/api/knowledge-bases/kb-1/unarchive',
    ])
    expect(calls.every(([, init]) => init.method === 'POST')).toBe(true)
  })
})

describe('邀请管理', () => {
  it('test_list_and_revoke_routes', async () => {
    fetchMock
      .mockResolvedValueOnce(
        json([
          {
            inviteId: 'i1',
            email: null,
            role: 'member',
            expiresAt: '2026-11-09T00:00:00',
            createdAt: '2026-11-06T00:00:00',
          },
        ]),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    const invites = await membersApi.listInvites()
    await membersApi.revokeInvite('i1')

    expect(invites[0].inviteId).toBe('i1')
    const calls = fetchMock.mock.calls as [string, RequestInit][]
    expect(calls[0][0]).toBe('/api/members/invites')
    expect(calls[1][0]).toBe('/api/members/invites/i1')
    expect(calls[1][1].method).toBe('DELETE')
  })
})

describe('审计查询', () => {
  it('test_audit_query_sends_filters_and_cursor', async () => {
    fetchMock.mockResolvedValueOnce(json({ events: [], nextCursor: null }))

    await listAuditEvents({
      limit: 20,
      before: '9',
      action: 'user.update',
      resourceKind: 'user',
    })

    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/api/admin/events?')
    expect(url).toContain('limit=20')
    expect(url).toContain('before=9')
    expect(url).toContain('action=user.update')
    expect(url).toContain('resource_kind=user')
  })
})
