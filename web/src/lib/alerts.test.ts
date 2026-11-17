import { describe, expect, it } from 'vitest';
import type { AlertSettings } from '../types';
import { MAX_RECIPIENTS, addRecipients, isDirty, summarize } from './alerts';

const defaults: AlertSettings = {
  version: 0, criticalFindings: true, overdueTriage: true, flightAlerts: true, missionComplete: false, minSeverity: 4, recipients: [],
};

describe('addRecipients', () => {
  it('splits pasted lists, lowercases and skips duplicates', () => {
    expect(addRecipients(['lead@example.com'], 'Lead@Example.com, crew@example.org;\nops@example.net')).toEqual({
      recipients: ['lead@example.com', 'crew@example.org', 'ops@example.net'],
    });
  });

  it('rejects an invalid address and adds nothing', () => {
    const r = addRecipients(['lead@example.com'], 'ok@example.com not-an-email');
    expect(r.recipients).toEqual(['lead@example.com']);
    expect(r.error).toContain('not-an-email');
  });

  it('caps the list', () => {
    const full = Array.from({ length: MAX_RECIPIENTS }, (_, i) => `u${i}@example.com`);
    expect(addRecipients(full, 'one@example.com').error).toContain(`${MAX_RECIPIENTS}`);
  });
});

describe('isDirty and summarize', () => {
  it('notices a change to any switch, the threshold or the recipients', () => {
    expect(isDirty(defaults, { ...defaults })).toBe(false);
    expect(isDirty(defaults, { ...defaults, missionComplete: true })).toBe(true);
    expect(isDirty(defaults, { ...defaults, minSeverity: 3 })).toBe(true);
    expect(isDirty(defaults, { ...defaults, recipients: ['a@example.com'] })).toBe(true);
  });

  it('describes what sends email', () => {
    expect(summarize(defaults)).toBe('Critical findings (sev 4+), Overdue triage (sev 4+), Flight alerts');
    expect(summarize({ ...defaults, criticalFindings: false, overdueTriage: false, flightAlerts: false })).toBe('No email alerts');
  });
});
