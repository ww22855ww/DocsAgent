import type { DocumentOutcome, TaskFinal } from '../types'

const STATUS_LABEL: Record<string, string> = {
  archived: 'Archived',
  manual_review: 'Manual review',
  failed: 'Failed',
  pending: 'Pending',
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
        {doc.status === 'manual_review' && doc.notified && <span className="sent">mail sent</span>}
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
          <h2>Result</h2>
          <span className={`state-pill ${final.state.toLowerCase()}`}>{final.state}</span>
        </div>

        <div className="tiles">
          <div className="tile">
            <span className="tile-value">{counts.downloaded}</span>
            <span className="tile-label">Downloaded</span>
          </div>
          <div className="tile">
            <span className="tile-value">{counts.archived}</span>
            <span className="tile-label">Archived</span>
          </div>
          <div className="tile warn">
            <span className="tile-value">{counts.manualReview}</span>
            <span className="tile-label">Manual review</span>
          </div>
          <div className="tile">
            <span className="tile-value">{(final.durationMs / 1000).toFixed(0)}s</span>
            <span className="tile-label">Duration</span>
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
              <th>File</th><th>Category</th><th>Document No</th><th>Supplier</th><th>Outcome</th>
            </tr>
          </thead>
          <tbody>
            {documents.map(d => <DocumentRow key={d.filename} doc={d} />)}
          </tbody>
        </table>
      </section>

      {reviewed.length > 0 && (
        <section className="card review">
          <h2>Manual review</h2>
          {reviewed.map(d => (
            <div className="review-item" key={d.filename}>
              <span className="mono">{d.filename}</span>
              <p>{d.reviewReason ?? 'Needs a human.'}</p>
            </div>
          ))}
        </section>
      )}

      {(final.summary || final.error) && (
        <section className={`card ${final.error ? 'failure' : 'summary'}`}>
          <h2>{final.error ? 'Failure' : 'Agent summary'}</h2>
          <p className="summary-text">{final.error ?? final.summary}</p>
        </section>
      )}
    </>
  )
}
