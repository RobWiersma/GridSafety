import { useQuery } from '@tanstack/react-query';
import { useContext, useEffect, useState } from 'react';
import { FetcherContext, useCrews, useFindingAerial, useFindingImage, useFindingStatus } from '../api';
import { ACTION_LABEL, FINDING_TYPE_LABEL, relativeTime, slaText } from '../lib/format';
import type { FindingStatus, FindingView } from '../types';
import { PhotoZoom } from './PhotoZoom';
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
  const aerial = useFindingAerial(id);
  const [view, setView] = useState<'aerial' | 'closeup'>('aerial');
  const [zoom, setZoom] = useState<{ src: string; alt: string } | null>(null);
  const change = useFindingStatus();
  const crews = useCrews();
  const [note, setNote] = useState('');
  const [assignee, setAssignee] = useState('');

  // Start the picker on the crew that normally handles this kind of defect.
  const suggested = data?.suggestedCrew;
  useEffect(() => {
    if (suggested) setAssignee((a) => a || suggested);
  }, [suggested]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const act = (status: FindingStatus) => {
    if (status === 'Assigned' && !assignee.trim()) {
      onToast({ kind: 'error', text: 'Pick a crew or crew member to assign.' });
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
              <figure className="capture">
                <div className="capture-tabs chips" role="tablist" aria-label="Capture view">
                  <button role="tab" aria-selected={view === 'aerial'} className={`chip ${view === 'aerial' ? 'chip-active' : ''}`} onClick={() => setView('aerial')}>
                    Aerial
                  </button>
                  <button role="tab" aria-selected={view === 'closeup'} className={`chip ${view === 'closeup' ? 'chip-active' : ''}`} onClick={() => setView('closeup')}>
                    Close-up
                  </button>
                </div>
                {view === 'aerial' && aerial.data?.url ? (
                  <>
                    <ZoomableImage src={aerial.data.url} alt={`Aerial view of ${f.structureLabel} with the ${FINDING_TYPE_LABEL[f.type].toLowerCase()} marked (simulated finding)`} onZoom={setZoom} />
                    <figcaption className="muted small">
                      Real aerial photo of this structure's location (USDA NAIP, public domain); the defect and detection are simulated.
                    </figcaption>
                  </>
                ) : view === 'aerial' ? (
                  <div className="capture-pending">
                    {aerial.data?.status === 'unavailable'
                      ? <span className="muted small">No aerial photo is available for this spot. <button className="link-button" onClick={() => setView('closeup')}>Show the close-up</button></span>
                      : <><div className="spinner" /><span className="muted small">Fetching the aerial photo of this structure (first time only, usually under a minute)…</span></>}
                  </div>
                ) : image.data ? (
                  <>
                    <ZoomableImage src={image.data} alt={`${FINDING_TYPE_LABEL[f.type]} at ${f.structureLabel} (simulated drone image)`} onZoom={setZoom} />
                    <figcaption className="muted small">Illustrated close-up for the demo; a real drone would upload its photo.</figcaption>
                  </>
                ) : <div className="capture-pending"><div className="spinner" /></div>}
              </figure>
              {zoom && <PhotoZoom src={zoom.src} alt={zoom.alt} onClose={() => setZoom(null)} />}
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
                    <label className="assign-field">
                      <span className="muted small">Assign to</span>
                      <select className="input" value={assignee} onChange={(e) => setAssignee(e.target.value)} disabled={!crews.data}>
                        {!crews.data && <option value="">Loading crews…</option>}
                        {crews.data?.map(({ crew, openWorkOrders }) => (
                          <optgroup key={crew.id} label={`${crew.name} · ${crew.status === 'OffDuty' ? 'off duty' : crew.specialty}`} disabled={crew.status === 'OffDuty'}>
                            <option value={crew.name}>
                              Whole crew ({openWorkOrders} open){crew.name === suggested ? ' ★ suggested' : ''}
                            </option>
                            {crew.members.map((m) => (
                              <option key={m.name} value={`${m.name} (${crew.name})`}>{m.name}, {m.role}</option>
                            ))}
                          </optgroup>
                        ))}
                      </select>
                    </label>
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

/** The drawer's capture: click (or Enter) opens the zoom viewer. */
function ZoomableImage({ src, alt, onZoom }: { src: string; alt: string; onZoom: (p: { src: string; alt: string }) => void }) {
  return (
    <button type="button" className="zoomable" onClick={() => onZoom({ src, alt })} aria-label={`Zoom in: ${alt}`} title="Click to zoom">
      <img src={src} alt={alt} />
      <span className="zoomable-hint" aria-hidden="true">⤢ Zoom</span>
    </button>
  );
}
