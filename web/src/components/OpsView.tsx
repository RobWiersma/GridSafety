import { useEffect, useMemo, useState } from 'react';
import { useAcknowledgeAlerts, useBulkFindings, type BulkResult, useCircuits, useDrones, useFieldJobs, useFindings, useMissions, useResources, useTrack } from '../api';
import { REST_HOURS, SHIFT_HOURS, phaseAt, positionAt, roadAhead, statusText, tripKey, truckName, useServerClock } from '../lib/field';
import { ShiftMeter } from './CrewsPanel';
import { FINDING_TYPE_LABEL, flightLabel, miles, pct, relativeTime } from '../lib/format';
import type { DroneAlert, MissionView } from '../types';
import { OpsMap, pickable, type DepotPin, type TruckPin } from './OpsMap';
import type { ToastMessage } from './Toast';
import { SeverityBadge } from './SeverityBadge';
import { SeverityFilter, useSeverityFilter } from './SeverityFilter';
import { PanelStack, SidePanel } from './SidePanel';
import { useSession } from '../session';
import { SignalBars } from './SignalBars';

export function OpsView({ onOpenFinding, onOpenMission, onOpenTriageQueue, onToast }: {
  onOpenFinding(id: string): void;
  onOpenMission(id: string): void;
  onOpenTriageQueue(): void;
  onToast(t: ToastMessage): void;
}) {
  const circuits = useCircuits();
  const drones = useDrones();
  const missions = useMissions();
  const findings = useFindings('open');
  const [focusId, setFocusId] = useState<string | null>(null);

  const active = useMemo(() => (missions.data ?? []).filter((m) => m.mission.status === 'InFlight' || m.mission.status === 'Planned'), [missions.data]);
  const recent = useMemo(() => (missions.data ?? []).filter((m) => m.mission.status === 'Completed' || m.mission.status === 'Aborted').slice(0, 3), [missions.data]);
  const focus = (missions.data ?? []).find((m) => m.mission.id === focusId) ?? active.find((m) => m.mission.status === 'InFlight') ?? null;
  const track = useTrack(focus?.mission.id ?? null, focus?.mission.status === 'InFlight');

  const { open, acknowledged } = useMemo(() => {
    const list: OpsAlert[] = [];
    for (const m of (missions.data ?? []).slice(0, 10)) {
      list.push(...m.mission.recentAlerts.map((a) => ({ ...a, droneId: m.mission.name ?? m.mission.droneId, missionId: m.mission.id, circuitName: m.circuitName })));
    }
    list.sort((a, b) => b.at.localeCompare(a.at));
    return { open: list.filter((a) => !a.acknowledgedAt), acknowledged: list.filter((a) => a.acknowledgedAt).slice(0, 5) };
  }, [missions.data]);

  const flightNames = useMemo(() => Object.fromEntries((missions.data ?? []).filter((m) => m.mission.name).map((m) => [m.mission.id, m.mission.name!])), [missions.data]);

  // Where the ground crews depart from: each technician's own depot, or their crew's.
  const resources = useResources();
  const [showDepots, setShowDepots] = useState(() => {
    try { return localStorage.getItem('gs.ops.depots') !== 'off'; } catch { return true; }
  });
  const toggleDepots = () => {
    setShowDepots((on) => {
      try { localStorage.setItem('gs.ops.depots', on ? 'off' : 'on'); } catch { /* storage blocked */ }
      return !on;
    });
  };
  const depotPins = useMemo<DepotPin[]>(() => {
    const r = resources.data;
    if (!r) return [];
    return r.depots.map((depot) => ({
      depot,
      people: r.groundCrews.flatMap(({ crew }) => crew.members
        .filter((m) => (m.depotId ?? crew.depotId) === depot.id)
        .map((m) => ({ name: m.name, role: m.role, crewName: crew.name, offDuty: crew.status === 'OffDuty' }))),
    }));
  }, [resources.data]);

  // Field crews out on jobs: positions come from the job's timestamps, so trucks move every second between polls.
  const field = useFieldJobs();
  const fieldNow = useServerClock(field.data?.now);
  const trucks: TruckPin[] = (field.data?.jobs ?? []).map((j) => {
    const phase = phaseAt(j.dispatch, fieldNow);
    return {
      findingId: j.findingId,
      label: truckName(j.dispatch),
      structureLabel: j.structureLabel,
      dispatch: j.dispatch,
      phase,
      position: positionAt(j.dispatch, fieldNow),
      ahead: roadAhead(j.dispatch, fieldNow),
      status: statusText(j.dispatch, fieldNow),
    };
  });
  const out = trucks.filter((tr) => tr.phase !== 'Back' && tr.phase !== 'MovedOn');
  // One row per truck: its current job, with the stops lined up after it.
  const people = [...new Set(out.map((tr) => tripKey(tr.dispatch)))].map((who) => {
    const jobs = out.filter((tr) => tripKey(tr.dispatch) === who);
    return { who, now: jobs.find((tr) => tr.phase !== 'Queued') ?? jobs[0], next: jobs.filter((tr) => tr.phase === 'Queued') };
  });

  // Hours of service: who's working a shift, who's resting before their next one.
  const shiftBoard = useMemo(() => {
    const rows = (resources.data?.groundCrews ?? []).filter(({ crew }) => crew.status !== 'OffDuty')
      .flatMap(({ crew, shifts = [] }) => shifts.map((s) => ({ ...s, crewName: crew.name })));
    return {
      resting: rows.filter((s) => s.restingUntil).sort((a, b) => a.restingUntil!.localeCompare(b.restingUntil!)),
      working: rows.filter((s) => !s.restingUntil && s.bookedHours > 0).sort((a, b) => b.bookedHours - a.bookedHours),
      fresh: rows.filter((s) => !s.restingUntil && s.bookedHours === 0).length,
    };
  }, [resources.data]);

  // The severity chips filter both the map and the triage panel, so the two always agree.
  const severity = useSeverityFilter();
  const allOpen = findings.data ?? [];
  const sevCounts = useMemo(() => {
    const c: Record<number, number> = {};
    for (const f of allOpen) c[f.finding.severity] = (c[f.finding.severity] ?? 0) + 1;
    return c;
  }, [allOpen]);
  const visible = allOpen.filter((f) => severity.shown.has(f.finding.severity));

  // Every untriaged finding, in work-queue order (overdue, then severity, then oldest: the API sorts it that way).
  const untriaged = visible.filter((f) => f.finding.status === 'New');
  const urgent = untriaged.slice(0, 5);

  // Picking several findings on the map to triage or assign together (signed-in users only).
  const session = useSession();
  const [selectMode, setSelectMode] = useState(false);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const selected = visible.filter((f) => picked.has(f.finding.id) && pickable(f));
  const selectedIds = useMemo(() => new Set(selected.map((f) => f.finding.id)), [selected.map((f) => f.finding.id).join()]); // eslint-disable-line react-hooks/exhaustive-deps
  const onSelect = (ids: string[], how: 'toggle' | 'add') =>
    setPicked((cur) => {
      const next = new Set([...cur].filter((id) => selectedIds.has(id)));
      for (const id of ids) {
        if (how === 'toggle' && next.has(id)) next.delete(id);
        else next.add(id);
      }
      return next;
    });
  const clearSelection = () => { setPicked(new Set()); setSelectMode(false); };
  useEffect(() => {
    if (!selectMode && picked.size === 0) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && clearSelection();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [selectMode, picked.size]);
  const bulk = useBulkFindings();
  const [working, setWorking] = useState<string | null>(null);
  const runBulk = async (verb: string, action: typeof bulk.triage, items = selected) => {
    setWorking(`${verb} 1 of ${items.length}…`);
    const report = (r: BulkResult) => {
      const who = Object.entries(r.done.reduce<Record<string, number>>((acc, d) => (d.who ? { ...acc, [d.who]: (acc[d.who] ?? 0) + 1 } : acc), {}))
        .map(([name, n]) => (n > 1 ? `${name} ×${n}` : name)).join(', ');
      const ok = r.done.length ? `${verb === 'Assigning' ? 'Assigned' : 'Triaged'} ${r.done.length}${who ? `: ${who}` : ''}.` : '';
      const bad = r.failed.map((x) => `${x.label}: ${x.reason}`).join('; ');
      onToast({ kind: r.failed.length && !r.done.length ? 'error' : 'success', text: [ok, bad && `Not done: ${bad}`].filter(Boolean).join(' ') });
    };
    try {
      const r = await action(items, (i) => setWorking(`${verb} ${i + 1} of ${items.length}…`));
      report(r);
      // Assigned ones drop out of the selection by themselves (no longer pickable); triaged ones stay, ready to assign.
    } finally {
      setWorking(null);
    }
  };
  const newCount = selected.filter((f) => f.finding.status === 'New').length;

  return (
    <div className="ops">
      <div className="map-wrap">
        <OpsMap
          circuits={circuits.data ?? []}
          drones={drones.data ?? []}
          findings={visible}
          flightNames={flightNames}
          depots={showDepots ? depotPins : []}
          trucks={trucks}
          track={track.data ?? []}
          focus={focus ? circuits.data?.find((c) => c.id === focus.mission.circuitId) : null}
          onOpenFinding={onOpenFinding}
          selected={selectedIds}
          selectMode={selectMode}
          onSelect={session.guest ? undefined : onSelect}
        />
        {!session.guest && (
          <div className={`select-bar ${selected.length ? 'select-bar-open' : ''}`} role="toolbar" aria-label="Bulk actions on selected findings">
            <button className={`btn btn-xs ${selectMode ? 'btn-primary' : 'btn-secondary'}`} aria-pressed={selectMode}
              onClick={() => setSelectMode((on) => !on)}
              title="Select mode: click findings to pick them, or drag a box. Without it, Shift/Ctrl-click or Shift-drag.">
              {selectMode ? 'Selecting' : 'Select'}
            </button>
            {selected.length > 0 && (
              <>
                <span className="small"><strong>{selected.length}</strong> selected</span>
                {working ? <span className="muted small">{working}</span> : (
                  <>
                    <button className="btn btn-secondary btn-xs" disabled={!newCount} onClick={() => runBulk('Triaging', bulk.triage)}
                      title={newCount ? 'Mark the untriaged ones as triaged' : 'All selected findings are already triaged'}>
                      Triage{newCount ? ` ${newCount}` : ''}
                    </button>
                    <button className="btn btn-primary btn-xs" onClick={() => runBulk('Assigning', bulk.assignNearest)}
                      title="Worst first, each to the nearest qualified technician who's free; nearby jobs line up behind one truck">
                      Assign nearest
                    </button>
                  </>
                )}
                <button className="btn btn-ghost btn-xs" onClick={clearSelection} disabled={Boolean(working)}>Clear</button>
              </>
            )}
            {selectMode && !selected.length && <span className="muted small">Click findings or drag a box</span>}
          </div>
        )}
        <SeverityFilter counts={sevCounts} shown={severity.shown} onToggle={severity.toggle} onOnly={severity.only} onAll={severity.all} />
        <div className="map-legend">
          <span><i style={{ background: '#14b8a6' }} />60 kV &amp; below</span>
          <span><i style={{ background: '#0ea5e9' }} />115 kV</span>
          <span><i style={{ background: '#a855f7' }} />230/500 kV</span>
          <span><i style={{ background: '#94a3b8' }} />Not tagged</span>
          <span><i className="legend-track" />Flight path</span>
          <span><i className="legend-truck" />Field crew</span>
          <button className={`legend-toggle ${showDepots ? 'legend-toggle-on' : ''}`} aria-pressed={showDepots} onClick={toggleDepots}
            title="Show or hide the yards ground crews depart from">
            <i className="legend-depot" />Crew depots
          </button>
          <span className="legend-hint">Zoom in for 115/60 kV lines and towers</span>
        </div>
      </div>

      <PanelStack storageKey="gs.ops.panelOrder" className="ops-side" panels={[
          { id: 'active', node: (
            <SidePanel id="active" title="Active missions" count={active.length}>
              {active.length === 0 && <p className="muted small">No drones in the air. Plan a mission and start the simulator.</p>}
              {active.map((m) => <MissionCard key={m.mission.id} m={m} selected={focus?.mission.id === m.mission.id} onSelect={() => setFocusId(m.mission.id)} />)}
            </SidePanel>
          ) },
          { id: 'recent', node: (
            <SidePanel id="recent" title="Recently completed" count={recent.length}>
              {recent.length === 0 && <p className="muted small">No finished missions yet.</p>}
              {recent.map((m) => <MissionCard key={m.mission.id} m={m} selected={focus?.mission.id === m.mission.id} onSelect={() => setFocusId(m.mission.id)} />)}
            </SidePanel>
          ) },
          { id: 'field', node: (
            <SidePanel id="field" title="In the field" count={people.length}>
              {people.length === 0 && <p className="muted small">No trucks out. Assign a work order to send the nearest qualified technician; someone from the same yard rides along when there's a seat.</p>}
              {people.map(({ who, now: tr, next }) => (
                <button key={who} className="finding-row field-row" onClick={() => onOpenFinding(tr.findingId)} title="Open the work order">
                  <span className={`field-dot field-${tr.phase.toLowerCase()}`} aria-hidden="true" />
                  <span className="grow">
                    {tr.label}
                    <span className="muted small"> · {tr.structureLabel}</span>
                    <span className="muted small field-status">{tr.status}</span>
                    {next.length > 0 && <span className="muted small field-status">Then {next.map((n) => n.structureLabel).join(', ')}</span>}
                  </span>
                </button>
              ))}
              {out.length > 0 && <p className="muted small field-note">Simulated and sped up: a real truck roll takes hours.</p>}
            </SidePanel>
          ) },
          { id: 'shifts', node: (
            <SidePanel id="shifts" title="Crew shifts" count={shiftBoard.working.length + shiftBoard.resting.length}>
              {shiftBoard.working.length + shiftBoard.resting.length === 0 && (
                <p className="muted small">Everyone is fresh. Each technician works up to {SHIFT_HOURS} h of driving and repairs, then rests {REST_HOURS} h.</p>
              )}
              {shiftBoard.working.map((s) => (
                <div key={`${s.crewName}/${s.member}`} className="shift-row">
                  <span className="grow">{s.member}<span className="muted small"> · {s.crewName}</span></span>
                  <ShiftMeter shift={s} now={fieldNow} />
                </div>
              ))}
              {shiftBoard.resting.map((s) => (
                <div key={`${s.crewName}/${s.member}`} className="shift-row muted">
                  <span className="grow">{s.member}<span className="small"> · {s.crewName}</span></span>
                  <ShiftMeter shift={s} now={fieldNow} />
                </div>
              ))}
              {shiftBoard.working.length + shiftBoard.resting.length > 0 && (
                <p className="muted small field-note">{shiftBoard.fresh} more fresh and ready. Shifts are {SHIFT_HOURS} h of driving and repairs, then {REST_HOURS} h off (sped up: 1 s = 1 min).</p>
              )}
            </SidePanel>
          ) },
          { id: 'alerts', node: (
            <SidePanel id="alerts" title="Flight alerts" count={open.length}>
              <FlightAlerts open={open} acknowledged={acknowledged} onOpenMission={onOpenMission} />
            </SidePanel>
          ) },
          { id: 'triage', node: (
            <SidePanel id="triage" title="Needs triage" count={untriaged.length}>
              {untriaged.length === 0 && <p className="muted small">{visible.length < allOpen.length ? 'Nothing waiting for triage at the selected severities.' : 'Nothing waiting for triage.'}</p>}
              {urgent.map((f) => (
                <div key={f.finding.id} className="triage-row">
                  <button className="finding-row" onClick={() => onOpenFinding(f.finding.id)} title="Open the finding">
                    <SeverityBadge severity={f.finding.severity} />
                    <span className="grow">
                      {FINDING_TYPE_LABEL[f.finding.type]}
                      <span className="muted small"> · {f.finding.structureLabel}</span>
                    </span>
                    {f.overdue && <span className="pill pill-danger">overdue</span>}
                  </button>
                  <button className="btn btn-ghost btn-xs" onClick={() => onOpenMission(f.finding.missionId)} title="Open the flight that found it">
                    Flight →
                  </button>
                </div>
              ))}
              {untriaged.length > urgent.length && (
                <button className="link-button" onClick={onOpenTriageQueue}>
                  View all {untriaged.length} in Findings →
                </button>
              )}
            </SidePanel>
          ) },
      ]} />
    </div>
  );
}

function MissionCard({ m, selected, onSelect }: { m: MissionView; selected: boolean; onSelect(): void }) {
  const mi = m.mission;
  const battery = mi.batteryPct ?? 100;
  return (
    <button className={`mission-card ${selected ? 'mission-card-selected' : ''}`} onClick={onSelect}>
      <div className="mission-card-top">
        <strong>{mi.name ?? mi.droneId}</strong>
        <span className={`pill pill-${mi.status.toLowerCase()}`}>{mi.status === 'InFlight' ? 'In flight' : mi.status}</span>
      </div>
      <div className="muted small">{mi.name ? `Drone ${mi.droneId} · ` : ''}{m.circuitName}{flightLabel(m.flight, false) ? ` · ${flightLabel(m.flight, false)}` : ''} · PIC {mi.pilot}</div>
      <div className="progress" title={`Coverage ${pct(m.coverage)}`}>
        <span style={{ width: pct(m.coverage) }} />
      </div>
      <div className="mission-card-meta">
        <span>{pct(m.coverage)} covered</span>
        <span>{miles(mi.distanceFlownMeters)} mi</span>
        <span className={battery <= 15 ? 'text-danger' : battery <= 30 ? 'text-warn' : ''}>🔋 {Math.round(battery)}%</span>
        <span>⚠ {mi.findingCount}</span>
        {mi.status === 'InFlight' && <SignalBars rssi={mi.rssiDbm} />}
      </div>
    </button>
  );
}

type OpsAlert = DroneAlert & { droneId: string; missionId: string; circuitName: string };

const MAX_OPEN_SHOWN = 8;

/** Open alerts with an Acknowledge button each (signed-in users); acknowledged ones fold away below. */
function FlightAlerts({ open, acknowledged, onOpenMission }: { open: OpsAlert[]; acknowledged: OpsAlert[]; onOpenMission(id: string): void }) {
  const session = useSession();
  const ack = useAcknowledgeAlerts();
  const [showAcked, setShowAcked] = useState(false);
  const canAck = !session.guest;
  const shown = open.slice(0, MAX_OPEN_SHOWN);

  const acknowledgeAll = () => {
    for (const missionId of new Set(open.map((a) => a.missionId))) ack.mutate({ missionId });
  };

  return (
    <>
      {open.length === 0 && <p className="muted small">All clear. No alerts need attention.</p>}
      {canAck && open.length > 1 && (
        <button className="btn btn-secondary btn-xs ack-all" onClick={acknowledgeAll} disabled={ack.isPending}>✓ Acknowledge all ({open.length})</button>
      )}
      <ul className="alert-list">
        {shown.map((a) => (
          <li key={`${a.missionId}|${a.at}|${a.kind}`} className={`alert-item ${a.level === 'Critical' ? 'alert-critical' : 'alert-warning'}`}>
            <AlertRow a={a} onOpenMission={onOpenMission} />
            {canAck && (
              <button className="btn btn-ghost btn-xs ack-btn" disabled={ack.isPending}
                onClick={() => ack.mutate({ missionId: a.missionId, at: a.at, kind: a.kind })}
                aria-label={`Acknowledge: ${a.droneId} ${a.message}`} title="Acknowledge">
                ✓ Ack
              </button>
            )}
          </li>
        ))}
      </ul>
      {open.length > shown.length && <p className="muted small">+{open.length - shown.length} older open alerts</p>}
      {ack.isError && <p className="small text-danger">Couldn't acknowledge: {ack.error.message}</p>}

      {acknowledged.length > 0 && (
        <>
          <button className="link-button small" onClick={() => setShowAcked((v) => !v)} aria-expanded={showAcked}>
            {showAcked ? 'Hide' : 'Show'} recently acknowledged ({acknowledged.length})
          </button>
          {showAcked && (
            <ul className="alert-list alert-list-acked">
              {acknowledged.map((a) => (
                <li key={`${a.missionId}|${a.at}|${a.kind}`} className="alert-item alert-acked">
                  <AlertRow a={a} onOpenMission={onOpenMission} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </>
  );
}

function AlertRow({ a, onOpenMission }: { a: OpsAlert; onOpenMission(id: string): void }) {
  return (
    <button className="alert-row" onClick={() => onOpenMission(a.missionId)} title={`Open the flight overview for ${a.circuitName}`}>
      <span className="alert-dot" />
      <span className="grow">
        <strong>{a.droneId}</strong> {a.message}
        <span className="muted small alert-meta">
          {relativeTime(a.at)}
          {a.acknowledgedAt && <> · ✓ acknowledged by {a.acknowledgedBy ?? 'ops'} {relativeTime(a.acknowledgedAt)}</>}
          {' · '}<span className="link">Flight overview →</span>
        </span>
      </span>
    </button>
  );
}
