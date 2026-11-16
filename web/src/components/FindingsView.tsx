import { useMemo, useState } from 'react';
import { useCrews, useFindings } from '../api';
import { FINDING_FILTERS, FINDING_TYPE_LABEL, relativeTime, slaText } from '../lib/format';
import type { FindingType } from '../types';
import { FilterBar, matchesSearch, SearchFilter, SelectFilter, ToggleFilter } from './FilterBar';
import { SeverityBadge } from './SeverityBadge';

const SEVERITY_OPTIONS = [['any', 'Any severity'], ['5', 'Sev 5 only'], ['4', 'Sev 4+'], ['3', 'Sev 3+']] as const;
type Severity = (typeof SEVERITY_OPTIONS)[number][0];

export function FindingsView({ onOpen, initialFilter = 'open' }: { onOpen(id: string): void; initialFilter?: string }) {
  const [filter, setFilter] = useState(initialFilter);
  const findings = useFindings(filter);
  const crews = useCrews();
  const [query, setQuery] = useState('');
  const [severity, setSeverity] = useState<Severity>('any');
  const [type, setType] = useState<FindingType | 'any'>('any');
  const [circuit, setCircuit] = useState('any');
  const [crew, setCrew] = useState('any');
  const [overdueOnly, setOverdueOnly] = useState(false);

  const all = findings.data ?? [];
  const circuitOptions = useMemo(
    () => [['any', 'All circuits'] as const, ...[...new Set(all.map((f) => f.circuitName))].sort().map((c) => [c, c] as const)],
    [all],
  );
  const crewOptions = useMemo(
    () => [['any', 'Anyone'] as const, ['unassigned', 'Unassigned'] as const, ...(crews.data ?? []).map((c) => [c.crew.name, c.crew.name] as const)],
    [crews.data],
  );
  const typeOptions = [['any', 'All issue types'] as const, ...Object.entries(FINDING_TYPE_LABEL).map(([k, v]) => [k as FindingType, v] as const)];

  const shown = all.filter(({ finding: f, circuitName, overdue }) =>
    (severity === 'any' || f.severity >= Number(severity)) &&
    (type === 'any' || f.type === type) &&
    (circuit === 'any' || circuitName === circuit) &&
    (crew === 'any' || (crew === 'unassigned' ? !f.assignedTo : f.assignedTo?.includes(crew))) &&
    (!overdueOnly || overdue) &&
    matchesSearch(query, f.structureLabel, f.description, circuitName, f.assignedTo, FINDING_TYPE_LABEL[f.type], f.missionId));
  const active = query !== '' || severity !== 'any' || type !== 'any' || circuit !== 'any' || crew !== 'any' || overdueOnly;
  const clear = () => {
    setQuery(''); setSeverity('any'); setType('any'); setCircuit('any'); setCrew('any'); setOverdueOnly(false);
  };

  return (
    <section className="panel panel-flush">
      <div className="panel-toolbar">
        <h2>Findings work queue</h2>
        <div className="chips" role="tablist">
          {FINDING_FILTERS.map((f) => (
            <button key={f.key} role="tab" aria-selected={filter === f.key} className={`chip ${filter === f.key ? 'chip-active' : ''}`} onClick={() => setFilter(f.key)}>
              {f.label}
            </button>
          ))}
        </div>
      </div>

      {findings.data && findings.data.length > 0 && (
        <FilterBar shown={shown.length} total={all.length} noun="findings" active={active} onClear={clear}>
          <SearchFilter value={query} onChange={setQuery} placeholder="Search structure, notes, circuit, crew…" />
          <SelectFilter label="Severity" value={severity} onChange={setSeverity} options={SEVERITY_OPTIONS} />
          <SelectFilter label="Issue type" value={type} onChange={setType} options={typeOptions} />
          <SelectFilter label="Circuit" value={circuit} onChange={setCircuit} options={circuitOptions} />
          <SelectFilter label="Assigned to" value={crew} onChange={setCrew} options={crewOptions} />
          <ToggleFilter label="Overdue only" checked={overdueOnly} onChange={setOverdueOnly} />
        </FilterBar>
      )}

      {findings.isPending && <div className="center"><div className="spinner" /></div>}
      {findings.isError && <div className="banner banner-error">{findings.error.message}</div>}
      {findings.data && findings.data.length === 0 && (
        <p className="empty-row muted">Nothing here. Findings appear as drones report them.</p>
      )}
      {findings.data && findings.data.length > 0 && shown.length === 0 && (
        <p className="empty-row muted">No findings match these filters.</p>
      )}
      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Sev</th>
                <th>Issue</th>
                <th>Structure</th>
                <th>Circuit</th>
                <th>Status</th>
                <th>Triage SLA</th>
                <th>Reported</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((f) => {
                const sla = slaText(f);
                return (
                  <tr key={f.finding.id} onClick={() => onOpen(f.finding.id)} tabIndex={0} onKeyDown={(e) => e.key === 'Enter' && onOpen(f.finding.id)}
                    className={f.overdue ? 'row-overdue' : ''}>
                    <td><SeverityBadge severity={f.finding.severity} /></td>
                    <td>
                      <strong>{FINDING_TYPE_LABEL[f.finding.type]}</strong>
                      {f.finding.description && <div className="muted small truncate">{f.finding.description}</div>}
                    </td>
                    <td className="mono">{f.finding.structureLabel}</td>
                    <td>{f.circuitName}</td>
                    <td>
                      <span className={`pill pill-${f.finding.status.toLowerCase()}`}>{f.finding.status}</span>
                      {f.finding.assignedTo && <div className="muted small">{f.finding.assignedTo}</div>}
                    </td>
                    <td>
                      {sla ? <span className={sla.overdue ? 'text-danger strong' : ''}>{sla.text}</span> : <span className="muted">–</span>}
                      {f.finding.escalated && <div><span className="pill pill-danger">escalated</span></div>}
                    </td>
                    <td className="muted">{relativeTime(f.finding.createdAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
