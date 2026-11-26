import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { useSyncExternalStore } from 'react';
import type { AppConfig } from './config';

/**
 * Live push. The server sends `{"topics":["drones","missions"]}` the moment something changes, and we refetch the
 * screens that show it. Messages never carry data, so roles and the guest view's redaction stay in the normal API.
 * In AWS the socket is an API Gateway WebSocket (config.liveUrl); locally it's the API's own /api/live.
 *
 * While connected, queries poll only as a slow safety net; if the socket drops they go back to fast polling until
 * it reconnects.
 */
const affects: Record<string, QueryKey[]> = {
  drones: [['drones'], ['track'], ['stats']],
  missions: [['missions'], ['mission'], ['stats'], ['circuit-flights']],
  findings: [['findings'], ['stats'], ['crews'], ['field-jobs'], ['schedule'], ['assignees'], ['mission'], ['report']],
  resources: [['resources'], ['crews'], ['field-jobs'], ['schedule'], ['report']],
};
const everything = [...new Set(Object.values(affects).flat().map((k) => JSON.stringify(k)))].map((k) => JSON.parse(k) as QueryKey);

/** Several writes land together (a telemetry batch saves the drone and the mission): refetch once for all of them. */
const COALESCE_MS = 150;
const MAX_BACKOFF_MS = 30_000;
/**
 * A flying drone reports every half second or so, and each report is a push. Refetching everything on each one would
 * cost more API calls than the old polling did (and the API is throttled), so each query refetches at most this often.
 * The track is the whole path so far and grows all flight, so it waits longest.
 */
const DEFAULT_GAP_MS = 1_000;
const MIN_GAP_MS: Record<string, number> = { track: 2_000, stats: 3_000, missions: 1_500, mission: 1_500 };

let connected = false;
const listeners = new Set<() => void>();
function setConnected(value: boolean) {
  if (connected === value) return;
  connected = value;
  listeners.forEach((l) => l());
}

export const isLive = () => connected;
export const useLive = () =>
  useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => connected, () => false);

/**
 * A refetchInterval: `fast` while there's no push connection, `slow` while there is. Things that also change with the
 * clock alone (trucks finishing jobs) pass a shorter `slow`.
 */
export const livePoll = (fast: number, slow = 60_000) => () => (connected ? slow : fast);

export function liveUrl(config: AppConfig): string {
  if (config.liveUrl) return config.liveUrl;
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}${config.apiBaseUrl}/live`;
}

export function startLive(config: AppConfig, qc: QueryClient) {
  if (typeof WebSocket === 'undefined') return;
  const url = liveUrl(config);
  let backoff = 1000;
  let pending = new Set<string>();
  let flushTimer: number | undefined;

  // Each kind of query refetches straight away on the first push, then at most once per gap while pushes keep coming
  // (with one trailing refetch, so the last change always shows).
  const last = new Map<string, number>();
  const timers = new Map<string, number>();
  const refetchOne = (queryKey: QueryKey) => {
    const id = String(queryKey[0]);
    const wait = (last.get(id) ?? 0) + (MIN_GAP_MS[id] ?? DEFAULT_GAP_MS) - Date.now();
    if (wait > 0) {
      if (!timers.has(id)) timers.set(id, window.setTimeout(() => { timers.delete(id); refetchOne(queryKey); }, wait));
      return;
    }
    last.set(id, Date.now());
    void qc.invalidateQueries({ queryKey });
  };
  const refetch = (ks: QueryKey[]) => ks.forEach(refetchOne);

  const flush = () => {
    flushTimer = undefined;
    const topics = pending;
    pending = new Set();
    const seen = new Set<string>();
    for (const t of topics) for (const k of affects[t] ?? []) {
      const id = JSON.stringify(k);
      if (!seen.has(id)) { seen.add(id); refetch([k]); }
    }
  };

  const connect = () => {
    let ws: WebSocket;
    try { ws = new WebSocket(url); } catch { schedule(); return; }
    let opened = 0;
    ws.onopen = () => {
      opened = Date.now();
      backoff = 1000;
      setConnected(true);
      refetch(everything); // catch up on anything that changed while we weren't listening
    };
    ws.onmessage = (ev) => {
      let topics: unknown;
      try { topics = (JSON.parse(String(ev.data)) as { topics?: unknown }).topics; } catch { return; }
      if (!Array.isArray(topics)) return;
      topics.forEach((t) => typeof t === 'string' && pending.add(t));
      flushTimer ??= window.setTimeout(flush, COALESCE_MS);
    };
    ws.onclose = () => {
      const wasOpen = opened > 0;
      setConnected(false);
      if (wasOpen) refetch(everything); // back to fast polling now, not after the slow interval runs out
      // API Gateway closes idle sockets after 10 minutes: reconnect straight away after a long, healthy connection.
      if (wasOpen && Date.now() - opened > 60_000) backoff = 1000;
      schedule();
    };
  };

  const schedule = () => {
    const wait = backoff + Math.random() * 500;
    backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
    window.setTimeout(connect, wait);
  };

  connect();
}
