import { useEffect, useState } from 'react';
import type { FieldDispatch, FieldPhase, GeoPoint } from '../types';

/**
 * Simulated truck rolls, mirroring FieldDispatch on the server: everything is fixed when the work order is assigned
 * (routes included), so the browser animates trucks from the timestamps alone and only polls for new jobs.
 */

const t = (iso: string) => Date.parse(iso);

export function phaseAt(d: FieldDispatch, now: number): FieldPhase {
  if (now >= t(d.returnsAt)) return d.nextFindingId ? 'MovedOn' : 'Back';
  if (now < t(d.departedAt)) return 'Queued';
  if (d.recalledAt && now >= t(d.recalledAt)) return 'Returning';
  if (now < t(d.arrivesAt)) return 'EnRoute';
  if (now < t(d.repairedAt)) return 'OnSite';
  return 'Returning';
}

const progress = (from: number, to: number, now: number) => (to <= from ? 1 : Math.min(1, Math.max(0, (now - from) / (to - from))));

const RAD = Math.PI / 180;
/** Metres between two points (equirectangular: plenty for a few km of road). */
function metres(a: GeoPoint, b: GeoPoint) {
  const x = (b.lon - a.lon) * RAD * Math.cos(((a.lat + b.lat) / 2) * RAD);
  const y = (b.lat - a.lat) * RAD;
  return Math.hypot(x, y) * 6_371_000;
}

/** The point `fraction` of the way along a path, and the index of the leg it's on. */
function split(path: GeoPoint[], fraction: number): { at: GeoPoint; leg: number } {
  if (path.length < 2 || fraction <= 0) return { at: path[0], leg: 0 };
  const legs = path.slice(1).map((p, i) => metres(path[i], p));
  let target = legs.reduce((a, b) => a + b, 0) * Math.min(1, fraction);
  for (let i = 0; i < legs.length; i++) {
    if (target <= legs[i]) {
      const k = legs[i] === 0 ? 0 : target / legs[i];
      const [a, b] = [path[i], path[i + 1]];
      return { at: { lat: a.lat + (b.lat - a.lat) * k, lon: a.lon + (b.lon - a.lon) * k }, leg: i };
    }
    target -= legs[i];
  }
  return { at: path[path.length - 1], leg: path.length - 2 };
}

/** Which leg the truck is driving now, and how far along it. */
function current(d: FieldDispatch, now: number): { path: GeoPoint[]; fraction: number } | null {
  switch (phaseAt(d, now)) {
    case 'EnRoute': return { path: d.route, fraction: progress(t(d.departedAt), t(d.arrivesAt), now) };
    case 'Returning': return { path: d.returnRoute, fraction: progress(t(d.recalledAt ?? d.repairedAt), t(d.returnsAt), now) };
    default: return null;
  }
}

/** Where the truck is, moving at a steady speed along its road route. */
export function positionAt(d: FieldDispatch, now: number): GeoPoint {
  const leg = current(d, now);
  if (leg) return split(leg.path, leg.fraction).at;
  switch (phaseAt(d, now)) {
    case 'Queued': return d.from;
    case 'OnSite': return d.site;
    case 'MovedOn': return d.returnRoute[d.returnRoute.length - 1];
    default: return d.depot;
  }
}

/** The road still ahead of the truck on its current drive, from where it is now; empty when it isn't driving. */
export function roadAhead(d: FieldDispatch, now: number): GeoPoint[] {
  const leg = current(d, now);
  if (!leg) return [];
  const { at, leg: i } = split(leg.path, leg.fraction);
  return [at, ...leg.path.slice(i + 1)];
}

/** The moment the current phase ends. */
export function phaseEndsAt(d: FieldDispatch, now: number): number {
  switch (phaseAt(d, now)) {
    case 'Queued': return t(d.departedAt);
    case 'EnRoute': return t(d.arrivesAt);
    case 'OnSite': return t(d.repairedAt);
    default: return t(d.returnsAt);
  }
}

/** Hours of driving and repairs per shift, then a rest (mirrors Shifts on the server). */
export const SHIFT_HOURS = 8;
export const REST_HOURS = 10;

/** Whether this option can take the job now: not out elsewhere, not resting, enough hours left. */
export const canTake = (o: { busyUntil?: string; restingUntil?: string; hoursLeft?: number; jobHours?: number }) =>
  !o.busyUntil && !o.restingUntil && !(o.hoursLeft != null && o.jobHours != null && o.jobHours > o.hoursLeft);

export const PHASE_LABEL: Record<FieldPhase, string> = {
  Queued: 'Next up',
  EnRoute: 'Driving to site',
  OnSite: 'Repairing',
  Returning: 'Driving back',
  Back: 'Back at depot',
  MovedOn: 'Moved on to next job',
};

/** What the truck is doing, with a countdown: "Repairing · 0:42", "Next up after SA-010 · 1:05". */
export function statusText(d: FieldDispatch, now: number) {
  const phase = phaseAt(d, now);
  if (phase === 'Back') return `Back at ${d.depotName}`;
  if (phase === 'MovedOn') return `Gone on to ${d.nextSiteLabel ?? 'the next job'}`;
  const label = phase === 'Queued' ? `Next up after ${d.fromLabel}` : phase === 'OnSite' && d.nextSiteLabel ? `Repairing, then ${d.nextSiteLabel}` : PHASE_LABEL[phase];
  return `${label} · ${countdown(phaseEndsAt(d, now), now)}`;
}

/** "1:05" until the moment, or "now". */
export function countdown(until: number, now: number) {
  const s = Math.max(0, Math.round((until - now) / 1000));
  return s === 0 ? 'now' : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/** Who the job ties up: the technician sent, or the whole crew. */
export const dispatchName = (d: FieldDispatch) => d.member ?? d.assignee;

/** Whether this job ties up a member of the crew (or, with no member given, the crew as a whole). */
export const involves = (d: FieldDispatch, crewId: string, member?: string) =>
  d.crewId === crewId && (!d.member || !member || d.member.toLowerCase() === member.toLowerCase());

/**
 * The server's clock, ticking once a second. Anchored to the server time of the last response, so trucks don't jump
 * when the visitor's clock is off.
 */
export function useServerClock(serverNow: string | undefined, intervalMs = 1000) {
  const [offset, setOffset] = useState(0);
  useEffect(() => {
    if (serverNow) setOffset(Date.parse(serverNow) - Date.now());
  }, [serverNow]);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now + offset;
}
