import type { FindingStatus, FindingType, FindingView, MissionStatus } from '../types';

export const FINDING_TYPE_LABEL: Record<FindingType, string> = {
  VegetationEncroachment: 'Vegetation encroachment',
  DamagedInsulator: 'Damaged insulator',
  CrossarmDamage: 'Crossarm damage',
  ThermalHotspot: 'Thermal hotspot',
  MissingHardware: 'Missing hardware',
  ConductorDamage: 'Conductor damage',
};

export const MISSION_STATUS_LABEL: Record<MissionStatus, string> = {
  Planned: 'Planned',
  InFlight: 'In flight',
  Completed: 'Completed',
  Aborted: 'Aborted',
};

export const severityColor = (sev: number) =>
  sev >= 5 ? 'var(--sev5)' : sev === 4 ? 'var(--sev4)' : sev === 3 ? 'var(--sev3)' : sev === 2 ? 'var(--sev2)' : 'var(--sev1)';

/** Hex versions for Leaflet, which can't read CSS variables. */
export const severityHex = (sev: number) =>
  sev >= 5 ? '#dc2626' : sev === 4 ? '#ea580c' : sev === 3 ? '#d4a017' : sev === 2 ? '#3b82f6' : '#64748b';

/** 0 kV means OpenStreetMap has no voltage for the line. */
export const voltageHex = (kv: number) => (kv >= 200 ? '#a855f7' : kv >= 100 ? '#0ea5e9' : kv > 0 ? '#14b8a6' : '#94a3b8');

/**
 * Map detail by zoom, so the county-wide map stays responsive: zoomed out only the 230/500 kV backbone,
 * 115 kV from zoom 11, everything (60/69 kV, distribution, untagged) from zoom 13, towers from zoom 14.
 */
export const minKvForZoom = (zoom: number) => (zoom < 11 ? 200 : zoom < 13 ? 100 : 0);
export const TOWER_ZOOM = 14;

/** Circuits grouped by voltage, highest first; unknown voltage last. Names sorted within each group. */
export function circuitsByVoltage<T extends { voltageKv: number; name: string }>(circuits: T[]): { kv: number; label: string; circuits: T[] }[] {
  const groups = new Map<number, T[]>();
  for (const c of circuits) groups.set(c.voltageKv, [...(groups.get(c.voltageKv) ?? []), c]);
  return [...groups.entries()]
    .sort(([a], [b]) => (a === 0 ? 1 : b === 0 ? -1 : b - a))
    .map(([kv, list]) => ({ kv, label: kv > 0 ? `${kv} kV` : 'Voltage not tagged', circuits: list.sort((x, y) => x.name.localeCompare(y.name)) }));
}

/** Dollars for reports: "$1,234", or "$12.50" under $100. */
export function money(n: number): string {
  return n < 100 && n % 1 !== 0
    ? `$${n.toFixed(2)}`
    : `$${Math.round(n).toLocaleString()}`;
}

export function relativeTime(iso: string, now: Date = new Date()): string {
  const sec = Math.round((now.getTime() - new Date(iso).getTime()) / 1000);
  const abs = Math.abs(sec);
  const unit = abs < 60 ? `${abs}s` : abs < 3600 ? `${Math.floor(abs / 60)}m` : abs < 86400 ? `${Math.floor(abs / 3600)}h` : `${Math.floor(abs / 86400)}d`;
  return sec >= 0 ? `${unit} ago` : `in ${unit}`;
}

/** SLA cell text: "due in 3h", "overdue 25m", or null when there's no SLA or it no longer applies. */
export function slaText(f: FindingView, now: Date = new Date()): { text: string; overdue: boolean } | null {
  if (!f.finding.triageDueAt || f.finding.status !== 'New') return null;
  const ms = new Date(f.finding.triageDueAt).getTime() - now.getTime();
  const abs = Math.abs(ms);
  const span = abs < 3_600_000 ? `${Math.max(1, Math.round(abs / 60_000))}m` : abs < 86_400_000 ? `${Math.round(abs / 3_600_000)}h` : `${Math.round(abs / 86_400_000)}d`;
  return ms >= 0 ? { text: `due in ${span}`, overdue: false } : { text: `overdue ${span}`, overdue: true };
}

export const miles = (meters: number) => (meters / 1609.344).toFixed(1);
export const pct = (n: number) => `${Math.round(n * 100)}%`;

/** "Flight 2 of 5 (L-050 to L-100)" for a line flown over several batteries; null when the whole line is one flight. */
export const flightLabel = (leg: { number: number; of: number; fromLabel: string; toLabel: string } | null | undefined, stretch = true) =>
  leg && leg.of > 1 ? `Flight ${leg.number} of ${leg.of}${stretch ? ` (${leg.fromLabel} to ${leg.toLabel})` : ''}` : null;

export const FINDING_FILTERS: { key: string; label: string }[] = [
  { key: 'open', label: 'Open' },
  { key: 'New', label: 'New' },
  { key: 'Triaged', label: 'Triaged' },
  { key: 'Assigned', label: 'Assigned' },
  { key: 'Resolved', label: 'Resolved' },
  { key: 'Dismissed', label: 'Dismissed' },
  { key: 'all', label: 'All' },
];

export const ACTION_LABEL: Record<FindingStatus, string> = {
  New: 'Reopen',
  Triaged: 'Confirm (triage)',
  Assigned: 'Assign crew',
  Resolved: 'Mark resolved',
  Dismissed: 'Dismiss',
};
