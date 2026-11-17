import type { AlertSettings, RecipientState } from '../types';

export const MAX_RECIPIENTS = 10;

type Switch = 'criticalFindings' | 'overdueTriage' | 'flightAlerts' | 'missionComplete';

/** The alert types in the order the settings page lists them. */
export const ALERT_SWITCHES: { key: Switch; label: string; detail: string; usesSeverity: boolean }[] = [
  { key: 'criticalFindings', label: 'Critical findings', detail: 'A drone reports a finding at or above the severity threshold.', usesSeverity: true },
  { key: 'overdueTriage', label: 'Overdue triage', detail: 'A finding at or above the threshold misses its triage deadline.', usesSeverity: true },
  { key: 'flightAlerts', label: 'Flight alerts', detail: 'A critical in-flight alert: corridor breach, low battery or lost link.', usesSeverity: false },
  { key: 'missionComplete', label: 'Mission complete', detail: 'A mission lands (completed) or is aborted.', usesSeverity: false },
];

export const RECIPIENT_STATE_LABEL: Record<RecipientState, string> = {
  Confirmed: 'Receiving',
  Pending: 'Awaiting confirmation',
  NotSubscribed: 'Not subscribed',
  Local: 'Local only',
  Unknown: 'Status unknown',
};

const EMAIL = /^[^@\s,;<>"]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$/;

/**
 * Adds addresses typed or pasted into the recipient box (comma, semicolon, space or newline separated).
 * Returns the new list, or an error naming the first problem; nothing is added when there is an error.
 */
export function addRecipients(current: string[], input: string): { recipients: string[]; error?: string } {
  const typed = input.split(/[\s,;]+/).map((s) => s.trim().toLowerCase()).filter(Boolean);
  const bad = typed.find((e) => e.length > 254 || !EMAIL.test(e));
  if (bad) return { recipients: current, error: `Not a valid email address: ${bad}` };
  const next = [...current];
  for (const e of typed) if (!next.includes(e)) next.push(e);
  if (next.length > MAX_RECIPIENTS) return { recipients: current, error: `At most ${MAX_RECIPIENTS} recipients.` };
  return { recipients: next };
}

/** Whether the form differs from what was last saved. */
export function isDirty(saved: AlertSettings, draft: AlertSettings): boolean {
  return ALERT_SWITCHES.some(({ key }) => saved[key] !== draft[key])
    || saved.minSeverity !== draft.minSeverity
    || saved.recipients.join('\n') !== draft.recipients.join('\n');
}

/** A one-line summary of what currently sends email, e.g. "Critical findings (sev 4+), Flight alerts". */
export function summarize(s: AlertSettings): string {
  const on = ALERT_SWITCHES.filter(({ key }) => s[key]).map(({ label, usesSeverity }) => (usesSeverity ? `${label} (sev ${s.minSeverity}+)` : label));
  return on.length ? on.join(', ') : 'No email alerts';
}
