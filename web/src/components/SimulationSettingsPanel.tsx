import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, useMe, useSaveSimulationSettings, useSimulationSettings } from '../api';
import { relativeTime } from '../lib/format';
import type { ToastMessage } from './Toast';

const MAX = 25;

/** How many findings demo and simulated flights come back with. Everyone signed in can look; supervisors change it. */
export function SimulationSettingsPanel({ onToast }: { onToast(t: ToastMessage): void }) {
  const query = useSimulationSettings();
  const save = useSaveSimulationSettings();
  const me = useMe();
  const canEdit = Boolean(me.data?.isSupervisor);

  const [mode, setMode] = useState<'random' | 'fixed'>('random');
  const [count, setCount] = useState(3);
  const [error, setError] = useState<string | null>(null);

  const saved = query.data;
  useEffect(() => {
    if (!saved) return;
    setMode(saved.findingsPerFlight == null ? 'random' : 'fixed');
    if (saved.findingsPerFlight != null) setCount(saved.findingsPerFlight);
  }, [saved]);

  const wanted = mode === 'random' ? null : count;
  const dirty = saved !== undefined && (saved.findingsPerFlight ?? null) !== wanted;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!saved) return;
    setError(null);
    try {
      await save.mutateAsync({ ...saved, findingsPerFlight: wanted ?? undefined });
      onToast({ kind: 'success', text: wanted == null ? 'Simulated flights find a random number of issues' : `Simulated flights now find ${wanted} issue${wanted === 1 ? '' : 's'} each` });
    } catch (err) {
      setError(err instanceof ApiError || err instanceof Error ? err.message : 'Failed');
    }
  };

  return (
    <section className="panel alerts">
      <div className="alerts-head">
        <h2>Simulated flights</h2>
        <p className="muted small">
          How many findings a demo flight or a simulated mission comes back with. Applies to the next flight launched.
        </p>
      </div>
      {query.isPending && <div className="center"><div className="spinner" /></div>}
      {query.isError && <div className="banner banner-error">Couldn't load simulation settings: {query.error.message}</div>}
      {saved && (
        <form className="alerts-form" onSubmit={submit}>
          <fieldset className="resource-fieldset" disabled={!canEdit}>
            <legend>Findings per flight</legend>
            <label className="check">
              <input type="radio" name="findings-mode" checked={mode === 'random'} onChange={() => setMode('random')} />
              <span>Random<span className="muted small"> · about 1 in 5 structures, so longer lines find more</span></span>
            </label>
            <label className="check sim-count">
              <input type="radio" name="findings-mode" checked={mode === 'fixed'} onChange={() => setMode('fixed')} />
              <span>Exactly</span>
              <input className="input" type="number" min={0} max={MAX} value={count} aria-label="Findings per flight"
                onFocus={() => setMode('fixed')}
                onChange={(e) => setCount(Math.max(0, Math.min(MAX, Math.round(Number(e.target.value) || 0))))} />
              <span className="muted small">per flight (0–{MAX}; fewer if the line has fewer structures). One is always severe.</span>
            </label>
          </fieldset>
          {error && <div className="banner banner-error">{error}</div>}
          <div className="alerts-actions">
            <span className="muted small">
              {saved.updatedAt ? `Last changed ${relativeTime(saved.updatedAt)}${saved.updatedBy ? ` by ${saved.updatedBy}` : ''}` : 'Using the default (random)'}
              {!canEdit && ' · Only supervisors can change this.'}
            </span>
            {canEdit && <button className="btn btn-primary" disabled={!dirty || save.isPending}>{save.isPending ? 'Saving…' : 'Save'}</button>}
          </div>
        </form>
      )}
    </section>
  );
}
