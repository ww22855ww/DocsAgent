import { useCallback, useEffect, useState } from 'react'
import type { HealthReport } from '../types'

/**
 * Pre-flight strip. Checked once on load and on demand, so the presenter can
 * confirm every dependency is up before starting rather than finding out
 * halfway through a run.
 */
export default function HealthStrip() {
  const [report, setReport] = useState<HealthReport | null>(null)
  const [checking, setChecking] = useState(false)
  const [failed, setFailed] = useState(false)
  const [open, setOpen] = useState(false)

  const check = useCallback(async () => {
    setChecking(true)
    try {
      const res = await fetch('/api/health/services')
      if (!res.ok) throw new Error(String(res.status))
      const data: HealthReport = await res.json()
      setReport(data)
      setFailed(false)
      // Only nag when something is actually wrong.
      setOpen(!data.ready)
    } catch {
      setFailed(true)
      setReport(null)
    } finally {
      setChecking(false)
    }
  }, [])

  useEffect(() => { check() }, [check])

  if (failed) {
    return (
      <div className="health bad-bar">
        <span className="dot bad" />
        <span>連不到 agent-api。確認容器是否啟動。</span>
        <button type="button" className="link" onClick={check}>重新檢查</button>
      </div>
    )
  }

  if (!report) {
    return <div className="health"><span className="dot idle" /><span>檢查服務中…</span></div>
  }

  const down = report.services.filter(s => !s.ok)
  const blocking = down.filter(s => s.required)

  return (
    <div className={`health ${report.ready ? '' : 'bad-bar'}`}>
      <span className={`dot ${report.ready ? 'ok' : 'bad'}`} />
      <span className="health-summary">
        {report.ready
          ? `六項依賴正常${down.length ? `，${down.length} 項選用服務未連上` : ''}`
          : `${blocking.length} 項必要服務未就緒`}
      </span>

      <span className="modes">
        <span className="mode-chip">{report.mode.llmModel}</span>
        <span className="mode-chip">主檔 {report.mode.dbquery}</span>
        <span className="mode-chip">{report.mode.mailEnabled ? '寄信開啟' : '寄信關閉'}</span>
      </span>

      <button type="button" className="link" onClick={() => setOpen(!open)}>
        {open ? '收起' : '明細'}
      </button>
      <button type="button" className="link" onClick={check} disabled={checking}>
        {checking ? '檢查中…' : '重新檢查'}
      </button>

      {open && (
        <ul className="health-list">
          {report.services.map(s => (
            <li key={s.key}>
              <span className={`dot ${s.ok ? 'ok' : s.required ? 'bad' : 'warn'}`} />
              <span className="svc">{s.name}</span>
              <span className="svc-detail">{s.detail}</span>
              <span className="svc-ms">{s.ms} ms</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
