import L from 'leaflet';
import { useMemo } from 'react';
import { CircleMarker, MapContainer, Polyline, TileLayer, Tooltip } from 'react-leaflet';
import { useCircuits, useFindingImage, useMission, useMissionFindings, useOpenReport, useTrack } from '../api';
import { FINDING_TYPE_LABEL, MISSION_STATUS_LABEL, miles, pct, relativeTime, severityHex, voltageHex } from '../lib/format';
import type { Circuit, DroneAlert, FindingView, Mission, TelemetryPoint } from '../types';
import { WEAK_SIGNAL_DBM } from './SignalBars';
import { SeverityBadge } from './SeverityBadge';
import type { ToastMessage } from './Toast';

interface Props {
  missionId: string;
  onBack(): void;
  onOpenFinding(id: string): void;
  onToast(t: ToastMessage): void;
}

const when = (iso?: string) =>
  iso ? new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' }) : '–';

function duration(m: Mission, now = Date.now()): string {
  if (!m.startedAt) return '–';
  const ms = (m.endedAt ? Date.parse(m.endedAt) : now) - Date.parse(m.startedAt);
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}m ${String(s % 60).padStart(2, '0')}s`;
}

/** Mission overview: what was flown, what was seen, what went wrong, and what is left to inspect. */
export function MissionDetail({ missionId, onBack, onOpenFinding, onToast }: Props) {
  const mission = useMission(missionId);
  const live = mission.data?.mission.status === 'InFlight';
  const track = useTrack(missionId, live);
  const findings = useMissionFindings(missionId, live);
  const circuits = useCircuits();
  const openReport = useOpenReport();

  const m = mission.data?.mission;
  const circuit = useMemo(() => circuits.data?.find((c) => c.id === m?.circuitId) ?? null, [circuits.data, m?.circuitId]);
  const points = track.data ?? [];
  const items = useMemo(() => [...(findings.data ?? [])].sort((a, b) => b.finding.severity - a.finding.severity), [findings.data]);

  if (mission.isError) {
    return (
      <section className="panel">
        <button className="btn btn-ghost back-link" onClick={onBack}>← Missions</button>
        <p className="muted">Mission {missionId} was not found.</p>
      </section>
    );
  }
  if (!m || !mission.data) return <div className="panel"><div className="spinner" aria-label="Loading" /></div>;

  const inspected = new Set(m.structuresInspected);
  const missed = circuit?.structures.filter((s) => !inspected.has(s.id)) ?? [];
  const highSev = items.filter((f) => f.finding.severity >= 4).length;
  const critical = m.recentAlerts.filter((a) => a.level === 'Critical').length;
  const maxAlt = points.length ? Math.max(...points.map((p) => p.altitudeM)) : null;
  const minBattery = points.length ? Math.min(...points.map((p) => p.batteryPct)) : m.batteryPct ?? null;
  const signal = points.filter((p) => p.rssiDbm != null);

  return (
    <div className="mission-detail">
      <section className="panel mission-head">
        <button className="btn btn-ghost back-link" onClick={onBack}>← Missions</button>
        <div className="mission-title">
          <div>
            <h2>{m.name ?? mission.data.circuitName}</h2>
            <p className="muted">
              {m.name && <>{mission.data.circuitName} · </>}
              <span className="mono">{m.id}</span> · {m.droneId} · PIC {m.pilot} · planned {relativeTime(m.createdAt)}
            </p>
          </div>
          <div className="toolbar-actions">
            <span className={`pill pill-${m.status.toLowerCase()}`}>{MISSION_STATUS_LABEL[m.status]}</span>
            {m.reportKey && (
              <button className="btn btn-secondary" onClick={() => openReport(m.id).catch((e: Error) => onToast({ kind: 'error', text: e.message }))}>
                Report ↗
              </button>
            )}
          </div>
        </div>
      </section>

      <section className="stats stats-6" aria-label="Mission summary">
        <Tile label="Coverage" value={pct(mission.data.coverage)} sub={`${m.structuresInspected.length} / ${circuit?.structures.length ?? '–'} structures`} />
        <Tile label="Distance" value={`${miles(m.distanceFlownMeters)} mi`} sub={circuit ? `${circuit.voltageKv > 0 ? `${circuit.voltageKv} kV` : 'voltage not tagged'} circuit` : ' '} />
        <Tile label="Flight time" value={duration(m)} sub={m.startedAt ? `${when(m.startedAt)} → ${m.endedAt ? when(m.endedAt) : 'now'}` : 'not started'} />
        <Tile label="Findings" value={String(items.length)} sub={`${highSev} high / critical`} />
        <Tile label="Flight alerts" value={String(m.recentAlerts.length)} sub={`${critical} critical`} danger={critical > 0} />
        <Tile label="Peak altitude" value={maxAlt == null ? '–' : `${Math.round(maxAlt)} m`} sub={minBattery == null ? ' ' : `battery low ${Math.round(minBattery)}%`} />
      </section>

      <div className="mission-grid">
        <section className="panel panel-flush">
          <div className="panel-toolbar"><h2>Flight</h2><span className="muted small">{points.length} telemetry points</span></div>
          {circuit ? <MissionMap circuit={circuit} inspected={inspected} track={points} findings={items} alerts={m.recentAlerts} onOpenFinding={onOpenFinding} />
            : <div className="map map-empty">Loading map…</div>}
          <div className="map-legend-row small muted">
            <span><i className="dot" style={{ background: '#22c55e' }} /> inspected</span>
            <span><i className="dot" style={{ background: '#64748b' }} /> not inspected</span>
            <span><i className="line" style={{ background: '#f43f5e' }} /> flight path</span>
            <span><i className="dot" style={{ background: severityHex(4) }} /> finding</span>
            <span><i className="dot ring" /> alert</span>
          </div>
        </section>

        <section className="panel">
          <h2>Flight profile</h2>
          {points.length > 1 ? (
            <>
              <Profile label="Altitude" unit="m" points={points} pick={(p) => p.altitudeM} color="#38bdf8" limit={120} limitLabel="120 m ceiling" />
              <Profile label="Battery" unit="%" points={points} pick={(p) => p.batteryPct} color="#22c55e" limit={30} limitLabel="30% low" min={0} max={100} />
              <Profile label="Speed" unit="m/s" points={points} pick={(p) => p.speedMps} color="#f59e0b" />
              {signal.length > 1 && (
                <Profile label="Control link (RSSI)" unit="dBm" points={signal} pick={(p) => p.rssiDbm!} color="#a78bfa"
                  limit={WEAK_SIGNAL_DBM} limitLabel={`${WEAK_SIGNAL_DBM} dBm weak`} max={-30} />
              )}
            </>
          ) : <p className="muted">No telemetry yet.</p>}
        </section>
      </div>

      <section className="panel">
        <h2>Findings ({items.length})</h2>
        {items.length === 0 ? <p className="muted">No issues reported on this patrol.</p> : (
          <div className="capture-grid">
            {items.map((f) => <CaptureCard key={f.finding.id} view={f} onOpen={() => onOpenFinding(f.finding.id)} />)}
          </div>
        )}
      </section>

      <div className="mission-grid mission-grid-even">
        <section className="panel">
          <h2>Flight alerts</h2>
          {m.recentAlerts.length === 0 ? <p className="muted">No alerts on this flight.</p> : (
            <ul className="timeline">
              {[...m.recentAlerts].sort((a, b) => a.at.localeCompare(b.at)).map((a, i) => (
                <li key={i}>
                  <span className={`pill ${a.level === 'Critical' ? 'pill-danger' : 'pill-warn'}`}>{a.level}</span>
                  <div>
                    <div>{a.message}</div>
                    <div className="muted small">
                      {when(a.at)}{a.acknowledgedAt ? ` · ✓ acknowledged by ${a.acknowledgedBy ?? 'ops'} at ${when(a.acknowledgedAt)}` : ' · not acknowledged'}
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel">
          <h2>Status history</h2>
          <ul className="timeline">
            <li><span className="pill">Planned</span><div><div>Planned{m.createdBy ? ` by ${m.createdBy}` : ''}</div><div className="muted small">{when(m.createdAt)}</div></div></li>
            {m.history.map((h, i) => (
              <li key={i}>
                <span className={`pill pill-${h.to.toLowerCase()}`}>{MISSION_STATUS_LABEL[h.to]}</span>
                <div><div>{h.by ? `by ${h.by}` : 'automatic'}{h.note ? ` · ${h.note}` : ''}</div><div className="muted small">{when(h.at)}</div></div>
              </li>
            ))}
          </ul>
        </section>
      </div>

      {missed.length > 0 && m.status !== 'Planned' && (
        <section className="panel">
          <h2>{live ? 'Not yet inspected' : 'Not inspected'} ({missed.length})</h2>
          <p className="small">{missed.map((s) => s.label).join(', ')}</p>
          {!live && <p className="muted small">Schedule a follow-up patrol for these structures.</p>}
        </section>
      )}
    </div>
  );
}

function Tile({ label, value, sub, danger }: { label: string; value: string; sub: string; danger?: boolean }) {
  return (
    <div className={`stat ${danger ? 'stat-alert' : ''}`}>
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      <div className="stat-sub">{sub}</div>
    </div>
  );
}

function CaptureCard({ view, onOpen }: { view: FindingView; onOpen(): void }) {
  const f = view.finding;
  const image = useFindingImage(f.id);
  return (
    <button className="capture-card" onClick={onOpen} aria-label={`Open ${FINDING_TYPE_LABEL[f.type]} at ${f.structureLabel}`}>
      {image.data ? <img src={image.data} alt="" /> : <div className="capture-placeholder" />}
      <div className="capture-body">
        <div className="capture-title"><SeverityBadge severity={f.severity} /> <strong>{FINDING_TYPE_LABEL[f.type]}</strong></div>
        <div className="small">{f.structureLabel} · {Math.round(f.confidence * 100)}% confidence · <span className={`pill pill-${f.status.toLowerCase()}`}>{f.status}</span></div>
        {f.description && <div className="muted small">{f.description}</div>}
      </div>
    </button>
  );
}

function MissionMap({ circuit, inspected, track, findings, alerts, onOpenFinding }: {
  circuit: Circuit; inspected: Set<string>; track: TelemetryPoint[]; findings: FindingView[]; alerts: DroneAlert[]; onOpenFinding(id: string): void;
}) {
  const bounds = useMemo(() => L.latLngBounds([
    ...circuit.structures.map((s) => [s.location.lat, s.location.lon] as [number, number]),
    ...track.map((p) => [p.lat, p.lon] as [number, number]),
  ]), [circuit, track]);
  const line = circuit.structures.map((s) => [s.location.lat, s.location.lon] as [number, number]);

  return (
    <MapContainer className="map map-mission" bounds={bounds} boundsOptions={{ padding: [24, 24] }} scrollWheelZoom={false} preferCanvas>
      <TileLayer className="map-tiles" attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" />
      <Polyline positions={line} pathOptions={{ color: voltageHex(circuit.voltageKv), weight: 5, opacity: 0.6 }} />
      {track.length > 1 && <Polyline positions={track.map((p) => [p.lat, p.lon] as [number, number])} pathOptions={{ color: '#f43f5e', weight: 2.5, dashArray: '6 5' }} />}
      {circuit.structures.map((s) => (
        <CircleMarker key={s.id} center={[s.location.lat, s.location.lon]} radius={4}
          pathOptions={{ color: '#0b1220', weight: 1, fillColor: inspected.has(s.id) ? '#22c55e' : '#64748b', fillOpacity: 1 }}>
          <Tooltip>{s.label} · {inspected.has(s.id) ? 'inspected' : 'not inspected'}</Tooltip>
        </CircleMarker>
      ))}
      {alerts.map((a, i) => (
        <CircleMarker key={`a${i}`} center={[a.position.lat, a.position.lon]} radius={11}
          pathOptions={{ color: a.level === 'Critical' ? '#ef4444' : '#f59e0b', weight: 2, fillOpacity: 0 }}>
          <Tooltip>{a.message}</Tooltip>
        </CircleMarker>
      ))}
      {findings.map((f) => (
        <CircleMarker key={f.finding.id} center={[f.finding.location.lat, f.finding.location.lon]} radius={6 + f.finding.severity}
          pathOptions={{ color: '#fff', weight: 2, fillColor: severityHex(f.finding.severity), fillOpacity: 0.9 }}
          eventHandlers={{ click: () => onOpenFinding(f.finding.id) }}>
          <Tooltip>Sev {f.finding.severity} · {FINDING_TYPE_LABEL[f.finding.type]} · {f.finding.structureLabel}</Tooltip>
        </CircleMarker>
      ))}
    </MapContainer>
  );
}

/** Small line chart of one telemetry channel over the flight, with an optional limit line. */
function Profile({ label, unit, points, pick, color, limit, limitLabel, min, max }: {
  label: string; unit: string; points: TelemetryPoint[]; pick(p: TelemetryPoint): number; color: string;
  limit?: number; limitLabel?: string; min?: number; max?: number;
}) {
  const W = 360, H = 80, P = 4;
  const values = points.map(pick);
  const lo = min ?? Math.min(...values, limit ?? Infinity);
  const hi = max ?? Math.max(...values, limit ?? -Infinity);
  const span = hi - lo || 1;
  const x = (i: number) => P + (i / (points.length - 1)) * (W - 2 * P);
  const y = (v: number) => H - P - ((v - lo) / span) * (H - 2 * P);
  const path = values.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join(' ');
  const last = values[values.length - 1];

  return (
    <figure className="profile">
      <figcaption className="small">
        <strong>{label}</strong>
        <span className="muted"> now {last.toFixed(unit === 'm/s' ? 1 : 0)} {unit} · range {Math.min(...values).toFixed(0)} to {Math.max(...values).toFixed(0)} {unit}</span>
      </figcaption>
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" role="img" aria-label={`${label} over the flight`}>
        {limit != null && (
          <>
            <line x1={0} x2={W} y1={y(limit)} y2={y(limit)} stroke="#ef4444" strokeDasharray="4 4" strokeWidth={1} />
            <text x={W - 4} y={y(limit) - 3} textAnchor="end" fontSize="9" fill="#ef4444">{limitLabel}</text>
          </>
        )}
        <path d={path} fill="none" stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" />
      </svg>
    </figure>
  );
}
