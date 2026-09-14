import { useEffect, useRef, useState } from 'react'
import { fetchHealth, startTask, streamTask } from './api'
import HealthStrip from './components/HealthStrip'
import ResultPanel from './components/ResultPanel'
import TaskForm from './components/TaskForm'
import Timeline from './components/Timeline'
import type { Step, TaskFinal } from './types'

const WAITING_LABEL: Record<string, string> = {
  WaitingLlm: 'Waiting for the model…',
  WaitingTool: 'Running a tool…',
  Running: 'Working…',
  Created: 'Starting…',
}

export default function App() {
  const [prompt, setPrompt] = useState('處理今天 SCM 文件')
  const [mode, setMode] = useState('llm')

  const [steps, setSteps] = useState<Step[]>([])
  const [state, setState] = useState<string | null>(null)
  const [final, setFinal] = useState<TaskFinal | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [running, setRunning] = useState(false)
  const [health, setHealth] = useState<Record<string, string> | null>(null)

  const closeStream = useRef<(() => void) | null>(null)

  useEffect(() => {
    fetchHealth().then(setHealth).catch(() => setHealth(null))
    return () => closeStream.current?.()
  }, [])

  const execute = async () => {
    closeStream.current?.()
    setSteps([])
    setFinal(null)
    setError(null)
    setState('Created')
    setRunning(true)

    try {
      const task = await startTask(prompt, mode)

      closeStream.current = streamTask(task.id, {
        // Keyed by index: EventSource replays from step 1 after a reconnect, so
        // appending blindly would duplicate the whole trace.
        onStep: step => setSteps(prev => {
          const next = prev.filter(s => s.index !== step.index)
          next.push(step)
          next.sort((a, b) => a.index - b.index)
          return next
        }),
        onState: setState,
        onDone: result => {
          setFinal(result)
          setState(result.state)
          setRunning(false)
        },
        onError: message => {
          setError(message)
          setRunning(false)
        },
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setRunning(false)
    }
  }

  return (
    <main>
      <header className="page-head">
        <div>
          <h1>Agentic Document Processor</h1>
          <p className="sub">
            Natural-language task, browser automation, classification and mapping,
            end to end.
          </p>
        </div>
        {health && (
          <span className="model-badge" title={`provider: ${health.llmProvider}`}>
            {health.llmModel}
          </span>
        )}
      </header>

      <HealthStrip />

      <TaskForm
        prompt={prompt}
        mode={mode}
        running={running}
        onPromptChange={setPrompt}
        onModeChange={setMode}
        onSubmit={execute}
      />

      {error && <div className="card failure"><p className="summary-text">{error}</p></div>}

      <Timeline
        steps={steps}
        running={running}
        waitingLabel={state ? WAITING_LABEL[state] ?? null : null}
        mode={mode}
      />

      {final && <ResultPanel final={final} />}
    </main>
  )
}
