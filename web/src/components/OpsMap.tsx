import L from 'leaflet';
import { useEffect, useMemo, useState } from 'react';
import { CircleMarker, MapContainer, Marker, Pane, Polyline, TileLayer, Tooltip, useMap, useMapEvents } from 'react-leaflet';
import { FINDING_TYPE_LABEL, TOWER_ZOOM, minKvForZoom, severityHex, voltageHex } from '../lib/format';
import type { Circuit, DroneState, FindingView, TelemetryPoint } from '../types';

interface Props {
  circuits: Circuit[];
  drones: DroneState[];
  findings: FindingView[];
  track: TelemetryPoint[];
  focus?: Circuit | null;
  /** Mission id → callsign, to label the drone with the flight it's on. */
  flightNames?: Record<string, string>;
  onOpenFinding(id: string): void;
}

const droneIcon = (d: DroneState) =>
  L.divIcon({
    className: '',
    iconSize: [34, 34],
    iconAnchor: [17, 17],
    html: `<div class="drone-marker ${d.activeAlerts.length ? 'drone-alert' : ''} ${d.status === 'Idle' ? 'drone-idle' : ''}">
             <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M12 2l3 7h7l-5.5 4.5L18.5 21 12 16.5 5.5 21l2-7.5L2 9h7z"/></svg>
           </div>`,
  });

export function OpsMap({ circuits, drones, findings, track, focus, flightNames = {}, onOpenFinding }: Props) {
  // SVG, not the map's shared canvas: a canvas in its own pane would cover the whole map and swallow
  // hover and clicks meant for the lines and parked drones underneath. There are only tens of findings.
  const findingsRenderer = useMemo(() => L.svg({ pane: 'findings' }), []);
  const bounds = useMemo(() => {
    const pts = (focus ? [focus] : circuits).flatMap((c) => c.structures.map((s) => [s.location.lat, s.location.lon] as [number, number]));
    return pts.length ? L.latLngBounds(pts) : null;
  }, [circuits, focus]);

  if (!bounds) return <div className="map map-empty">Loading map…</div>;

  return (
    // preferCanvas: hundreds of lines and thousands of towers draw on one canvas instead of as SVG nodes
    <MapContainer className="map" bounds={bounds} boundsOptions={{ padding: [30, 30] }} scrollWheelZoom preferCanvas>
      <FitBounds bounds={bounds} />
      <TileLayer
        className="map-tiles"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
      />

      <CircuitLayer circuits={circuits} focus={focus} />

      {track.length > 1 && (
        <Polyline positions={track.map((p) => [p.lat, p.lon] as L.LatLngTuple)} pathOptions={{ color: '#f43f5e', weight: 2.5, dashArray: '6 4' }} />
      )}

      {/* Stacking, bottom to top: lines, parked drones, findings (worst on top), flying drones.
          A drone that landed on a defect must not hide it. */}
      <Pane name="parked-drones" style={{ zIndex: 590 }} />
      <Pane name="findings" style={{ zIndex: 595 }}>
      {[...findings].sort((a, b) => a.finding.severity - b.finding.severity).map((f) => (
        <CircleMarker key={f.finding.id} pane="findings" renderer={findingsRenderer} center={[f.finding.location.lat, f.finding.location.lon]}
          radius={6 + f.finding.severity * 1.5}
          pathOptions={{ color: '#fff', weight: 2, fillColor: severityHex(f.finding.severity), fillOpacity: 0.9 }}
          eventHandlers={{ click: () => onOpenFinding(f.finding.id) }}>
          <Tooltip>
            <strong>Sev {f.finding.severity}</strong> · {FINDING_TYPE_LABEL[f.finding.type]}<br />
            {f.finding.structureLabel} · {f.finding.status}
          </Tooltip>
        </CircleMarker>
      ))}
      </Pane>

      {drones.map((d) => (
        <Marker key={d.droneId} position={[d.position.lat, d.position.lon]} icon={droneIcon(d)}
          pane={d.status === 'Flying' ? 'markerPane' : 'parked-drones'} zIndexOffset={d.status === 'Flying' ? 1000 : 0}>
          <Tooltip direction="top" offset={[0, -14]} permanent={d.status === 'Flying'}>
            {(d.status === 'Flying' && d.missionId && flightNames[d.missionId]) || d.droneId} · {Math.round(d.batteryPct)}% · {Math.round(d.altitudeM)} m{d.rssiDbm != null && ` · ${Math.round(d.rssiDbm)} dBm`}
          </Tooltip>
        </Marker>
      ))}
    </MapContainer>
  );
}

/**
 * Lines and towers, filtered so the county-wide map stays responsive: only voltages that matter at the
 * current zoom (see minKvForZoom), only circuits inside the (padded) view, towers only when zoomed in.
 * The circuit being inspected is always drawn with its towers.
 */
function CircuitLayer({ circuits, focus }: { circuits: Circuit[]; focus?: Circuit | null }) {
  const map = useMap();
  // Leaflet can't report bounds until the container has a size, so read the view once the map is ready.
  const [view, setView] = useState<{ zoom: number; bounds: L.LatLngBounds } | null>(null);
  const update = () => { if (map.getSize().x > 0) setView({ zoom: map.getZoom(), bounds: map.getBounds().pad(0.25) }); };
  useEffect(() => { map.whenReady(update); }, [map]); // eslint-disable-line react-hooks/exhaustive-deps
  useMapEvents({ moveend: update, resize: update });

  const shapes = useMemo(
    () => circuits.map((c) => {
      const positions = c.structures.map((s) => [s.location.lat, s.location.lon] as L.LatLngTuple);
      return { circuit: c, positions, bounds: L.latLngBounds(positions) };
    }),
    [circuits],
  );

  if (!view) return null;
  const minKv = minKvForZoom(view.zoom);
  const showTowers = view.zoom >= TOWER_ZOOM;
  const visible = shapes.filter(({ circuit: c, bounds }) =>
    c.id === focus?.id || (c.voltageKv >= minKv && view.bounds.intersects(bounds)));

  return (
    <>
      {visible.map(({ circuit: c, positions }) => (
        <Polyline key={c.id} positions={positions}
          pathOptions={{ color: voltageHex(c.voltageKv), weight: focus?.id === c.id ? 6 : 4, opacity: focus && focus.id !== c.id ? 0.35 : 0.9 }}>
          <Tooltip sticky>{c.name} · {c.structures.length} structures · {(c.lengthMeters / 1000).toFixed(1)} km</Tooltip>
        </Polyline>
      ))}
      {visible
        .filter(({ circuit: c }) => showTowers || c.id === focus?.id)
        .flatMap(({ circuit: c }) => c.structures)
        .filter((s) => view.bounds.contains([s.location.lat, s.location.lon]))
        .map((s) => (
          <CircleMarker key={s.id} center={[s.location.lat, s.location.lon]} radius={3}
            pathOptions={{ color: '#1e293b', weight: 1, fillColor: '#f8fafc', fillOpacity: 1 }}>
            <Tooltip>{s.label}</Tooltip>
          </CircleMarker>
        ))}
    </>
  );
}

function FitBounds({ bounds }: { bounds: L.LatLngBounds }) {
  const map = useMap();
  useEffect(() => {
    map.fitBounds(bounds, { padding: [30, 30] });
  }, [map, bounds]);
  return null;
}
