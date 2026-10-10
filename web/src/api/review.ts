export interface ReviewCapabilities {
  extract: boolean
  compare: boolean
  llmExtract: boolean
}

export interface ReviewStep {
  name: string
  status: string
  elapsedMs: number
  detail?: string | null
}

export interface ReviewRun {
  id: number
  documentId: string
  fileName: string
  mode: string
  template?: string | null
  status: string
  model?: string | null
  error?: string | null
  latencyMs?: number | null
  createdAt: string
  finishedAt?: string | null
  elementCount: number
  findingCount: number
  steps: string
}

export interface ReviewElement {
  id: number
  key: string
  label: string
  value: string
  valueType: string
  unit?: string | null
  source: string
  confidence: number
  chunkId?: number | null
  seq?: number | null
  pageFrom?: number | null
  pageTo?: number | null
  evidence?: string | null
}

export interface ReviewFinding {
  id: number
  ruleId?: number | null
  severity: string
  category: string
  title: string
  detail?: string | null
  expected?: string | null
  actual?: string | null
  chunkId?: number | null
  seq?: number | null
  pageFrom?: number | null
  pageTo?: number | null
  evidence?: string | null
}

export interface ReviewRule {
  id: number
  category: string
  name: string
  body: string
  ruleType: string
  severity: string
  targetKey?: string | null
  expected?: string | null
  matchHint?: string | null
  enabled: number
  version: number
  createdAt: string
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `HTTP ${response.status}`)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const reviewApi = {
  health: () => fetch('/api/review/health').then((r) => handle<ReviewCapabilities>(r)),
  create: (documentId: string, mode: string, template?: string) =>
    fetch('/api/review/runs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ documentId, mode, template }),
    }).then((r) => handle<{ runId: number }>(r)),
  runs: (documentId?: string) =>
    fetch(`/api/review/runs${documentId ? `?documentId=${encodeURIComponent(documentId)}` : ''}`)
      .then((r) => handle<ReviewRun[]>(r)),
  run: (id: number) => fetch(`/api/review/runs/${id}`).then((r) => handle<ReviewRun>(r)),
  rerun: (id: number) => fetch(`/api/review/runs/${id}/rerun`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  remove: (id: number) => fetch(`/api/review/runs/${id}`, { method: 'DELETE' }).then((r) => handle<unknown>(r)),
  elements: (id: number) => fetch(`/api/review/runs/${id}/elements`).then((r) => handle<ReviewElement[]>(r)),
  findings: (id: number) => fetch(`/api/review/runs/${id}/findings`).then((r) => handle<ReviewFinding[]>(r)),
  report: (id: number, format: string) =>
    fetch(`/api/review/runs/${id}/report?format=${format}`).then((r) => handle<{ format: string; content: string }>(r)),
  rules: () => fetch('/api/review/rules').then((r) => handle<ReviewRule[]>(r)),
  addRule: (rule: Partial<ReviewRule>) =>
    fetch('/api/review/rules', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(rule),
    }).then((r) => handle<ReviewRule>(r)),
  removeRule: (id: number) => fetch(`/api/review/rules/${id}`, { method: 'DELETE' }).then((r) => handle<unknown>(r)),
}
