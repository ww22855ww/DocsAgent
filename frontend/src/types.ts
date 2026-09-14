export type StepKind = 'agent' | 'tool' | 'classify' | 'summary' | 'error'

export interface Step {
  index: number
  kind: StepKind
  title: string
  detail: string | null
  thought: string | null
  toolName: string | null
  arguments: unknown
  result: unknown
  success: boolean
  durationMs: number
  at: string
  /** "model" when the agent chose this step, "script" when the fixed order did. */
  decidedBy: 'model' | 'script' | null
  /** Which layer did the work: an MCP tool, or the model itself. */
  executedBy: 'mcp' | 'model' | null
  /** Callout for the one or two steps that carry the argument. */
  note: string | null
  noteTone: 'win' | 'limit' | null
}

/** One Playwright action, recorded so the trace can show how the page was driven. */
export interface BrowserAction {
  action: string
  target: string
  detail: string
  found: string | null
}

export interface DocumentOutcome {
  filename: string
  category: string | null
  confidence: number | null
  supplierCode: string | null
  supplierName: string | null
  partNo: string | null
  documentNo: string | null
  status: 'pending' | 'archived' | 'manual_review' | 'failed'
  reviewReason: string | null
  notified: boolean
}

export interface Counts {
  downloaded: number
  classified: number
  archived: number
  manualReview: number
  notificationsSent: number
  byCategory: Record<string, number>
}

export interface TaskFinal {
  state: string
  summary: string | null
  error: string | null
  durationMs: number
  counts: Counts
  documents: DocumentOutcome[]
}

export type TaskState =
  | 'Created' | 'Running' | 'WaitingLlm' | 'WaitingTool'
  | 'ManualReview' | 'Completed' | 'Failed'

export interface ServiceStatus {
  key: string
  name: string
  detail: string
  ok: boolean
  ms: number
  required: boolean
}

export interface HealthReport {
  ready: boolean
  checkedAt: string
  mode: {
    agent: string
    llmProvider: string
    llmModel: string
    // Sourced from mcp-worker; null when it did not answer.
    dbquery: string | null
    mailEnabled: boolean | null
    modeSource: string
    busy: boolean
  }
  services: ServiceStatus[]
}
