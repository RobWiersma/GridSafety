import { useQuery } from '@tanstack/react-query';
import { useContext, useEffect, useState } from 'react';
import { FetcherContext, useAssignees, useFindingAerial, useFindingImage, useFindingStatus } from '../api';
import { SHIFT_HOURS, canTake, countdown, dispatchName, phaseAt, statusText } from '../lib/field';
import { ACTION_LABEL, FINDING_TYPE_LABEL, relativeTime, slaText } from '../lib/format';
import type { AssigneeOption, FindingStatus, FindingView } from '../types';
import { PhotoZoom } from './PhotoZoom';
import { SeverityBadge } from './SeverityBadge';
import type { ToastMessage } from './Toast';

interface Props {
  id: string;
  isSupervisor: boolean;
  onClose(): void;
  onToast(t: ToastMessage): void;
}

/** "8.2 km from Concord yard" (straight line), or the depot alone when its position is unknown. */
const distance = (o: AssigneeOption) =>
  o.distanceKm == null ? (o.depotName ?? 'no depot set') : `${o.distanceKm.toFixed(1)} km from ${o.afterJob ?? o.depotName}`;

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

export function FindingDrawer({ id, isSupervisor, onClose, onToast }: Props) {
  const api = useContext(FetcherContext)!;
  const { data, isPending } = useQuery({ queryKey: ['finding', id], queryFn: () => api<FindingView>(`/findings/${id}`), refetchInterval: 5000 });
  const image = useFindingImage(id);
  const aerial = useFindingAerial(id);
  const [view, setView] = useState<'aerial' | 'closeup'>('aerial');
  const [zoom, setZoom] = useState<{ src: string; alt: string } | null>(null);
  const change = useFindingStatus();
  const canAssign = Boolean(data?.nextStatuses.includes('Assigned'));
  const options = useAssignees(id, canAssign);
  const [note, setNote] = useState('');
  const [assignee, setAssignee] = useState('');

  // Start the picker on the nearest qualified technician; drop a pick that is no longer on the list.
  const nearest = options.data?.find(canTake)?.label;
  useEffect(() => {
    if (!options.data) return;
    setAssignee((a) => (a && options.data.some((o) => o.label === a && canTake(o)) ? a : nearest ?? ''));
  }, [options.data, nearest]);
  const technicians = options.data?.filter((o) => o.member && canTake(o)) ?? [];
  const wholeCrews = options.data?.filter((o) => !o.member && canTake(o)) ?? [];
  const busy = options.data?.filter((o) => !canTake(o)) ?? [];
  const hoursFor = (o: AssigneeOption) => (o.hoursLeft != null ? ` · ${o.hoursLeft} h left` : '');
  const whyNot = (o: AssigneeOption) =>
    o.busyUntil ? `out elsewhere, free in ${countdown(Date.parse(o.busyUntil), now)}`
    : o.restingUntil ? `resting after a full shift, back in ${countdown(Date.parse(o.restingUntil), now)}`
    : `needs ~${o.jobHours} h, ${o.hoursLeft} h left`;
  const afterFor = (o: AssigneeOption) => (o.afterJob ? ` · after ${o.afterJob}` : '');
  const [now, setNow] = useState(() => Date.now());
  const dispatch = data?.finding.dispatch;
  const moving = Boolean(dispatch && Date.parse(dispatch.returnsAt) > now - 1000);
  useEffect(() => {
    if (!moving && !busy.length) return;
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [moving, busy.length]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const act = (status: FindingStatus) => {
    if (status === 'Assigned' && !assignee.trim()) {
      onToast({ kind: 'error', text: !nearest ? 'Nobody qualified is free right now (out, resting or out of hours). Check Resources, or wait for a crew to get back.' : 'Pick a technician or crew to assign.' });
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

              {dispatch && (() => {
                const phase = phaseAt(dispatch, now);
                const steps: [string, string][] = [
                  [`Leaves ${dispatch.fromLabel}`, dispatch.departedAt],
                  ['On site', dispatch.arrivesAt],
                  [dispatch.recalledAt ? 'Called back' : 'Repaired', dispatch.recalledAt ?? dispatch.repairedAt],
                  [dispatch.nextFindingId ? `On to ${dispatch.nextSiteLabel ?? 'next job'}` : `Back at ${dispatch.depotName}`, dispatch.returnsAt],
                ];
                return (
                  <div className={`field-job field-job-${phase.toLowerCase()}`}>
                    <strong>{dispatchName(dispatch)}</strong>
                    <span> · {statusText(dispatch, now)}</span>
                    <ol className="field-steps">
                      {steps.map(([label, at]) => (
                        <li key={label} className={Date.parse(at) <= now ? 'done' : undefined}>
                          {label}<span className="muted small"> · {new Date(at).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', second: '2-digit' })}</span>
                        </li>
                      ))}
                    </ol>
                    <span className="muted small">
                      Simulated and sped up{dispatch.onRoads ? ', along the roads' : ' (no road route found, so a straight line)'}; the truck shows on the Operations map.
                    </span>
                  </div>
                );
              })()}

              {data.nextStatuses.length > 0 && (
                <section>
                  <h3>Actions</h3>
                  <input className="input" placeholder="Note for the record (optional)" value={note} onChange={(e) => setNote(e.target.value)} />
                  {data.nextStatuses.includes('Assigned') && (
                    <label className="assign-field">
                      <span className="muted small">
                        Assign to · only on-duty people qualified for {FINDING_TYPE_LABEL[f.type].toLowerCase()} with hours left in their {SHIFT_HOURS} h shift, nearest first
                      </span>
                      <select className="input" value={assignee} onChange={(e) => setAssignee(e.target.value)} disabled={!options.data?.length}>
                        {options.isPending && <option value="">Finding qualified technicians…</option>}
                        {options.data && !nearest && <option value="">{busy.length ? 'Everyone qualified is out, resting or out of hours' : 'Nobody on duty is qualified for this'}</option>}
                        {technicians.length > 0 && (
                          <optgroup label="Technicians, nearest first">
                            {technicians.map((o, i) => (
                              <option key={o.label} value={o.label}>
                                {o.member} · {distance(o)}{afterFor(o)}{hoursFor(o)} · {o.role}, {o.crewName}{i === 0 ? ' ★ nearest' : ''}
                              </option>
                            ))}
                          </optgroup>
                        )}
                        {wholeCrews.length > 0 && (
                          <optgroup label="Whole crews">
                            {wholeCrews.map((o) => (
                              <option key={o.label} value={o.label}>{o.crewName} · {distance(o)}{afterFor(o)}{hoursFor(o)} · {o.crewOpenWorkOrders} open</option>
                            ))}
                          </optgroup>
                        )}
                        {busy.length > 0 && (
                          <optgroup label="Not available">
                            {busy.map((o) => (
                              <option key={o.label} value={o.label} disabled>
                                {o.member ?? `${o.crewName} (whole crew)`} · {whyNot(o)}
                              </option>
                            ))}
                          </optgroup>
                        )}
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
