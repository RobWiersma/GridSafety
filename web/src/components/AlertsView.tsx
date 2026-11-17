import { useState, type FormEvent } from 'react';
import { ApiError, useAlertSettings, useMe, useSaveAlertSettings } from '../api';
import { ALERT_SWITCHES, RECIPIENT_STATE_LABEL, addRecipients, isDirty, summarize } from '../lib/alerts';
import { relativeTime } from '../lib/format';
import type { AlertSettingsView, RecipientState } from '../types';
import type { ToastMessage } from './Toast';

const SEVERITIES = [
  [5, 'Severity 5 only'],
  [4, 'Severity 4 and above'],
  [3, 'Severity 3 and above'],
  [2, 'Severity 2 and above'],
  [1, 'Every finding'],
] as const;

const STATE_PILL: Record<RecipientState, string> = {
  Confirmed: 'pill-res-active',
  Pending: 'pill-warn',
  NotSubscribed: 'pill-danger',
  Local: '',
  Unknown: '',
};

/** Email alerts: which events send email, the severity threshold and who gets it. Everyone signed in can look; supervisors change it. */
export function AlertsView({ onToast }: { onToast(t: ToastMessage): void }) {
  const query = useAlertSettings();
  const me = useMe();
  const canEdit = Boolean(me.data?.isSupervisor);

  return (
    <section className="panel alerts">
      <div className="alerts-head">
        <h2>Email alerts</h2>
        <p className="muted small">
          Choose which events email the on-call team. Alerts always show in the console; these settings only control email.
        </p>
      </div>
      {query.isPending && <div className="center"><div className="spinner" /></div>}
      {query.isError && <div className="banner banner-error">Couldn't load alert settings: {query.error.message}</div>}
      {query.data && (
        // Remount on every save so the form starts from what the server stored.
        <AlertsForm key={query.data.settings.version} view={query.data} canEdit={canEdit} onToast={onToast} onReload={() => query.refetch()} />
      )}
    </section>
  );
}

function AlertsForm({ view, canEdit, onToast, onReload }: { view: AlertSettingsView; canEdit: boolean; onToast(t: ToastMessage): void; onReload(): void }) {
  const save = useSaveAlertSettings();
  const [draft, setDraft] = useState(view.settings);
  const [newRecipient, setNewRecipient] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const dirty = isDirty(view.settings, draft);
  const status = new Map(view.recipients.map((r) => [r.email, r.status]));

  const add = () => {
    const r = addRecipients(draft.recipients, newRecipient);
    setError(r.error ?? null);
    if (!r.error) {
      setDraft({ ...draft, recipients: r.recipients });
      setNewRecipient('');
    }
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    // An address still in the box counts, so "type it and press Save" works.
    let recipients = draft.recipients;
    if (newRecipient.trim()) {
      const r = addRecipients(recipients, newRecipient);
      if (r.error) return setError(r.error);
      recipients = r.recipients;
      setNewRecipient('');
    }
    setError(null);
    save.mutate({ ...draft, recipients }, {
      onSuccess: (v) => onToast(v.warning ? { kind: 'error', text: v.warning } : { kind: 'success', text: 'Email alerts saved' }),
      onError: (err) => {
        if (err instanceof ApiError && err.status === 409) setConflict(true);
        setError(err.message);
      },
    });
  };

  return (
    <form className="alerts-form" onSubmit={submit}>
      {!canEdit && <div className="banner banner-info">Only supervisors can change email alerts.</div>}
      {!view.emailEnabled && <div className="banner banner-info">Local mode: settings are saved, but no email is sent.</div>}

      <fieldset className="resource-fieldset" disabled={!canEdit}>
        <legend>Send email for</legend>
        {ALERT_SWITCHES.map(({ key, label, detail }) => (
          <label key={key} className="check alerts-switch">
            <input type="checkbox" checked={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: e.target.checked })} />
            <span>
              <strong>{label}</strong>
              <span className="muted small"> {detail}</span>
            </span>
          </label>
        ))}
        <label className="alerts-severity">
          Severity threshold for findings
          <select className="input" value={draft.minSeverity} onChange={(e) => setDraft({ ...draft, minSeverity: Number(e.target.value) })}>
            {SEVERITIES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>
      </fieldset>

      <fieldset className="resource-fieldset" disabled={!canEdit}>
        <legend>Recipients</legend>
        <p className="muted small">
          Each new address gets a confirmation email from AWS and starts receiving alerts once its owner clicks the link.
          The ops address set at deployment always receives them too.
        </p>
        {draft.recipients.length === 0 ? (
          <p className="muted small">No extra recipients.</p>
        ) : (
          <ul className="alerts-recipients">
            {draft.recipients.map((email) => {
              const s = status.get(email);
              return (
                <li key={email}>
                  <span className="mono">{email}</span>
                  {s ? <span className={`pill ${STATE_PILL[s]}`}>{RECIPIENT_STATE_LABEL[s]}</span> : <span className="pill">Unsaved</span>}
                  {canEdit && (
                    <button type="button" className="btn btn-ghost btn-xs" aria-label={`Remove ${email}`}
                      onClick={() => setDraft({ ...draft, recipients: draft.recipients.filter((r) => r !== email) })}>✕</button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
        {canEdit && (
          <div className="alerts-add">
            <input className="input" type="text" inputMode="email" aria-label="Recipient email" placeholder="name@utility.com" value={newRecipient}
              onChange={(e) => setNewRecipient(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} />
            <button type="button" className="btn btn-secondary" onClick={add} disabled={!newRecipient.trim()}>Add</button>
          </div>
        )}
      </fieldset>

      {error && (
        <div className="banner banner-error">
          {error}
          {conflict && <> <button type="button" className="link-button" onClick={onReload}>Reload</button></>}
        </div>
      )}
      <div className="alerts-footer">
        <span className="muted small">
          Sending now: {summarize(view.settings)}.
          {view.settings.updatedAt && <> Changed {relativeTime(view.settings.updatedAt)}{view.settings.updatedBy && ` by ${view.settings.updatedBy}`}.</>}
        </span>
        {canEdit && (
          <div className="dialog-actions">
            <button type="button" className="btn btn-ghost" disabled={!dirty || save.isPending}
              onClick={() => { setDraft(view.settings); setNewRecipient(''); setError(null); }}>Discard</button>
            <button className="btn btn-primary" disabled={(!dirty && !newRecipient.trim()) || save.isPending}>{save.isPending ? 'Saving…' : 'Save'}</button>
          </div>
        )}
      </div>
    </form>
  );
}
