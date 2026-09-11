import { useState } from 'react'
import type { Step } from '../types'

const ICON: Record<string, string> = {
  agent: '•', tool: '✓', classify: '✓', summary: '✓', error: '!',
}

function pretty(value: unknown): string {
  if (value === null || value === undefined) return ''
  return JSON.stringify(value, null, 2)
}

function StepRow({ step }: { step: Step }) {
  const [open, setOpen] = useState(false)
  const hasPayload = step.arguments != null || step.result != null
  const mark = step.success ? ICON[step.kind] ?? '•' : '!'

  return (
    <li className={`step ${step.kind} ${step.success ? '' : 'failed'}`}>
      <span className="marker" aria-hidden="true">{mark}</span>

      <div className="step-body">
        <div className="step-head">
          <span className="step-title">{step.title}</span>
          {step.durationMs > 0 && (
            <span className="step-time">{(step.durationMs / 1000).toFixed(1)}s</span>
          )}
        </div>

        {step.thought && <p className="thought">{step.thought}</p>}
        {step.detail && <p className="detail">{step.detail}</p>}

        {hasPayload && (
          <>
            <button type="button" className="link" onClick={() => setOpen(!open)}>
              {open ? 'Hide' : 'Show'} {step.toolName ?? 'details'}
            </button>
            {open && (
              <div className="payload">
                {step.arguments != null && (
                  <>
                    <span className="payload-label">Arguments</span>
                    <pre>{pretty(step.arguments)}</pre>
                  </>
                )}
                {step.result != null && (
                  <>
                    <span className="payload-label">Result</span>
                    <pre>{pretty(step.result)}</pre>
                  </>
                )}
              </div>
            )}
          </>
        )}
      </div>
    </li>
  )
}

interface Props {
  steps: Step[]
  running: boolean
  waitingLabel: string | null
}

export default function Timeline({ steps, running, waitingLabel }: Props) {
  if (steps.length === 0 && !running) {
    return (
      <section className="card">
        <h2>Execution</h2>
        <p className="empty">Run a task to see the agent's trace here.</p>
      </section>
    )
  }

  return (
    <section className="card">
      <h2>Execution</h2>
      <ol className="timeline">
        {steps.map(s => <StepRow key={s.index} step={s} />)}
        {running && (
          <li className="step pending">
            <span className="marker spinner" aria-hidden="true" />
            <div className="step-body">
              <span className="step-title">{waitingLabel ?? 'Working…'}</span>
            </div>
          </li>
        )}
      </ol>
    </section>
  )
}
