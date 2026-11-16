import type { ReactNode } from 'react';

/** One row of filter controls above a table, with a result count and a reset link. */
export function FilterBar({ shown, total, noun, active, onClear, children }: {
  shown: number;
  total: number;
  noun: string;
  active: boolean;
  onClear(): void;
  children: ReactNode;
}) {
  return (
    <div className="filter-bar" role="search">
      <div className="filter-controls">{children}</div>
      <div className="filter-summary small muted">
        {active ? `Showing ${shown} of ${total} ${noun}` : `${total} ${total === 1 ? noun.replace(/s$/, '') : noun}`}
        {active && <button className="link-button" onClick={onClear}>Clear filters</button>}
      </div>
    </div>
  );
}

export function SearchFilter({ value, onChange, placeholder }: { value: string; onChange(v: string): void; placeholder: string }) {
  return (
    <input className="input filter-search" type="search" value={value} placeholder={placeholder} aria-label={placeholder}
      onChange={(e) => onChange(e.target.value)} />
  );
}

export function SelectFilter<T extends string>({ label, value, onChange, options }: {
  label: string;
  value: T;
  onChange(v: NoInfer<T>): void;
  options: readonly (readonly [NoInfer<T>, string])[];
}) {
  return (
    <label className={`filter-select ${options[0] && value !== options[0][0] ? 'filter-on' : ''}`}>
      <span className="sr-only">{label}</span>
      <select className="input" value={value} onChange={(e) => onChange(e.target.value as T)} aria-label={label}>
        {options.map(([v, text]) => <option key={v} value={v}>{text}</option>)}
      </select>
    </label>
  );
}

export function ToggleFilter({ label, checked, onChange }: { label: string; checked: boolean; onChange(v: boolean): void }) {
  return (
    <label className={`filter-toggle ${checked ? 'filter-on' : ''}`}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

/** Case-insensitive "every word appears somewhere" match. */
export function matchesSearch(query: string, ...fields: (string | number | null | undefined)[]) {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return true;
  const hay = fields.filter((f) => f != null).join(' ').toLowerCase();
  return words.every((w) => hay.includes(w));
}
