import { useEffect, useState } from 'react'

type Health = Record<string, unknown>

/**
 * Phase 0 console. Confirms the browser -> nginx -> agent-api path works.
 * Phase 6 replaces this with the task form, execution trace and summary.
 */
export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [ready, setReady] = useState<Health | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const load = async () => {
      try {
        const h = await fetch('/api/health')
        setHealth(await h.json())
        const r = await fetch('/api/ready')
        setReady(await r.json())
        setError(null)
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e))
      }
    }
    load()
    const id = setInterval(load, 10_000)
    return () => clearInterval(id)
  }, [])

  return (
    <main>
      <h1>Agentic Document Processor</h1>
      <p className="sub">Phase 0 — bootstrap console</p>

      {error && <div className="card bad">Cannot reach agent-api: {error}</div>}

      <div className="card">
        <strong>Agent API</strong>
        {health
          ? Object.entries(health).map(([k, v]) => (
              <div className="row" key={k}>
                <span className="k">{k}</span>
                <span className="v">{String(v)}</span>
              </div>
            ))
          : <div className="row"><span className="k">loading…</span></div>}
      </div>

      <div className="card">
        <strong>Dependencies</strong>
        {ready && typeof ready.checks === 'object' && ready.checks !== null
          ? Object.entries(ready.checks as Record<string, string>).map(([k, v]) => (
              <div className="row" key={k}>
                <span className="k">{k}</span>
                <span className={`v ${v.startsWith('ok') ? 'ok' : 'bad'}`}>{v}</span>
              </div>
            ))
          : <div className="row"><span className="k">loading…</span></div>}
      </div>
    </main>
  )
}
