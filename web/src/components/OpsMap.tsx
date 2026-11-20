import L from 'leaflet';
import { useEffect, useMemo, useRef, useState } from 'react';
import { CircleMarker, MapContainer, Marker, Pane, Polyline, TileLayer, Tooltip, useMap, useMapEvents } from 'react-leaflet';
import { FINDING_TYPE_LABEL, TOWER_ZOOM, minKvForZoom, voltageHex } from '../lib/format';
import { severityMarkerHtml, severityMarkerSize } from '../lib/severityIcon';
import type { Circuit, Depot, DroneState, FieldDispatch, FieldPhase, FindingView, GeoPoint, TelemetryPoint } from '../types';

/** A truck on a simulated field job, positioned for the current moment. */
export interface TruckPin {
  findingId: string;
  label: string;
  structureLabel: string;
  dispatch: FieldDispatch;
  phase: FieldPhase;
  position: GeoPoint;
  /** The road still ahead on the current drive, from the truck onwards. */
  ahead: GeoPoint[];
  /** e.g. "Repairing · 0:42" */
  status: string;
}

/** A depot with the on-duty and off-duty people who depart from it. */
export interface DepotPin {
  depot: Depot;
  people: { name: string; role: string; crewName: string; offDuty: boolean }[];
}

interface Props {
  circuits: Circuit[];
  drones: DroneState[];
  findings: FindingView[];
  track: TelemetryPoint[];
  focus?: Circuit | null;
  /** Mission id → callsign, to label the drone with the flight it's on. */
  flightNames?: Record<string, string>;
  /** Yards ground crews depart from; omitted or empty to hide them. */
  depots?: DepotPin[];
  /** Field crews out on jobs. */
  trucks?: TruckPin[];
  onOpenFinding(id: string): void;
  /** Findings picked for a bulk action; omit onSelect to turn picking off (guests). */
  selected?: ReadonlySet<string>;
  /** Select mode: a click picks instead of opening, and dragging draws a box instead of panning (works without a keyboard). */
  selectMode?: boolean;
  onSelect?(ids: string[], how: 'toggle' | 'add'): void;
}

/** Only findings still waiting for triage or a crew can be picked for a bulk action. */
export const pickable = (f: FindingView) => f.finding.status === 'New' || f.finding.status === 'Triaged';

const truckIcon = (phase: FieldPhase) =>
  L.divIcon({
    className: '',
    iconSize: [28, 28],
    iconAnchor: [14, 14],
    html: `<div class="truck-marker truck-${phase.toLowerCase()}"><svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true">
             <path d="M3 6h11v9H3zM14 9h4l3 3v3h-7zM6.5 19a2 2 0 1 1 0-4 2 2 0 0 1 0 4zm11 0a2 2 0 1 1 0-4 2 2 0 0 1 0 4z"/></svg></div>`,
  });

const depotIcon = L.divIcon({
  className: '',
  iconSize: [26, 26],
  iconAnchor: [13, 13],
  html: `<div class="depot-marker"><svg viewBox="0 0 24 24" width="15" height="15" fill="currentColor" aria-hidden="true">
           <path d="M3 10.5 12 4l9 6.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/></svg></div>`,
});

/** A quadcopter seen from above: body, four arms and a rotor at the end of each. The rotors spin while it flies. */
const ROTOR = (x: number, y: number) =>
  `<circle cx="${x}" cy="${y}" r="4.3" fill="currentColor" fill-opacity=".3" stroke="currentColor" stroke-width="1.1"/>`
  + `<g class="drone-rotor" style="transform-origin:${x}px ${y}px"><path d="M${x - 3.6} ${y}h7.2" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></g>`;
const QUADCOPTER = `<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
  <path d="M7 7l10 10M17 7 7 17" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
  ${ROTOR(5.6, 5.6)}${ROTOR(18.4, 5.6)}${ROTOR(5.6, 18.4)}${ROTOR(18.4, 18.4)}
  <rect x="8.6" y="8.6" width="6.8" height="6.8" rx="2" fill="currentColor"/>
</svg>`;

/** One icon per severity and selected state, made once and reused. */
const findingIcons = new Map<string, L.DivIcon>();
const findingIcon = (sev: number, picked: boolean) => {
  const key = `${sev}/${picked}`;
  let icon = findingIcons.get(key);
  if (!icon) {
    const size = severityMarkerSize(sev);
    icon = L.divIcon({ className: '', iconSize: [size, size], iconAnchor: [size / 2, size / 2], html: severityMarkerHtml(sev, picked) });
    findingIcons.set(key, icon);
  }
  return icon;
};

const droneIcon = (d: DroneState) =>
  L.divIcon({
    className: '',
    iconSize: [34, 34],
    iconAnchor: [17, 17],
    html: `<div class="drone-marker ${d.activeAlerts.length ? 'drone-alert' : ''} ${d.status === 'Idle' ? 'drone-idle' : ''}">${QUADCOPTER}</div>`,
  });

export function OpsMap({ circuits, drones, findings, track, focus, flightNames = {}, depots = [], trucks = [], onOpenFinding,
  selected, selectMode = false, onSelect }: Props) {
  // SVG, not the map's shared canvas: a canvas in its own pane would cover the whole map and swallow
  // hover and clicks meant for everything underneath (findings, lines, parked drones). There are only tens of each.
  const trucksRenderer = useMemo(() => L.svg({ pane: 'trucks' }), []);
  const clickFinding = (f: FindingView, e: L.LeafletMouseEvent) => {
    const k = e.originalEvent;
    if (onSelect && pickable(f) && (selectMode || k.shiftKey || k.ctrlKey || k.metaKey)) onSelect([f.finding.id], 'toggle');
    else onOpenFinding(f.finding.id);
  };
  const bounds = useMemo(() => {
    const pts = (focus ? [focus] : circuits).flatMap((c) => c.structures.map((s) => [s.location.lat, s.location.lon] as [number, number]));
    return pts.length ? L.latLngBounds(pts) : null;
  }, [circuits, focus]);

  if (!bounds) return <div className="map map-empty">Loading map…</div>;

  return (
    // preferCanvas: hundreds of lines and thousands of towers draw on one canvas instead of as SVG nodes
    <MapContainer className="map" bounds={bounds} boundsOptions={{ padding: [30, 30] }} scrollWheelZoom preferCanvas>
      <FitBounds bounds={bounds} />
      <MiddlePan />
      <TileLayer
        className="map-tiles"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
      />

      <CircuitLayer circuits={circuits} focus={focus} />

      {track.length > 1 && (
        <Polyline positions={track.map((p) => [p.lat, p.lon] as L.LatLngTuple)} pathOptions={{ color: '#f43f5e', weight: 2.5, dashArray: '6 4' }} />
      )}

      {/* Stacking, bottom to top: lines, depots, parked drones, findings (worst on top), flying drones.
          A drone that landed on a defect must not hide it. */}
      <Pane name="depots" style={{ zIndex: 585 }} />
      {depots.map(({ depot, people }) => {
        const onDuty = people.filter((p) => !p.offDuty);
        return (
          <Marker key={depot.id} pane="depots" position={[depot.location.lat, depot.location.lon]} icon={depotIcon}
            title={depot.name} alt={`${depot.name}: ${onDuty.length} on duty depart from here`}>
            <Tooltip direction="top" offset={[0, -12]}>
              <strong>{depot.name}</strong> · {onDuty.length} on duty depart{onDuty.length === 1 ? 's' : ''} from here
              {people.map((p) => (
                <div key={`${p.crewName}/${p.name}`} className={p.offDuty ? 'depot-tip-off' : undefined}>
                  {p.name} · {p.role}, {p.crewName}{p.offDuty ? ' (off duty)' : ''}
                </div>
              ))}
            </Tooltip>
          </Marker>
        );
      })}
      {/* Field trucks: the route they're driving, and the truck itself, above depots and findings but under flying drones. */}
      <Pane name="trucks" style={{ zIndex: 597 }} />
      {trucks.filter((tr) => tr.ahead.length > 1).map((tr) => (
        <Polyline key={`route-${tr.findingId}`} pane="trucks" renderer={trucksRenderer} positions={tr.ahead.map((p) => [p.lat, p.lon] as L.LatLngTuple)} interactive={false}
          pathOptions={{ color: tr.phase === 'Returning' ? '#94a3b8' : '#f5b224', weight: 3, dashArray: tr.dispatch.onRoads ? undefined : '2 6', opacity: 0.85 }} />
      ))}
      {trucks.filter((tr) => tr.phase !== 'Queued' && tr.phase !== 'MovedOn').map((tr) => (
        <Marker key={`truck-${tr.findingId}`} pane="trucks" position={[tr.position.lat, tr.position.lon]} icon={truckIcon(tr.phase)}
          title={`${tr.label}: ${tr.status}`} eventHandlers={{ click: () => onOpenFinding(tr.findingId) }}>
          <Tooltip direction="top" offset={[0, -12]} permanent={tr.phase === 'OnSite'}>
            <strong>{tr.label}</strong> · {tr.structureLabel}<br />{tr.status}
          </Tooltip>
        </Marker>
      ))}
      <Pane name="parked-drones" style={{ zIndex: 590 }} />
      <Pane name="findings" style={{ zIndex: 595 }}>
      {[...findings].sort((a, b) => a.finding.severity - b.finding.severity).map((f) => {
        const picked = selected?.has(f.finding.id) ?? false;
        return (
        <Marker key={f.finding.id} pane="findings" position={[f.finding.location.lat, f.finding.location.lon]}
          icon={findingIcon(f.finding.severity, picked)} zIndexOffset={f.finding.severity * 1000}
          eventHandlers={{ click: (e) => clickFinding(f, e) }}>
          <Tooltip direction="top" offset={[0, -severityMarkerSize(f.finding.severity) / 2]}>
            <strong>Sev {f.finding.severity}</strong> · {FINDING_TYPE_LABEL[f.finding.type]}<br />
            {f.finding.structureLabel} · {f.finding.status}{picked ? ' · selected' : ''}
          </Tooltip>
        </Marker>
        );
      })}
      </Pane>
      {onSelect && <BoxSelect findings={findings} always={selectMode} onSelect={onSelect} />}

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


/**
 * Shift-drag (or any drag in select mode, mouse or touch) draws a box and picks the open findings inside it, in place of
 * Leaflet's shift-drag box zoom. Plain DOM pointer events on the container, caught before Leaflet's own drag handler;
 * a press that barely moves stays a click.
 */
function BoxSelect({ findings, always, onSelect }: { findings: FindingView[]; always: boolean; onSelect(ids: string[], how: 'add'): void }) {
  const map = useMap();
  const latest = useRef({ findings, onSelect });
  latest.current = { findings, onSelect };
  useEffect(() => {
    const el = map.getContainer();
    map.boxZoom.disable();
    if (always) map.dragging.disable();
    el.classList.toggle('map-selecting', always);
    let start: { x: number; y: number; id: number } | null = null;
    let box: HTMLDivElement | null = null;
    const at = (e: PointerEvent) => { const r = el.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
    const down = (e: PointerEvent) => {
      if (start || e.button !== 0 || !(always || e.shiftKey) || (e.target as Element).closest('.leaflet-control')) return;
      start = { ...at(e), id: e.pointerId };
      map.dragging.disable();
    };
    const move = (e: PointerEvent) => {
      if (!start || e.pointerId !== start.id) return;
      const p = at(e);
      if (!box) {
        if (Math.hypot(p.x - start.x, p.y - start.y) < 6) return;
        box = document.createElement('div');
        box.className = 'select-box';
        el.appendChild(box);
        el.setPointerCapture(e.pointerId); // keep the box following outside the map; only once it's a drag, so clicks still land
      }
      Object.assign(box.style, {
        left: `${Math.min(p.x, start.x)}px`, top: `${Math.min(p.y, start.y)}px`,
        width: `${Math.abs(p.x - start.x)}px`, height: `${Math.abs(p.y - start.y)}px`,
      });
    };
    const up = (e: PointerEvent) => {
      if (!start || e.pointerId !== start.id) return;
      if (box) {
        const p = at(e);
        const bounds = L.latLngBounds(map.containerPointToLatLng([start.x, start.y]), map.containerPointToLatLng([p.x, p.y]));
        box.remove();
        box = null;
        const ids = latest.current.findings.filter((f) => pickable(f) && bounds.contains([f.finding.location.lat, f.finding.location.lon])).map((f) => f.finding.id);
        if (ids.length) latest.current.onSelect(ids, 'add');
      }
      start = null;
      if (!always) map.dragging.enable();
    };
    el.addEventListener('pointerdown', down, true);
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
    return () => {
      el.removeEventListener('pointerdown', down, true);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      el.removeEventListener('pointercancel', up);
      box?.remove();
      map.boxZoom.enable();
      map.dragging.enable();
      el.classList.remove('map-selecting');
    };
  }, [map, always]);
  return null;
}

/** Hold the middle mouse button to pan, in select mode too (where a left drag draws a box). */
function MiddlePan() {
  const map = useMap();
  useEffect(() => {
    const el = map.getContainer();
    let last: { x: number; y: number; id: number } | null = null;
    const down = (e: PointerEvent) => {
      if (e.button !== 1 || last) return;
      e.preventDefault(); // no browser autoscroll
      e.stopPropagation(); // and no Leaflet drag or box select
      last = { x: e.clientX, y: e.clientY, id: e.pointerId };
      el.setPointerCapture(e.pointerId);
      el.classList.add('map-panning');
    };
    const move = (e: PointerEvent) => {
      if (!last || e.pointerId !== last.id) return;
      map.panBy([last.x - e.clientX, last.y - e.clientY], { animate: false });
      last = { x: e.clientX, y: e.clientY, id: e.pointerId };
    };
    const up = (e: PointerEvent) => {
      if (!last || e.pointerId !== last.id) return;
      last = null;
      el.classList.remove('map-panning');
    };
    const noAutoscroll = (e: MouseEvent) => { if (e.button === 1) e.preventDefault(); };
    el.addEventListener('pointerdown', down, true);
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
    el.addEventListener('mousedown', noAutoscroll, true);
    el.addEventListener('auxclick', noAutoscroll, true);
    return () => {
      el.removeEventListener('pointerdown', down, true);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      el.removeEventListener('pointercancel', up);
      el.removeEventListener('mousedown', noAutoscroll, true);
      el.removeEventListener('auxclick', noAutoscroll, true);
      el.classList.remove('map-panning');
    };
  }, [map]);
  return null;
}

function FitBounds({ bounds }: { bounds: L.LatLngBounds }) {
  const map = useMap();
  useEffect(() => {
    map.fitBounds(bounds, { padding: [30, 30] });
  }, [map, bounds]);
  return null;
}
