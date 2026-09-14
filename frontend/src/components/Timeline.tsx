import { useState } from 'react'
import type { BrowserAction, Step } from '../types'

const ICON: Record<string, string> = {
  agent: '•', tool: '✓', classify: '✓', summary: '✓', error: '!',
}

/** Who chose to take this step. The badge is how the two modes read differently. */
const DECIDER: Record<string, { label: string; title: string }> = {
  model: { label: '模型決策', title: '這一步是模型讀完上一個結果後自己選的' },
  script: { label: '固定順序', title: '這一步是程式寫死的順序，模型沒有參與' },
}

function pretty(value: unknown): string {
  if (value === null || value === undefined) return ''
  return JSON.stringify(value, null, 2)
}

/** The browser journal, if this step produced one. */
function browserActions(result: unknown): BrowserAction[] | null {
  if (!result || typeof result !== 'object') return null
  const steps = (result as Record<string, unknown>).browser_steps
  return Array.isArray(steps) && steps.length > 0 ? (steps as BrowserAction[]) : null
}

/**
 * What Playwright actually did, action by action.
 *
 * Colleagues who have not met browser automation ask how it "reads" the page.
 * Showing the selector next to each action answers that: it does not read
 * anything, it addresses elements the page already names.
 */
function BrowserDetail({ actions }: { actions: BrowserAction[] }) {
  return (
    <div className="browser-detail">
      <p className="browser-lede">
        Playwright 不是「看懂」畫面，而是照名字去找元件。下面是這次實際做的每一個動作，
        右邊是它用來指定元件的選擇器。
      </p>
      <ol className="browser-steps">
        {actions.map((a, i) => (
          <li key={i}>
            <span className="ba-n">{i + 1}</span>
            <span className="ba-action">{a.action}</span>
            <span className="ba-body">
              <span className="ba-detail">{a.detail}</span>
              {a.found && <span className="ba-found">得到：{a.found}</span>}
            </span>
            <code className="ba-target">{a.target}</code>
          </li>
        ))}
      </ol>
    </div>
  )
}

function StepRow({ step }: { step: Step }) {
  const [open, setOpen] = useState(false)
  const hasPayload = step.arguments != null || step.result != null
  const mark = step.success ? ICON[step.kind] ?? '•' : '!'
  const decider = step.decidedBy ? DECIDER[step.decidedBy] : null
  const actions = browserActions(step.result)

  return (
    <li className={`step ${step.kind} ${step.success ? '' : 'failed'} ${step.note ? 'noted' : ''}`}>
      <span className="marker" aria-hidden="true">{mark}</span>

      <div className="step-body">
        <div className="step-head">
          <span className="step-title">{step.title}</span>
          <span className="step-meta">
            {decider && (
              <span className={`decider ${step.decidedBy}`} title={decider.title}>
                {decider.label}
              </span>
            )}
            {step.durationMs > 0 && (
              <span className="step-time">{(step.durationMs / 1000).toFixed(1)}s</span>
            )}
          </span>
        </div>

        {step.thought && <p className="thought">{step.thought}</p>}
        {step.detail && <p className="detail">{step.detail}</p>}

        {step.note && (
          <p className={`note ${step.noteTone ?? ''}`}>
            <b>{step.noteTone === 'limit' ? '固定流程的極限' : '這就是 agent 的價值'}</b>
            {step.note}
          </p>
        )}

        {actions && (
          <>
            <button type="button" className="link" onClick={() => setOpen(!open)}>
              {open ? '收起瀏覽器操作明細' : `看瀏覽器實際做了什麼（${actions.length} 個動作）`}
            </button>
            {open && <BrowserDetail actions={actions} />}
          </>
        )}

        {!actions && hasPayload && (
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
  mode: string
}

export default function Timeline({ steps, running, waitingLabel, mode }: Props) {
  if (steps.length === 0 && !running) {
    return (
      <section className="card">
        <h2>Execution</h2>
        <p className="empty">Run a task to see the agent's trace here.</p>
      </section>
    )
  }

  const decided = steps.filter(s => s.decidedBy === 'model').length

  return (
    <section className="card">
      <div className="card-head">
        <h2>Execution</h2>
        <span className={`run-mode ${mode}`}>
          {mode === 'llm'
            ? `Agent 模式 · ${decided} 步由模型決定`
            : '固定流程 · 模型沒有參與任何決定'}
        </span>
      </div>
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
