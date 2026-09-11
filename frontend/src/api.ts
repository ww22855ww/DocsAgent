import type { Step, TaskFinal } from './types'

/**
 * The browser cannot resolve Docker service names, so every call is a relative
 * /api path. nginx proxies them to agent-api, with buffering off on the stream
 * route so events arrive as they happen.
 */

export interface StartedTask {
  id: string
  mode: string
  state: string
  prompt: string
}

export async function startTask(prompt: string, mode: string): Promise<StartedTask> {
  const res = await fetch('/api/tasks', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt, mode }),
  })
  if (!res.ok) throw new Error(`Could not start the task (HTTP ${res.status})`)
  return res.json()
}

export async function fetchHealth(): Promise<Record<string, string>> {
  const res = await fetch('/api/health')
  if (!res.ok) throw new Error(`agent-api is not responding (HTTP ${res.status})`)
  return res.json()
}

export interface StreamHandlers {
  onStep: (step: Step) => void
  onState: (state: string, durationMs: number) => void
  onDone: (final: TaskFinal) => void
  onError: (message: string) => void
}

/**
 * Subscribe to a task's trace. Returns a function that closes the connection.
 *
 * EventSource reconnects on its own after a network blip, and the server
 * replays the trace from step 1 on every connect, so the caller must key steps
 * by index rather than appending blindly.
 */
export function streamTask(taskId: string, handlers: StreamHandlers): () => void {
  const source = new EventSource(`/api/tasks/stream/${encodeURIComponent(taskId)}`)
  let finished = false

  source.addEventListener('step', e => {
    handlers.onStep(JSON.parse((e as MessageEvent).data))
  })

  source.addEventListener('state', e => {
    const d = JSON.parse((e as MessageEvent).data)
    handlers.onState(d.state, d.durationMs)
  })

  source.addEventListener('done', e => {
    finished = true
    handlers.onDone(JSON.parse((e as MessageEvent).data))
    source.close()
  })

  source.addEventListener('error', e => {
    const data = (e as MessageEvent).data
    if (data) {
      try {
        handlers.onError(JSON.parse(data).error ?? 'Stream error')
        return
      } catch {
        /* fall through to the connection-level case */
      }
    }
    // A close after "done" is the normal end of the stream, not a failure.
    if (!finished && source.readyState === EventSource.CLOSED) {
      handlers.onError('Lost the connection to agent-api.')
    }
  })

  return () => {
    finished = true
    source.close()
  }
}
