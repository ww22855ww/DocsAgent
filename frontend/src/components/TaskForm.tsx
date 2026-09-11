interface Props {
  prompt: string
  mode: string
  running: boolean
  onPromptChange: (value: string) => void
  onModeChange: (value: string) => void
  onSubmit: () => void
}

const PRESETS = ['處理今天 SCM 文件', '處理今天 SCM 部門的品檢文件']

export default function TaskForm({
  prompt, mode, running, onPromptChange, onModeChange, onSubmit,
}: Props) {
  return (
    <section className="card">
      <div className="card-head">
        <h2>Task</h2>
        <div className="mode-toggle" role="group" aria-label="Agent mode">
          <button
            type="button"
            className={mode === 'scripted' ? 'active' : ''}
            onClick={() => onModeChange('scripted')}
            disabled={running}
          >
            Scripted
          </button>
          <button
            type="button"
            className={mode === 'llm' ? 'active' : ''}
            onClick={() => onModeChange('llm')}
            disabled={running}
          >
            Agent
          </button>
        </div>
      </div>

      <p className="mode-hint">
        {mode === 'llm'
          ? 'The model decides which tool to call at each step.'
          : 'A fixed sequence, for comparison. No decisions are made by the model.'}
      </p>

      <form
        className="task-row"
        onSubmit={e => { e.preventDefault(); if (!running) onSubmit() }}
      >
        <input
          id="task-prompt"
          value={prompt}
          onChange={e => onPromptChange(e.target.value)}
          placeholder="處理今天 SCM 文件"
          disabled={running}
          aria-label="Task"
        />
        <button type="submit" className="primary" disabled={running || !prompt.trim()}>
          {running ? 'Running…' : 'Execute'}
        </button>
      </form>

      <div className="presets">
        {PRESETS.map(p => (
          <button key={p} type="button" onClick={() => onPromptChange(p)} disabled={running}>
            {p}
          </button>
        ))}
      </div>
    </section>
  )
}
