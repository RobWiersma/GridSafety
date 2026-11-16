import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useSession } from './session';
import type {
  Circuit, DroneState, FindingStatus, FindingView, Me, MissionStatus, MissionView, OpsStats, TelemetryPoint,
} from './types';

export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors?: Record<string, string[]>) {
    super(message);
  }
}

export type Fetcher = <T>(path: string, init?: RequestInit) => Promise<T>;

export function createFetcher(baseUrl: string, authHeaders: () => Record<string, string>): Fetcher {
  return async <T,>(path: string, init: RequestInit = {}) => {
    const res = await fetch(`${baseUrl}${path}`, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...authHeaders(), ...init.headers },
    });
    if (res.status === 204) return undefined as T;
    const body = await res.text();
    let data: unknown;
    try {
      data = body ? JSON.parse(body) : undefined;
    } catch {
      data = body;
    }
    if (!res.ok) {
      // ASP.NET returns RFC 9457 problem details; surface field errors when present.
      const p = data as { errors?: Record<string, string[]>; detail?: string; title?: string } | string | undefined;
      const errors = typeof p === 'object' ? p?.errors : undefined;
      const message = errors
        ? Object.values(errors).flat().join(' ')
        : typeof p === 'string' ? p : (p?.detail ?? p?.title ?? res.statusText);
      throw new ApiError(res.status, message, errors);
    }
    return data as T;
  };
}

export const FetcherContext = createContext<Fetcher | null>(null);
const useFetcher = () => {
  const f = useContext(FetcherContext);
  if (!f) throw new Error('FetcherContext missing');
  return f;
};

const keys = {
  me: ['me'],
  circuits: ['circuits'],
  stats: ['stats'],
  drones: ['drones'],
  missions: ['missions'],
  track: (id: string) => ['track', id],
  findings: (filter: string) => ['findings', filter],
};

export const useMe = () => {
  const api = useFetcher();
  // Keyed by user so a role switch (or re-login) never shows another user's rights.
  const { displayName } = useSession();
  return useQuery({ queryKey: [...keys.me, displayName], queryFn: () => api<Me>('/me'), staleTime: 60_000 });
};

export const useCircuits = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.circuits, queryFn: () => api<Circuit[]>('/circuits'), staleTime: Infinity });
};

/** Live ops data polls; React Query pauses polling when the tab is hidden. */
export const useStats = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.stats, queryFn: () => api<OpsStats>('/stats'), refetchInterval: 5000 });
};

export const useDrones = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.drones, queryFn: () => api<DroneState[]>('/drones'), refetchInterval: 2000 });
};

export const useMissions = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.missions, queryFn: () => api<MissionView[]>('/missions'), refetchInterval: 3000 });
};

export const useTrack = (missionId: string | null, live: boolean) => {
  const api = useFetcher();
  return useQuery({
    queryKey: keys.track(missionId ?? ''),
    queryFn: () => api<TelemetryPoint[]>(`/missions/${missionId}/track`),
    enabled: Boolean(missionId),
    refetchInterval: live ? 2000 : false,
  });
};

export const useFindings = (filter: string) => {
  const api = useFetcher();
  return useQuery({
    queryKey: keys.findings(filter),
    queryFn: () => api<FindingView[]>(`/findings?status=${encodeURIComponent(filter)}`),
    refetchInterval: 5000,
  });
};

/** One mission, polled while it is in the air. */
export const useMission = (id: string) => {
  const api = useFetcher();
  return useQuery({
    queryKey: ['mission', id],
    queryFn: () => api<MissionView>(`/missions/${id}`),
    refetchInterval: (q) => (q.state.data?.mission.status === 'InFlight' ? 3000 : false),
  });
};

export const useMissionFindings = (missionId: string, live: boolean) => {
  const api = useFetcher();
  return useQuery({
    queryKey: ['findings', `mission:${missionId}`],
    queryFn: () => api<FindingView[]>(`/findings?status=all&missionId=${encodeURIComponent(missionId)}`),
    refetchInterval: live ? 3000 : false,
  });
};

/** Synthetic drone capture as a data: URL. It never changes for a finding, so it is fetched once. */
export const useFindingImage = (findingId: string) => {
  const api = useFetcher();
  return useQuery({
    queryKey: ['finding-image', findingId],
    queryFn: async () => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(await api<string>(`/findings/${findingId}/image`))}`,
    staleTime: Infinity,
  });
};

function useInvalidate() {
  const qc = useQueryClient();
  return () =>
    Promise.all([
      qc.invalidateQueries({ queryKey: keys.missions }),
      qc.invalidateQueries({ queryKey: ['findings'] }),
      qc.invalidateQueries({ queryKey: keys.stats }),
    ]);
}

export function usePlanMission() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { circuitId: string; droneId: string; pilot: string }) =>
      api<MissionView>('/missions', { method: 'POST', body: JSON.stringify(input) }),
    onSuccess: invalidate,
  });
}

/** Supervisor-only: plan a mission for the demo drone and fly it now (on demand, nothing is scheduled). */
export function useDemoFlight() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (circuitId?: string) => api<MissionView>('/demo-flights', { method: 'POST', body: JSON.stringify({ circuitId }) }),
    onSuccess: invalidate,
  });
}

export function useMissionStatus() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ id, status, note }: { id: string; status: MissionStatus; note?: string }) =>
      api<MissionView>(`/missions/${id}/status`, { method: 'POST', body: JSON.stringify({ status, note }) }),
    onSuccess: invalidate,
  });
}

export function useFindingStatus() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ id, status, note, assignee }: { id: string; status: FindingStatus; note?: string; assignee?: string }) =>
      api<FindingView>(`/findings/${id}/status`, { method: 'POST', body: JSON.stringify({ status, note, assignee }) }),
    onSuccess: invalidate,
  });
}

export function useOpenReport() {
  const api = useFetcher();
  return async (missionId: string) => {
    const { url } = await api<{ url: string }>(`/missions/${missionId}/report-url`);
    window.open(url, '_blank', 'noopener');
  };
}
