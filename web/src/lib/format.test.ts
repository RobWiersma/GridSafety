import { describe, expect, it } from 'vitest';
import type { FindingView } from '../types';
import { TOWER_ZOOM, circuitsByVoltage, minKvForZoom, miles, relativeTime, severityHex, slaText } from './format';

const now = new Date('2026-11-15T12:00:00Z');

const finding = (status: FindingView['finding']['status'], triageDueAt?: string): FindingView => ({
  finding: {
    id: 'f1', missionId: 'm1', circuitId: 'PITT-MART-115', structureId: 's', structureLabel: 'PM-001',
    type: 'ThermalHotspot', severity: 4, confidence: 0.9, location: { lat: 0, lon: 0 },
    status, createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z', triageDueAt, escalated: false, history: [],
  },
  circuitName: 'Pittsburg - Martinez 115 kV',
  nextStatuses: [],
  overdue: false,
  severityLabel: '',
  suggestedCrew: 'Springfield Hot-Stick Crew',
});

describe('slaText', () => {
  it('counts down to the triage deadline', () => {
    expect(slaText(finding('New', '2026-11-15T15:00:00Z'), now)).toEqual({ text: 'due in 3h', overdue: false });
  });

  it('flags overdue findings', () => {
    expect(slaText(finding('New', '2026-11-15T11:35:00Z'), now)).toEqual({ text: 'overdue 25m', overdue: true });
  });

  it('stops once the finding is triaged or has no SLA', () => {
    expect(slaText(finding('Triaged', '2026-11-15T11:00:00Z'), now)).toBeNull();
    expect(slaText(finding('New'), now)).toBeNull();
  });
});

describe('formatting', () => {
  it('formats relative times both ways', () => {
    expect(relativeTime('2026-11-15T11:59:30Z', now)).toBe('30s ago');
    expect(relativeTime('2026-11-15T09:00:00Z', now)).toBe('3h ago');
    expect(relativeTime('2026-11-15T12:10:00Z', now)).toBe('in 10m');
  });

  it('converts meters to miles', () => {
    expect(miles(1609.344 * 2.5)).toBe('2.5');
  });

  it('maps severity to a distinct color', () => {
    expect(new Set([1, 2, 3, 4, 5].map(severityHex)).size).toBe(5);
  });
});

describe('circuitsByVoltage', () => {
  it('groups highest voltage first, untagged last, names sorted', () => {
    const c = (name: string, voltageKv: number) => ({ name, voltageKv });
    const groups = circuitsByVoltage([c('B line', 60), c('Mystery', 0), c('Tidewater', 230), c('A line', 60)]);
    expect(groups.map((g) => g.label)).toEqual(['230 kV', '60 kV', 'Voltage not tagged']);
    expect(groups[1].circuits.map((x) => x.name)).toEqual(['A line', 'B line']);
  });
});

describe('map detail by zoom', () => {
  it('shows only the backbone zoomed out and everything zoomed in', () => {
    expect(minKvForZoom(9)).toBe(200);
    expect(minKvForZoom(11)).toBe(100);
    expect(minKvForZoom(12)).toBe(100);
    expect(minKvForZoom(13)).toBe(0);
    expect(TOWER_ZOOM).toBeGreaterThan(13);
  });
});
