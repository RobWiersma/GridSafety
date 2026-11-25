import type { ReportTarget } from '../api';

/** A link-safe key for a report target, e.g. "truck/T-071" or "technician/sector-7g/Lenny%20Leonardo". */
export function reportKey(t: ReportTarget): string {
  if (t.kind === 'activity') return 'activity';
  const parts = 'member' in t ? [t.crewId, t.member] : 'crewId' in t ? [t.crewId] : [t.id];
  return [t.kind, ...parts.map(encodeURIComponent)].join('/');
}
