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
