import { useState } from 'react';
import { useFindings } from '../api';
import { FINDING_FILTERS, FINDING_TYPE_LABEL, relativeTime, slaText } from '../lib/format';
import { SeverityBadge } from './SeverityBadge';

export function FindingsView({ onOpen }: { onOpen(id: string): void }) {
  const [filter, setFilter] = useState('open');
  const findings = useFindings(filter);

  return (
    <section className="panel panel-flush">
      <div className="panel-toolbar">
        <h2>Findings work queue</h2>
        <div className="chips" role="tablist">
          {FINDING_FILTERS.map((f) => (
            <button key={f.key} role="tab" aria-selected={filter === f.key} className={`chip ${filter === f.key ? 'chip-active' : ''}`} onClick={() => setFilter(f.key)}>
              {f.label}
            </button>
          ))}
        </div>
      </div>

      {findings.isPending && <div className="center"><div className="spinner" /></div>}
      {findings.isError && <div className="banner banner-error">{findings.error.message}</div>}
      {findings.data && findings.data.length === 0 && (
        <p className="empty-row muted">Nothing here. Findings appear as drones report them.</p>
      )}
      {findings.data && findings.data.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Sev</th>
                <th>Issue</th>
                <th>Structure</th>
                <th>Circuit</th>
                <th>Status</th>
                <th>Triage SLA</th>
                <th>Reported</th>
              </tr>
            </thead>
            <tbody>
              {findings.data.map((f) => {
                const sla = slaText(f);
                return (
                  <tr key={f.finding.id} onClick={() => onOpen(f.finding.id)} tabIndex={0} onKeyDown={(e) => e.key === 'Enter' && onOpen(f.finding.id)}
                    className={f.overdue ? 'row-overdue' : ''}>
                    <td><SeverityBadge severity={f.finding.severity} /></td>
                    <td>
                      <strong>{FINDING_TYPE_LABEL[f.finding.type]}</strong>
                      {f.finding.description && <div className="muted small truncate">{f.finding.description}</div>}
                    </td>
                    <td className="mono">{f.finding.structureLabel}</td>
                    <td>{f.circuitName}</td>
                    <td>
                      <span className={`pill pill-${f.finding.status.toLowerCase()}`}>{f.finding.status}</span>
                      {f.finding.assignedTo && <div className="muted small">{f.finding.assignedTo}</div>}
                    </td>
                    <td>
                      {sla ? <span className={sla.overdue ? 'text-danger strong' : ''}>{sla.text}</span> : <span className="muted">–</span>}
                      {f.finding.escalated && <div><span className="pill pill-danger">escalated</span></div>}
                    </td>
                    <td className="muted">{relativeTime(f.finding.createdAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
