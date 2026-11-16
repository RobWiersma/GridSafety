import { useState } from 'react';
import { severityHex } from '../lib/format';

const LEVELS = [5, 4, 3, 2, 1] as const;
const KEY = 'gs.ops.severities';

/** Which severities the ops map and triage panel show; remembered per browser. */
export function useSeverityFilter() {
  const [shown, setShown] = useState<Set<number>>(() => {
    try {
      const saved = JSON.parse(localStorage.getItem(KEY) ?? 'null') as number[] | null;
      if (Array.isArray(saved)) return new Set(saved.filter((n) => LEVELS.includes(n as 1)));
    } catch { /* storage blocked or bad value */ }
    return new Set(LEVELS);
  });
  const update = (next: Set<number>) => {
    setShown(next);
    try { localStorage.setItem(KEY, JSON.stringify([...next])); } catch { /* storage blocked */ }
  };
  return {
    shown,
    toggle: (sev: number) => {
      const next = new Set(shown);
      if (next.has(sev)) next.delete(sev); else next.add(sev);
      update(next);
    },
    only: (sev: number) => update(new Set([sev])),
    all: () => update(new Set(LEVELS)),
  };
}

/** Severity chips over the map: count per level, click to show/hide, double-click to show only that level. */
export function SeverityFilter({ counts, shown, onToggle, onOnly, onAll }: {
  counts: Record<number, number>;
  shown: Set<number>;
  onToggle(sev: number): void;
  onOnly(sev: number): void;
  onAll(): void;
}) {
  const allOn = LEVELS.every((s) => shown.has(s));
  return (
    <div className="sev-filter" role="group" aria-label="Show findings by severity">
      <span className="sev-filter-label">Severity</span>
      {LEVELS.map((sev) => (
        <button
          key={sev}
          className={`sev-chip ${shown.has(sev) ? 'sev-chip-on' : ''}`}
          aria-pressed={shown.has(sev)}
          title={`Severity ${sev}: ${counts[sev] ?? 0} open. Click to show or hide, double-click to show only this level.`}
          onClick={() => onToggle(sev)}
          onDoubleClick={() => onOnly(sev)}
        >
          <i style={{ background: severityHex(sev) }} />
          {sev}
          <span className="sev-chip-count">{counts[sev] ?? 0}</span>
        </button>
      ))}
      {!allOn && <button className="link-button sev-filter-all" onClick={onAll}>All</button>}
    </div>
  );
}
