import type { DocumentOutcome, TaskFinal } from '../types'

const STATUS_LABEL: Record<string, string> = {
  archived: '已歸檔',
  manual_review: '人工複核',
  failed: '處理失敗',
  pending: '待處理',
}

function DocumentRow({ doc }: { doc: DocumentOutcome }) {
  const supplier = doc.supplierCode
    ? `${doc.supplierCode}${doc.supplierName ? ` · ${doc.supplierName}` : ''}`
    : doc.supplierName ?? '—'

  return (
    <tr>
      <td className="mono">{doc.filename}</td>
      <td>{doc.category ?? '—'}</td>
      <td>{doc.documentNo ?? '—'}</td>
      <td>{supplier}</td>
      <td>
        <span className={`pill ${doc.status}`}>{STATUS_LABEL[doc.status] ?? doc.status}</span>
        {doc.status === 'manual_review' && doc.notified && <span className="sent">已寄出通知</span>}
      </td>
    </tr>
  )
}

export default function ResultPanel({ final }: { final: TaskFinal }) {
  const { counts, documents } = final
  const reviewed = documents.filter(d => d.status === 'manual_review')

  return (
    <>
      <section className="card">
        <div className="card-head">
          <h2>結果</h2>
          <span className={`state-pill ${final.state.toLowerCase()}`}>{final.state}</span>
        </div>

        <div className="tiles">
          <div className="tile">
            <span className="tile-value">{counts.downloaded}</span>
            <span className="tile-label">取得文件</span>
          </div>
          <div className="tile">
            <span className="tile-value">{counts.archived}</span>
            <span className="tile-label">已歸檔</span>
          </div>
          <div className="tile warn">
            <span className="tile-value">{counts.manualReview}</span>
            <span className="tile-label">人工複核</span>
          </div>
          <div className="tile">
            <span className="tile-value">{(final.durationMs / 1000).toFixed(0)}s</span>
            <span className="tile-label">總耗時</span>
          </div>
        </div>

        {Object.keys(counts.byCategory).length > 0 && (
          <div className="categories">
            {Object.entries(counts.byCategory).map(([name, n]) => (
              <span key={name} className="chip">{name}<b>{n}</b></span>
            ))}
          </div>
        )}

        <table className="documents">
          <thead>
            <tr>
              <th>檔案</th><th>類別</th><th>單據號碼</th><th>供應商</th><th>處理結果</th>
            </tr>
          </thead>
          <tbody>
            {documents.map(d => <DocumentRow key={d.filename} doc={d} />)}
          </tbody>
        </table>
      </section>

      {reviewed.length > 0 && (
        <section className="card review">
          <h2>人工複核</h2>
          {reviewed.map(d => (
            <div className="review-item" key={d.filename}>
              <span className="mono">{d.filename}</span>
              <p>{d.reviewReason ?? '需要人工判斷。'}</p>
            </div>
          ))}
        </section>
      )}

      {(final.summary || final.error) && (
        <section className={`card ${final.error ? 'failure' : 'summary'}`}>
          <h2>{final.error ? '失敗' : 'Agent 摘要'}</h2>
          <p className="summary-text">{final.error ?? final.summary}</p>
        </section>
      )}
    </>
  )
}
