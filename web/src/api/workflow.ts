export interface WorkflowCapabilities {
  workflow: boolean
  steps: string[]
}

export interface WorkflowDefinition {
  id: string
  key: string
  name: string
  definition: string
  enabled: number
  createdAt: string
  updatedAt: string
}

export interface WorkflowStep {
  id: number
  stepKey: string
  stepType: string
  orderIndex: number
  status: string
  attempts: number
  output?: string | null
  error?: string | null
  startedAt?: string | null
  finishedAt?: string | null
}

export interface WorkflowRun {
  id: number
  workflowKey: string
  documentId: string
  promptVersion?: string | null
  status: string
  error?: string | null
  latencyMs?: number | null
  createdAt: string
  finishedAt?: string | null
  steps: WorkflowStep[]
}

export interface PromptVersion {
  id: number
  promptKey: string
  version: string
  content: string
  active: number
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

export const workflowApi = {
  health: () => fetch('/api/workflow/health').then((r) => handle<WorkflowCapabilities>(r)),
  definitions: () => fetch('/api/workflow/definitions').then((r) => handle<WorkflowDefinition[]>(r)),
  create: (documentId: string, workflowKey?: string, promptVersion?: string) =>
    fetch('/api/workflow/runs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ documentId, workflowKey, promptVersion }),
    }).then((r) => handle<{ runId: number }>(r)),
  runs: (documentId?: string) =>
    fetch(`/api/workflow/runs${documentId ? `?documentId=${encodeURIComponent(documentId)}` : ''}`)
      .then((r) => handle<WorkflowRun[]>(r)),
  run: (id: number) => fetch(`/api/workflow/runs/${id}`).then((r) => handle<WorkflowRun>(r)),
  steps: (id: number) => fetch(`/api/workflow/runs/${id}/steps`).then((r) => handle<WorkflowStep[]>(r)),
  rerunStep: (id: number, stepKey: string) =>
    fetch(`/api/workflow/runs/${id}/steps/${encodeURIComponent(stepKey)}/rerun`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  cancel: (id: number) => fetch(`/api/workflow/runs/${id}/cancel`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  resume: (id: number) => fetch(`/api/workflow/runs/${id}/resume`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  remove: (id: number) => fetch(`/api/workflow/runs/${id}`, { method: 'DELETE' }).then((r) => handle<unknown>(r)),
  prompts: (key?: string) =>
    fetch(`/api/workflow/prompts${key ? `?key=${encodeURIComponent(key)}` : ''}`).then((r) => handle<PromptVersion[]>(r)),
  addPrompt: (promptKey: string, version: string, content: string, active: boolean) =>
    fetch('/api/workflow/prompts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ promptKey, version, content, active }),
    }).then((r) => handle<PromptVersion>(r)),
  activatePrompt: (key: string, version: string) =>
    fetch(`/api/workflow/prompts/${encodeURIComponent(key)}/${encodeURIComponent(version)}/activate`, { method: 'POST' })
      .then((r) => handle<unknown>(r)),
}
