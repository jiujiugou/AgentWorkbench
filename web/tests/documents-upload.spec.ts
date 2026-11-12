/**
 * 新增文档的前端契约：目标知识库必须随 multipart 一起发出。
 *
 * 后端缺 `kb_id` 会落到默认公共库——这不会报错，只会让“私人库上传”
 * 静默变成“全员可见”。所以这里断言 FormData，而不是只看返回 202。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { uploadDocument } from '@/api/documents'

type Handler = () => void

class FakeXHR {
  static last: FakeXHR | null = null
  status = 0
  responseText = ''
  form: FormData | null = null
  upload = { addEventListener: vi.fn() }
  private handlers: Record<string, Handler> = {}

  open = vi.fn()

  addEventListener = vi.fn((event: string, handler: Handler) => {
    this.handlers[event] = handler
  })

  send = vi.fn((form: FormData) => {
    this.form = form
  })

  abort = vi.fn(() => this.handlers.abort?.())

  finish(): void {
    this.status = 202
    this.responseText = JSON.stringify({ id: 'doc-1' })
    this.handlers.load?.()
  }

  constructor() {
    FakeXHR.last = this
  }
}

beforeEach(() => {
  FakeXHR.last = null
  vi.stubGlobal('XMLHttpRequest', FakeXHR)
})

describe('新增文档', () => {
  it('test_upload_includes_the_selected_knowledge_base', async () => {
    const task = uploadDocument(new File(['pdf'], 'a.pdf'), () => {}, 'kb-private')
    const xhr = FakeXHR.last
    expect(xhr?.form?.get('kb_id')).toBe('kb-private')

    xhr?.finish()
    await task.promise
  })

  it('test_upload_omits_kb_id_when_none_is_selected', async () => {
    const task = uploadDocument(new File(['pdf'], 'a.pdf'), () => {})
    const xhr = FakeXHR.last
    expect(xhr?.form?.has('kb_id')).toBe(false)

    xhr?.finish()
    await task.promise
  })
})
