import L from 'leaflet';
import { useEffect, useMemo } from 'react';
import { CircleMarker, MapContainer, Marker, Polyline, TileLayer, Tooltip, useMap } from 'react-leaflet';
import { FINDING_TYPE_LABEL, severityHex, voltageHex } from '../lib/format';
import type { Circuit, DroneState, FindingView, TelemetryPoint } from '../types';

interface Props {
  circuits: Circuit[];
  drones: DroneState[];
  findings: FindingView[];
  track: TelemetryPoint[];
  focus?: Circuit | null;
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

export function OpsMap({ circuits, drones, findings, track, focus, onOpenFinding }: Props) {
  const bounds = useMemo(() => {
    const pts = (focus ? [focus] : circuits).flatMap((c) => c.structures.map((s) => [s.location.lat, s.location.lon] as [number, number]));
    return pts.length ? L.latLngBounds(pts) : null;
  }, [circuits, focus]);

  if (!bounds) return <div className="map map-empty">Loading map…</div>;

  return (
    <MapContainer className="map" bounds={bounds} boundsOptions={{ padding: [30, 30] }} scrollWheelZoom>
      <FitBounds bounds={bounds} />
      <TileLayer
        className="map-tiles"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
      />

      {circuits.map((c) => (
        <Polyline key={c.id} positions={c.structures.map((s) => [s.location.lat, s.location.lon] as L.LatLngTuple)}
          pathOptions={{ color: voltageHex(c.voltageKv), weight: focus?.id === c.id ? 6 : 4, opacity: focus && focus.id !== c.id ? 0.35 : 0.9 }}>
          <Tooltip sticky>{c.name} · {c.structures.length} structures · {(c.lengthMeters / 1000).toFixed(1)} km</Tooltip>
        </Polyline>
      ))}
      {circuits.flatMap((c) =>
        c.structures.map((s) => (
          <CircleMarker key={s.id} center={[s.location.lat, s.location.lon]} radius={3}
            pathOptions={{ color: '#1e293b', weight: 1, fillColor: '#f8fafc', fillOpacity: 1 }}>
            <Tooltip>{s.label}</Tooltip>
          </CircleMarker>
        )),
      )}

      {track.length > 1 && (
        <Polyline positions={track.map((p) => [p.lat, p.lon] as L.LatLngTuple)} pathOptions={{ color: '#f43f5e', weight: 2.5, dashArray: '6 4' }} />
      )}

      {findings.map((f) => (
        <CircleMarker key={f.finding.id} center={[f.finding.location.lat, f.finding.location.lon]}
          radius={6 + f.finding.severity * 1.5}
          pathOptions={{ color: '#fff', weight: 2, fillColor: severityHex(f.finding.severity), fillOpacity: 0.9 }}
          eventHandlers={{ click: () => onOpenFinding(f.finding.id) }}>
          <Tooltip>
            <strong>Sev {f.finding.severity}</strong> · {FINDING_TYPE_LABEL[f.finding.type]}<br />
            {f.finding.structureLabel} · {f.finding.status}
          </Tooltip>
        </CircleMarker>
      ))}

      {drones.map((d) => (
        <Marker key={d.droneId} position={[d.position.lat, d.position.lon]} icon={droneIcon(d)} zIndexOffset={1000}>
          <Tooltip direction="top" offset={[0, -14]} permanent={d.status === 'Flying'}>
            {d.droneId} · {Math.round(d.batteryPct)}% · {Math.round(d.altitudeM)} m
          </Tooltip>
        </Marker>
      ))}
    </MapContainer>
  );
}

function FitBounds({ bounds }: { bounds: L.LatLngBounds }) {
  const map = useMap();
  useEffect(() => {
    map.fitBounds(bounds, { padding: [30, 30] });
  }, [map, bounds]);
  return null;
}
