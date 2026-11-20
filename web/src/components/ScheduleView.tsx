import { useMemo, useState } from 'react';
import { useDispatchSchedule, useSchedule } from '../api';
import { FINDING_TYPE_LABEL, severityColor } from '../lib/format';
import { useSession } from '../session';
import type { ScheduledJob, ScheduleDispatchResult } from '../types';
import { SeverityBadge } from './SeverityBadge';
import type { ToastMessage } from './Toast';

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
const day = (iso: string) => new Date(iso).toLocaleDateString([], { month: 'short', day: 'numeric' });

/** "now", "in 12 min", "in 2 h 5 min", relative to the server's clock. */
function fromNow(iso: string, now: number): string {
  const min = Math.round((Date.parse(iso) - now) / 60_000);
  if (min <= 0) return 'now';
  return min < 60 ? `in ${min} min` : `in ${Math.floor(min / 60)} h${min % 60 ? ` ${min % 60} min` : ''}`;
}

/**
 * The forward schedule: every triaged work order nobody has been sent to yet, ranked by importance, each planned onto
 * the technician who'd get it done soonest (around the jobs they're on, their shift hours and rests, and the trucks at
 * their yard), with a timeline per technician. Signed-in users send everything that can start now in one go.
 */
export function ScheduleView({ onOpenFinding, onToast }: { onOpenFinding(id: string): void; onToast(t: ToastMessage): void }) {
  const { guest } = useSession();
  const schedule = useSchedule();
  const dispatch = useDispatchSchedule();
  const [result, setResult] = useState<ScheduleDispatchResult | null>(null);
  const data = schedule.data;
  const now = data ? Date.parse(data.now) : Date.now();
  const jobs = data?.jobs ?? [];
  const ready = jobs.filter((j) => j.startsNow && j.assignee);
  const late = jobs.filter((j) => j.late).length;
  const blocked = jobs.filter((j) => j.blocked).length;
  const clearBy = jobs.reduce<string | null>((a, j) => (j.backAt && (!a || j.backAt > a) ? j.backAt : a), null);

  const send = async (findingId?: string) => {
    try {
      const r = await dispatch.mutateAsync(findingId);
      setResult(r);
      onToast(r.dispatched.length ? { kind: 'success', text: `Sent ${r.dispatched.length} work order${r.dispatched.length === 1 ? '' : 's'}` }
        : { kind: 'error', text: 'Nothing could be sent just now' });
    } catch (err) {
      onToast({ kind: 'error', text: (err as Error).message });
    }
  };

  return (
    <div className="schedule">
      <section className="panel">
        <div className="schedule-head">
          <div>
            <h2>Work schedule</h2>
            <p className="muted small">
              Triaged work orders nobody has been sent to yet, most important first. Each is planned onto the qualified technician who'd
              be done soonest, around the jobs they're on, their {data?.shiftHours ?? 8} h shifts and {data?.restHours ?? 10} h rests,
              and the trucks at their yard. The plan is redone as things change; times are estimates.
            </p>
          </div>
          {!guest && (
            <button className="btn btn-primary" disabled={!ready.length || dispatch.isPending} onClick={() => send()}>
              {dispatch.isPending ? 'Sending…' : ready.length ? `Dispatch ${ready.length} ready now` : 'Nothing ready now'}
            </button>
          )}
        </div>
        <div className="report-tiles">
          <Tile label="Waiting">{jobs.length}{data?.untriaged ? <span className="muted small"> · {data.untriaged} to triage</span> : null}</Tile>
          <Tile label="Can start now">{ready.length}</Tile>
          <Tile label="Late for repair-by">{late ? <span className="danger-text">{late}</span> : 0}</Tile>
          <Tile label="Backlog clear">{clearBy ? fromNow(clearBy, now) : '—'}{blocked ? <span className="muted small"> · {blocked} can't be planned</span> : null}</Tile>
        </div>
        {result && (result.dispatched.length > 0 || result.skipped.length > 0) && (
          <div className="banner banner-info schedule-result">
            {result.dispatched.length > 0 && <div>Sent: {result.dispatched.map((d) => `${d.structureLabel} → ${d.assignee}`).join('; ')}</div>}
            {result.skipped.map((s) => <div key={s.findingId}>Not sent: {s.structureLabel}: {s.reason}</div>)}
            {result.more && <div>More are ready: dispatch again to send the rest.</div>}
            <button className="link-button small" onClick={() => setResult(null)}>Dismiss</button>
          </div>
        )}
      </section>

      {schedule.isLoading && <p className="muted">Planning…</p>}
      {schedule.error && <div className="banner banner-error">{(schedule.error as Error).message}</div>}
      {data && jobs.length === 0 && (
        <section className="panel"><p className="muted">Nothing waiting: every triaged work order has someone on it.{data.untriaged ? ` ${data.untriaged} finding(s) still need triage.` : ''}</p></section>
      )}

      {jobs.length > 0 && <Timeline jobs={jobs} now={now} onOpen={onOpenFinding} />}

      {jobs.length > 0 && (
        <section className="panel panel-flush">
          <div className="table-wrap">
            <table className="table schedule-table">
              <thead>
                <tr><th>#</th><th>Work order</th><th>Why</th><th>Planned for</th><th>Sets off</th><th>Repaired by</th><th>Repair-by</th></tr>
              </thead>
              <tbody>
                {jobs.map((j) => (
                  <tr key={j.findingId} className={j.startsNow ? 'schedule-ready' : undefined}>
                    <td className="mono">{j.rank}</td>
                    <td>
                      <button className="link-button schedule-wo" onClick={() => onOpenFinding(j.findingId)}>
                        <SeverityBadge severity={j.severity} />
                        <span><strong>{j.structureLabel}</strong><br /><span className="muted small">{FINDING_TYPE_LABEL[j.type]}</span></span>
                      </button>
                    </td>
                    <td>
                      <div className="schedule-score" title={`Priority score ${j.score}`}>
                        <i style={{ width: `${Math.min(100, j.score / 1.8)}%`, background: severityColor(j.severity) }} />
                      </div>
                      <span className="small muted">{j.reasons.join(' · ')}</span>
                    </td>
                    <td>
                      {j.assignee ? (
                        <>
                          {j.assignee}
                          <br /><span className="muted small">{j.depotName}{j.vehicleId ? ` · ${j.vehicleId}` : ''}</span>
                        </>
                      ) : <span className="danger-text small">{j.blocked}</span>}
                    </td>
                    <td>
                      {j.startsAt && (j.startsNow ? (
                        <>
                          <span className="pill pill-accent">Ready now</span>
                          {!guest && j.assignee && (
                            <><br /><button className="link-button small" disabled={dispatch.isPending} onClick={() => send(j.findingId)}
                              aria-label={`Send ${j.assignee} to ${j.structureLabel}`}>Send</button></>
                          )}
                        </>
                      )
                        : <>{fromNow(j.startsAt, now)}<br /><span className="muted small">{clock(j.startsAt)}</span></>)}
                    </td>
                    <td>{j.repairedAt ? <>{fromNow(j.repairedAt, now)}<br /><span className="muted small">{clock(j.repairedAt)}</span></> : '—'}</td>
                    <td>
                      {j.repairBy ? <span className={j.late ? 'danger-text' : undefined}>{day(j.repairBy)}{j.late ? ' · late' : ''}</span> : <span className="muted small">as time allows</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}

/** One lane per technician with their planned jobs on a shared time axis, from now to when the backlog clears. */
function Timeline({ jobs, now, onOpen }: { jobs: ScheduledJob[]; now: number; onOpen(id: string): void }) {
  const lanes = useMemo(() => {
    const by = new Map<string, ScheduledJob[]>();
    for (const j of jobs) if (j.assignee && j.startsAt && j.backAt) by.set(j.assignee, [...(by.get(j.assignee) ?? []), j]);
    return [...by.entries()].sort((a, b) => Date.parse(a[1][0].startsAt!) - Date.parse(b[1][0].startsAt!));
  }, [jobs]);
  if (!lanes.length) return null;
  const end = Math.max(now + 60_000, ...jobs.filter((j) => j.backAt).map((j) => Date.parse(j.backAt!)));
  const span = end - now;
  const pos = (iso: string) => Math.max(0, Math.min(100, ((Date.parse(iso) - now) / span) * 100));
  const ticks = 4;
  return (
    <section className="panel schedule-timeline" aria-label="Timeline by technician">
      <h2>Timeline</h2>
      <div className="schedule-axis">
        <span />
        <div>{Array.from({ length: ticks + 1 }, (_, i) => <span key={i} style={{ left: `${(i / ticks) * 100}%` }}>{i === 0 ? 'now' : clock(new Date(now + (span * i) / ticks).toISOString())}</span>)}</div>
      </div>
      {lanes.map(([who, js]) => (
        <div key={who} className="schedule-lane">
          <span className="small" title={who}>{who.replace(/ \(.*\)$/, '')}</span>
          <div className="schedule-track">
            {js.map((j) => (
              <button key={j.findingId} className="schedule-bar" onClick={() => onOpen(j.findingId)}
                style={{ left: `${pos(j.startsAt!)}%`, width: `max(${pos(j.backAt!) - pos(j.startsAt!)}%, 6px)`, background: severityColor(j.severity) }}
                title={`#${j.rank} ${j.structureLabel} · ${FINDING_TYPE_LABEL[j.type]} · ${clock(j.startsAt!)}–${clock(j.backAt!)}`}
                aria-label={`#${j.rank} ${j.structureLabel}, ${clock(j.startsAt!)} to ${clock(j.backAt!)}`}>
                <span>{j.rank}</span>
              </button>
            ))}
          </div>
        </div>
      ))}
    </section>
  );
}

function Tile({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="report-tile"><span className="muted small">{label}</span><strong>{children}</strong></div>;
}
