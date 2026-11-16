import { useMemo, useState } from 'react';
import { useAcknowledgeAlerts, useCircuits, useDrones, useFindings, useMissions, useTrack } from '../api';
import { FINDING_TYPE_LABEL, miles, pct, relativeTime } from '../lib/format';
import type { DroneAlert, MissionView } from '../types';
import { OpsMap } from './OpsMap';
import { SeverityBadge } from './SeverityBadge';
import { SidePanel } from './SidePanel';
import { useSession } from '../session';
import { SignalBars } from './SignalBars';

export function OpsView({ onOpenFinding, onOpenMission }: { onOpenFinding(id: string): void; onOpenMission(id: string): void }) {
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
      list.push(...m.mission.recentAlerts.map((a) => ({ ...a, droneId: m.mission.droneId, missionId: m.mission.id, circuitName: m.circuitName })));
    }
    list.sort((a, b) => b.at.localeCompare(a.at));
    return { open: list.filter((a) => !a.acknowledgedAt), acknowledged: list.filter((a) => a.acknowledgedAt).slice(0, 5) };
  }, [missions.data]);

  const urgent = (findings.data ?? []).filter((f) => f.finding.severity >= 4 && f.finding.status === 'New').slice(0, 5);

  return (
    <div className="ops">
      <div className="map-wrap">
        <OpsMap
          circuits={circuits.data ?? []}
          drones={drones.data ?? []}
          findings={findings.data ?? []}
          track={track.data ?? []}
          focus={focus ? circuits.data?.find((c) => c.id === focus.mission.circuitId) : null}
          onOpenFinding={onOpenFinding}
        />
        <div className="map-legend">
          <span><i style={{ background: '#14b8a6' }} />60 kV &amp; below</span>
          <span><i style={{ background: '#0ea5e9' }} />115 kV</span>
          <span><i style={{ background: '#a855f7' }} />230/500 kV</span>
          <span><i style={{ background: '#94a3b8' }} />Not tagged</span>
          <span><i className="legend-track" />Flight path</span>
          <span className="legend-hint">Zoom in for 115/60 kV lines and towers</span>
        </div>
      </div>

      <aside className="ops-side">
        <SidePanel id="active" title="Active missions" count={active.length}>
          {active.length === 0 && <p className="muted small">No drones in the air. Plan a mission and start the simulator.</p>}
          {active.map((m) => <MissionCard key={m.mission.id} m={m} selected={focus?.mission.id === m.mission.id} onSelect={() => setFocusId(m.mission.id)} />)}
        </SidePanel>

        <SidePanel id="recent" title="Recently completed" count={recent.length}>
          {recent.length === 0 && <p className="muted small">No finished missions yet.</p>}
          {recent.map((m) => <MissionCard key={m.mission.id} m={m} selected={focus?.mission.id === m.mission.id} onSelect={() => setFocusId(m.mission.id)} />)}
        </SidePanel>

        <SidePanel id="alerts" title="Flight alerts" count={open.length}>
          <FlightAlerts open={open} acknowledged={acknowledged} onOpenMission={onOpenMission} />
        </SidePanel>

        <SidePanel id="triage" title="Needs triage" count={urgent.length}>
          {urgent.length === 0 && <p className="muted small">No high-severity findings waiting.</p>}
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
        </SidePanel>
      </aside>
    </div>
  );
}

function MissionCard({ m, selected, onSelect }: { m: MissionView; selected: boolean; onSelect(): void }) {
  const mi = m.mission;
  const battery = mi.batteryPct ?? 100;
  return (
    <button className={`mission-card ${selected ? 'mission-card-selected' : ''}`} onClick={onSelect}>
      <div className="mission-card-top">
        <strong>{mi.droneId}</strong>
        <span className={`pill pill-${mi.status.toLowerCase()}`}>{mi.status === 'InFlight' ? 'In flight' : mi.status}</span>
      </div>
      <div className="muted small">{m.circuitName} · PIC {mi.pilot}</div>
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
