import { useMemo, useState } from 'react';
import { useCircuits, useDrones, useFindings, useMissions, useTrack } from '../api';
import { FINDING_TYPE_LABEL, miles, pct, relativeTime } from '../lib/format';
import type { DroneAlert, MissionView } from '../types';
import { OpsMap } from './OpsMap';
import { SeverityBadge } from './SeverityBadge';
import { SidePanel } from './SidePanel';

export function OpsView({ onOpenFinding }: { onOpenFinding(id: string): void }) {
  const circuits = useCircuits();
  const drones = useDrones();
  const missions = useMissions();
  const findings = useFindings('open');
  const [focusId, setFocusId] = useState<string | null>(null);

  const active = useMemo(() => (missions.data ?? []).filter((m) => m.mission.status === 'InFlight' || m.mission.status === 'Planned'), [missions.data]);
  const recent = useMemo(() => (missions.data ?? []).filter((m) => m.mission.status === 'Completed' || m.mission.status === 'Aborted').slice(0, 3), [missions.data]);
  const focus = (missions.data ?? []).find((m) => m.mission.id === focusId) ?? active.find((m) => m.mission.status === 'InFlight') ?? null;
  const track = useTrack(focus?.mission.id ?? null, focus?.mission.status === 'InFlight');

  const alerts = useMemo(() => {
    const list: (DroneAlert & { droneId: string })[] = [];
    for (const m of (missions.data ?? []).slice(0, 10)) list.push(...m.mission.recentAlerts.map((a) => ({ ...a, droneId: m.mission.droneId })));
    return list.sort((a, b) => b.at.localeCompare(a.at)).slice(0, 8);
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

        <SidePanel id="alerts" title="Flight alerts" count={alerts.length}>
          {alerts.length === 0 && <p className="muted small">All clear.</p>}
          <ul className="alert-list">
            {alerts.map((a, i) => (
              <li key={i} className={a.level === 'Critical' ? 'alert-critical' : 'alert-warning'}>
                <span className="alert-dot" />
                <div>
                  <strong>{a.droneId}</strong> {a.message}
                  <div className="muted small">{relativeTime(a.at)}</div>
                </div>
              </li>
            ))}
          </ul>
        </SidePanel>

        <SidePanel id="triage" title="Needs triage" count={urgent.length}>
          {urgent.length === 0 && <p className="muted small">No high-severity findings waiting.</p>}
          {urgent.map((f) => (
            <button key={f.finding.id} className="finding-row" onClick={() => onOpenFinding(f.finding.id)}>
              <SeverityBadge severity={f.finding.severity} />
              <span className="grow">
                {FINDING_TYPE_LABEL[f.finding.type]}
                <span className="muted small"> · {f.finding.structureLabel}</span>
              </span>
              {f.overdue && <span className="pill pill-danger">overdue</span>}
            </button>
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
      </div>
    </button>
  );
}
