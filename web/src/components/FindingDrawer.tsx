import { useQuery } from '@tanstack/react-query';
import { useContext, useEffect, useState } from 'react';
import { FetcherContext, useFindingImage, useFindingStatus } from '../api';
import { ACTION_LABEL, FINDING_TYPE_LABEL, relativeTime, slaText } from '../lib/format';
import type { FindingStatus, FindingView } from '../types';
import { SeverityBadge } from './SeverityBadge';
import type { ToastMessage } from './Toast';

interface Props {
  id: string;
  isSupervisor: boolean;
  onClose(): void;
  onToast(t: ToastMessage): void;
}

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

export function FindingDrawer({ id, isSupervisor, onClose, onToast }: Props) {
  const api = useContext(FetcherContext)!;
  const { data, isPending } = useQuery({ queryKey: ['finding', id], queryFn: () => api<FindingView>(`/findings/${id}`), refetchInterval: 5000 });
  const image = useFindingImage(id);
  const change = useFindingStatus();
  const [note, setNote] = useState('');
  const [assignee, setAssignee] = useState('');

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const act = (status: FindingStatus) => {
    if (status === 'Assigned' && !assignee.trim()) {
      onToast({ kind: 'error', text: 'Enter the crew or person to assign.' });
      return;
    }
    change.mutate(
      { id, status, note: note || undefined, assignee: status === 'Assigned' ? assignee : undefined },
      {
        onSuccess: () => {
          setNote('');
          onToast({ kind: 'success', text: `Finding ${status.toLowerCase()}` });
        },
        onError: (err) => onToast({ kind: 'error', text: err.message }),
      },
    );
  };

  const f = data?.finding;
  const sla = data ? slaText(data) : null;

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-label="Finding details">
        {isPending || !data || !f ? (
          <div className="center"><div className="spinner" /></div>
        ) : (
          <>
            <header className="drawer-header">
              <div className="drawer-title">
                <SeverityBadge severity={f.severity} />
                <div>
                  <h2>{FINDING_TYPE_LABEL[f.type]}</h2>
                  <p className="muted">{f.structureLabel} · {data.circuitName}</p>
                </div>
              </div>
              <button className="btn btn-ghost icon-btn" onClick={onClose} aria-label="Close">✕</button>
            </header>

            <div className="drawer-body">
              {image.data && (
                <figure className="capture">
                  <img src={image.data} alt={`${FINDING_TYPE_LABEL[f.type]} at ${f.structureLabel} (simulated drone image)`} />
                  <figcaption className="muted small">Synthetic image for the demo; a real drone would upload its photo.</figcaption>
                </figure>
              )}
              <div className="facts">
                <span className={`pill pill-${f.status.toLowerCase()}`}>{f.status}</span>
                <span>{data.severityLabel}</span>
              </div>
              {sla && (
                <div className={`banner ${sla.overdue ? 'banner-error' : 'banner-info'}`}>
                  Triage {sla.text}{f.escalated ? ' · escalated to on-call supervisor' : ''}
                </div>
              )}
              {f.description && <p className="notes">{f.description}</p>}

              <dl className="kv">
                <dt>Model confidence</dt><dd>{Math.round(f.confidence * 100)}%</dd>
                <dt>Reported</dt><dd>{fmt(f.createdAt)} ({relativeTime(f.createdAt)})</dd>
                <dt>Mission</dt><dd className="mono">{f.missionId}</dd>
                <dt>Location</dt>
                <dd>
                  <a className="link" href={`https://www.google.com/maps?q=${f.location.lat},${f.location.lon}`} target="_blank" rel="noopener noreferrer">
                    {f.location.lat.toFixed(5)}, {f.location.lon.toFixed(5)} ↗
                  </a>
                </dd>
                {f.assignedTo && (<><dt>Assigned to</dt><dd>{f.assignedTo}</dd></>)}
              </dl>

              {data.nextStatuses.length > 0 && (
                <section>
                  <h3>Actions</h3>
                  <input className="input" placeholder="Note for the record (optional)" value={note} onChange={(e) => setNote(e.target.value)} />
                  {data.nextStatuses.includes('Assigned') && (
                    <input className="input" style={{ marginTop: 8 }} placeholder="Assign to (e.g. Line Crew 4, Veg Mgmt – Davey Tree)"
                      value={assignee} onChange={(e) => setAssignee(e.target.value)} />
                  )}
                  <div className="status-buttons">
                    {data.nextStatuses.map((s) => {
                      const locked = s === 'Dismissed' && !isSupervisor;
                      return (
                        <button key={s} className={`btn ${s === 'Dismissed' ? 'btn-secondary' : 'btn-primary'}`}
                          disabled={change.isPending || locked}
                          title={locked ? 'Only supervisors can dismiss findings' : undefined}
                          onClick={() => act(s)}>
                          {locked ? '🔒 ' : ''}{ACTION_LABEL[s]}
                        </button>
                      );
                    })}
                  </div>
                </section>
              )}

              <section>
                <h3>Audit trail</h3>
                <ol className="timeline">
                  {[...f.history].reverse().map((h, i) => (
                    <li key={i}>
                      <span className="tl-dot" />
                      <div>
                        <strong>{h.to}</strong> <span className="muted small">by {h.by ?? 'system'} · {fmt(h.at)}</span>
                        {h.note && <p className="small">{h.note}</p>}
                      </div>
                    </li>
                  ))}
                  <li>
                    <span className="tl-dot" />
                    <div><strong>Reported by drone</strong> <span className="muted small">{fmt(f.createdAt)}</span></div>
                  </li>
                </ol>
              </section>
            </div>
          </>
        )}
      </aside>
    </>
  );
}
