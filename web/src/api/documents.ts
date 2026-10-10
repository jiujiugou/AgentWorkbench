export type DocumentStatus = 'pending' | 'parsing' | 'parsed' | 'failed'

export interface DocumentDto {
  id: string
  fileName: string
  status: DocumentStatus
  pageCount: number
  sizeBytes: number
  sha256: string
  error?: string | null
  createdAt: string
  parsedAt?: string | null
  chunkCount?: number | null
}

export interface PageDto {
  pageNo: number
  rawText: string
  cleanedText: string
}

export interface ChunkDto {
  seq: number
  pageFrom: number
  pageTo: number
  heading?: string | null
  content: string
  charCount: number
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `HTTP ${response.status}`)
  }
  return (await response.json()) as T
}

export const documentsApi = {
  list: () => fetch('/api/documents').then((r) => handle<DocumentDto[]>(r)),
  get: (id: string) => fetch(`/api/documents/${id}`).then((r) => handle<DocumentDto>(r)),
  pages: (id: string) => fetch(`/api/documents/${id}/pages`).then((r) => handle<PageDto[]>(r)),
  chunks: (id: string) => fetch(`/api/documents/${id}/chunks`).then((r) => handle<ChunkDto[]>(r)),
  upload: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return fetch('/api/documents', { method: 'POST', body: form }).then((r) => handle<DocumentDto>(r))
  },
  reparse: (id: string) =>
    fetch(`/api/documents/${id}/reparse`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  remove: (id: string) => fetch(`/api/documents/${id}`, { method: 'DELETE' }),
}
