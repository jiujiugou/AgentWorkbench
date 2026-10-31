/**
 * F2 知识资料管理 · API
 * 对应 DocumentsController（/api/documents）。
 * 注意：GET 列表当前返回**裸数组**（无 items 包装）；错误体当前只有 { message }，无 code。
 */

export type DocumentStatus = 'pending' | 'parsing' | 'parsed' | 'failed'

/** 索引覆盖度（后端按「块数 vs 向量数」算出，不是文档状态列）。 */
export type IndexState = 'full' | 'text_only' | 'partial' | 'empty' | 'pending' | 'failed'

export interface DocumentDto {
  id: string
  fileName: string
  status: DocumentStatus | string
  pageCount: number
  sizeBytes: number
  sha256: string
  error: string | null
  createdAt: string
  parsedAt: string | null
  chunkCount?: number | null
  /** 已写入向量索引的块数；缺失（老响应）按 0 处理。 */
  vectorCount?: number | null
  /** 索引覆盖度；text_only / partial 表示语义检索覆盖不全。 */
  indexState?: IndexState | string
}

export interface DocumentPageDto {
  pageNo: number
  rawText: string
  cleanedText: string
}

export interface DocumentChunkDto {
  seq: number
  pageFrom: number
  pageTo: number
  heading: string | null
  content: string
  charCount: number
}

export const ACCEPT_EXTS = ['.pdf', '.txt', '.md', '.json']
export const MAX_FILE_BYTES = 30 * 1024 * 1024

function parseError(text: string): string {
  if (!text) return ''
  try {
    const obj = JSON.parse(text) as { message?: string }
    return obj.message ?? text
  } catch {
    return text
  }
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(parseError(text) || `HTTP ${response.status}`)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export async function listDocuments(): Promise<DocumentDto[]> {
  return handle<DocumentDto[]>(await fetch('/api/documents'))
}

export async function fetchPages(id: string): Promise<DocumentPageDto[]> {
  return handle<DocumentPageDto[]>(await fetch(`/api/documents/${id}/pages`))
}

export async function fetchChunks(id: string): Promise<DocumentChunkDto[]> {
  return handle<DocumentChunkDto[]>(await fetch(`/api/documents/${id}/chunks`))
}

export async function reparseDocument(id: string): Promise<void> {
  await handle<unknown>(await fetch(`/api/documents/${id}/reparse`, { method: 'POST' }))
}

export async function removeDocument(id: string): Promise<void> {
  await handle<unknown>(await fetch(`/api/documents/${id}`, { method: 'DELETE' }))
}

/** 上传（带进度与取消）。失败时抛出后端 message。 */
export function uploadDocument(
  file: File,
  onProgress: (percent: number) => void,
): { promise: Promise<DocumentDto>; abort: () => void } {
  const xhr = new XMLHttpRequest()
  const promise = new Promise<DocumentDto>((resolve, reject) => {
    xhr.open('POST', '/api/documents')
    xhr.upload.addEventListener('progress', (e) => {
      if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100))
    })
    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as DocumentDto)
        } catch {
          reject(new Error('响应不是合法 JSON'))
        }
      } else {
        reject(new Error(parseError(xhr.responseText) || `HTTP ${xhr.status}`))
      }
    })
    xhr.addEventListener('error', () => reject(new Error('网络错误')))
    xhr.addEventListener('abort', () => reject(new Error('已取消')))
    const form = new FormData()
    form.append('file', file)
    xhr.send(form)
  })
  return { promise, abort: () => xhr.abort() }
}
