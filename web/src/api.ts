import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useSession } from './session';
import { canTake } from './lib/field';
import type {
  AlertKind, AlertSettings, AlertSettingsView,
  AssigneeOption, Circuit, Crew, CrewView, FieldJobs, DroneAsset, FlightCrew, Resources, VisitorStats, DroneState, FindingStatus, FindingView, Me, MissionStatus, MissionView, OpsStats, TelemetryPoint, SimulationSettings,
 DroneReport, PilotReport, TechnicianReport, Vehicle, VehicleReport, CrewWorkReport, DepotReport, FlightCrewReport, ActivityReport, RateCard, EmployeeProfile, FindingType, ScheduleView, ScheduleDispatchResult,} from './types';

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
  crews: ['crews'],
  resources: ['resources'],
  alertSettings: ['alert-settings'],
  simulationSettings: ['simulation-settings'],
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

/** Supervisor-only visitor summary for the last N days. */
export const useVisitorStats = (days: number) => {
  const api = useFetcher();
  return useQuery({ queryKey: ['visitors', days], queryFn: () => api<VisitorStats>(`/visitors?days=${days}`), refetchInterval: 60_000 });
};

/** Field crews and their open work-order counts. */
export const useCrews = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.crews, queryFn: () => api<CrewView[]>('/crews'), refetchInterval: 15_000 });
};

/** Trucks out on simulated field jobs (and ones just back), with the server's clock. */
export const useFieldJobs = () => {
  const api = useFetcher();
  return useQuery({ queryKey: ['field-jobs'], queryFn: () => api<FieldJobs>('/field-jobs'), refetchInterval: 5_000 });
};

/** The forward schedule: triaged work orders not sent yet, most important first, with who'd take each and when. */
export const useSchedule = () => {
  const api = useFetcher();
  return useQuery({ queryKey: ['schedule'], queryFn: () => api<ScheduleView>('/schedule'), refetchInterval: 5_000 });
};

/** Sends every work order on the schedule that can start now, most important first (or just one, by finding id). */
export function useDispatchSchedule() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (findingId?: string) =>
      api<ScheduleDispatchResult>(`/schedule/dispatch${findingId ? `?findingId=${encodeURIComponent(findingId)}` : ''}`, { method: 'POST' }),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: ['schedule'] }),
      qc.invalidateQueries({ queryKey: ['findings'] }),
      qc.invalidateQueries({ queryKey: ['field-jobs'] }),
      qc.invalidateQueries({ queryKey: keys.crews }),
      qc.invalidateQueries({ queryKey: keys.stats }),
      qc.invalidateQueries({ queryKey: ['assignees'] }),
    ]),
  });
}

/** Who a finding's work order can go to: qualified, on-duty technicians nearest first, then whole crews. */
export const useAssignees = (findingId: string, enabled = true) => {
  const api = useFetcher();
  return useQuery({ queryKey: ['assignees', findingId], queryFn: () => api<AssigneeOption[]>(`/findings/${findingId}/assignees`), enabled, refetchInterval: 30_000 });
};

/** Drones, flight crews and ground crews, with live state and usage. */
export const useResources = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.resources, queryFn: () => api<Resources>('/resources'), refetchInterval: 15_000 });
};

export type ResourceKind = 'drones' | 'flight-crews' | 'ground-crews' | 'vehicles';
/** A save (with record) or a removal (without) of one drone or crew. */
export type ResourceChange =
  | { kind: 'drones'; id: string; record?: DroneAsset }
  | { kind: 'flight-crews'; id: string; record?: FlightCrew }
  | { kind: 'ground-crews'; id: string; record?: Crew }
  | { kind: 'vehicles'; id: string; record?: Vehicle };

/** Supervisor-only: add or update (PUT) or remove (DELETE) one drone or crew. */
export function useSaveResource() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ResourceChange) =>
      api<unknown>(`/resources/${input.kind}/${encodeURIComponent(input.id)}`, input.record
        ? { method: 'PUT', body: JSON.stringify(input.record) }
        : { method: 'DELETE' }),
    onSuccess: () => Promise.all([
      qc.invalidateQueries({ queryKey: keys.resources }),
      qc.invalidateQueries({ queryKey: keys.crews }),
      qc.invalidateQueries({ queryKey: ['findings'] }),
    ]),
  });
}

/** Supervisor-only: log a repair (into the shop) or a service for a drone or truck. */
export function useLogMaintenance() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { kind: 'drones' | 'vehicles'; id: string; body: { kind: 'Repair' | 'Service'; hours?: number; note?: string; cost?: number } }) =>
      api<unknown>(`/resources/${input.kind}/${encodeURIComponent(input.id)}/maintenance`, { method: 'POST', body: JSON.stringify(input.body) }),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: keys.resources }), qc.invalidateQueries({ queryKey: ['report'] })]),
  });
}

/** Supervisor-only: one employee's details, employee record and own pay rate (null: the standard rate). */
export function useSaveEmployee() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      kind: 'ground-crews' | 'flight-crews'; crewId: string; member: string;
      body: { name: string; role: string; skills: FindingType[] | null; depotId: string | null; profile: EmployeeProfile; payRate: number | null };
    }) =>
      api<unknown>(`/resources/${input.kind}/${encodeURIComponent(input.crewId)}/members/${encodeURIComponent(input.member)}`,
        { method: 'PUT', body: JSON.stringify(input.body) }),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: keys.resources }), qc.invalidateQueries({ queryKey: ['report'] })]),
  });
}

/** The rate card behind every report's costs. */
export const useRates = () => {
  const api = useFetcher();
  return useQuery({ queryKey: ['rates'], queryFn: () => api<RateCard>('/reports/rates'), staleTime: Infinity });
};

/** Supervisor-only: a repair is done, back in service now. */
export function useFinishRepair() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (recordId: string) => api<unknown>(`/resources/maintenance/${encodeURIComponent(recordId)}/done`, { method: 'POST' }),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: keys.resources }), qc.invalidateQueries({ queryKey: ['report'] })]),
  });
}

/** Email alert settings and where each recipient's subscription stands. Signed-in users only (it lists addresses). */
export const useAlertSettings = (enabled = true) => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.alertSettings, queryFn: () => api<AlertSettingsView>('/alert-settings'), enabled });
};

/** Supervisor-only: replace the alert settings. Fails with 409 if someone else saved since `version` was read. */
export function useSaveAlertSettings() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (settings: AlertSettings) => api<AlertSettingsView>('/alert-settings', { method: 'PUT', body: JSON.stringify(settings) }),
    onSuccess: (view) => qc.setQueryData(keys.alertSettings, view),
  });
}

/** How many findings simulated flights report. */
export const useSimulationSettings = () => {
  const api = useFetcher();
  return useQuery({ queryKey: keys.simulationSettings, queryFn: () => api<SimulationSettings>('/simulation-settings') });
};

/** Supervisor-only: replace the simulation settings. Fails with 409 if someone else saved since `version` was read. */
export function useSaveSimulationSettings() {
  const api = useFetcher();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (settings: SimulationSettings) => api<SimulationSettings>('/simulation-settings', { method: 'PUT', body: JSON.stringify(settings) }),
    onSuccess: (saved) => qc.setQueryData(keys.simulationSettings, saved),
  });
}

/** One mission, polled while it is in the air. */
export const useMission = (id: string) => {
  const api = useFetcher();
  return useQuery({
    queryKey: ['mission', id],
    queryFn: () => api<MissionView>(`/missions/${id}`),
    // Poll while airborne, or while a simulated flight is taking off.
    refetchInterval: (q) => {
      const m = q.state.data?.mission;
      return m?.status === 'InFlight' || (m?.status === 'Planned' && m.simulatedAt) ? 3000 : false;
    },
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

/**
 * The capture over real aerial imagery of the finding's structure, as a data: URL once ready. The first request
 * starts a background fetch (15–30 s), so this polls while "pending". With fetch=false it only reads the cache.
 */
export const useFindingAerial = (findingId: string, fetch = true) => {
  const api = useFetcher();
  return useQuery({
    queryKey: ['finding-aerial', findingId, fetch],
    queryFn: async () => {
      const v = await api<{ status: 'ready' | 'pending' | 'unavailable'; svg?: string }>(
        `/findings/${findingId}/aerial${fetch ? '' : '?fetch=false'}`);
      return { status: v.status, url: v.svg ? `data:image/svg+xml;charset=utf-8,${encodeURIComponent(v.svg)}` : undefined };
    },
    refetchInterval: (q) => (q.state.data?.status === 'pending' ? 4000 : false),
    staleTime: (q) => (q.state.data?.status === 'ready' ? Infinity : 0),
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
      qc.invalidateQueries({ queryKey: keys.crews }),
      qc.invalidateQueries({ queryKey: keys.resources }),
      qc.invalidateQueries({ queryKey: ['field-jobs'] }),
      qc.invalidateQueries({ queryKey: ['assignees'] }),
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

/** Supervisor-only: fly a planned mission now with a simulated drone. */
export function useSimulateMission() {
  const api = useFetcher();
  const qc = useQueryClient();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (missionId: string) => api<MissionView>(`/missions/${encodeURIComponent(missionId)}/simulate`, { method: 'POST' }),
    onSuccess: (m) => {
      qc.setQueryData(['mission', m.mission.id], m);
      return invalidate();
    },
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

/** Acknowledge one flight alert (by time and kind), or every open alert on the mission when `at` is omitted. */
export function useAcknowledgeAlerts() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ missionId, at, kind }: { missionId: string; at?: string; kind?: AlertKind }) =>
      api<MissionView>(`/missions/${missionId}/alerts/ack`, { method: 'POST', body: JSON.stringify({ at, kind }) }),
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

export interface BulkResult {
  done: { label: string; who?: string }[];
  failed: { label: string; reason: string }[];
}

/**
 * Triage or assign several findings, one after another. Each assignment goes to whoever is nearest, qualified and free
 * at that moment, so nearby jobs line up behind one technician the way they would if assigned by hand.
 */
export function useBulkFindings() {
  const api = useFetcher();
  const invalidate = useInvalidate();
  const post = (id: string, status: FindingStatus, assignee?: string) =>
    api<FindingView>(`/findings/${id}/status`, { method: 'POST', body: JSON.stringify({ status, assignee }) });
  const run = async (findings: FindingView[], one: (f: FindingView) => Promise<string | undefined>, onProgress?: (done: number) => void) => {
    const result: BulkResult = { done: [], failed: [] };
    try {
      for (const [i, f] of findings.entries()) {
        onProgress?.(i);
        try {
          result.done.push({ label: f.finding.structureLabel, who: await one(f) });
        } catch (err) {
          result.failed.push({ label: f.finding.structureLabel, reason: err instanceof Error ? err.message : String(err) });
        }
      }
    } finally {
      await invalidate();
    }
    return result;
  };
  return {
    triage: (findings: FindingView[], onProgress?: (done: number) => void) =>
      run(findings.filter((f) => f.finding.status === 'New'), async (f) => { await post(f.finding.id, 'Triaged'); return undefined; }, onProgress),
    /** Worst first, so the most urgent jobs get the nearest technicians; untriaged ones are triaged on the way. */
    assignNearest: (findings: FindingView[], onProgress?: (done: number) => void) =>
      run([...findings].sort((a, b) => b.finding.severity - a.finding.severity), async (f) => {
        if (f.finding.status === 'New') await post(f.finding.id, 'Triaged');
        const options = await api<AssigneeOption[]>(`/findings/${f.finding.id}/assignees`);
        const pick = options.find(canTake);
        if (!pick) throw new Error('nobody qualified is free (out, resting or out of hours)');
        await post(f.finding.id, 'Assigned', pick.label);
        return pick.member ?? pick.crewName;
      }, onProgress),
  };
}

/** Whose report to show: a drone's or pilot's flight log, or a technician's shifts. */
export type ReportTarget =
  | { kind: 'drone'; id: string }
  | { kind: 'pilot'; crewId: string; member: string }
  | { kind: 'technician'; crewId: string; member: string }
  | { kind: 'truck'; id: string }
  | { kind: 'ground-crew'; crewId: string; name: string }
  | { kind: 'flight-crew'; crewId: string; name: string }
  | { kind: 'depot'; id: string; name: string }
  | { kind: 'activity' };

export type AnyReport = TechnicianReport | DroneReport | PilotReport | VehicleReport | CrewWorkReport | DepotReport | FlightCrewReport | ActivityReport;

function reportPath(t: ReportTarget) {
  const e = encodeURIComponent;
  switch (t.kind) {
    case 'drone': return `/resources/drones/${e(t.id)}/report`;
    case 'pilot': return `/resources/flight-crews/${e(t.crewId)}/members/${e(t.member)}/report`;
    case 'technician': return `/resources/ground-crews/${e(t.crewId)}/members/${e(t.member)}/report`;
    case 'truck': return `/resources/vehicles/${e(t.id)}/report`;
    case 'ground-crew': return `/resources/ground-crews/${e(t.crewId)}/report`;
    case 'flight-crew': return `/resources/flight-crews/${e(t.crewId)}/report`;
    case 'depot': return `/resources/depots/${e(t.id)}/report`;
    case 'activity': return '/reports/activity';
  }
}

export function useReport(target: ReportTarget) {
  const api = useFetcher();
  const path = reportPath(target);
  return useQuery({
    queryKey: ['report', path],
    queryFn: () => api<AnyReport>(path),
    refetchInterval: 10_000,
  });
}

export function useOpenReport() {
  const api = useFetcher();
  return async (missionId: string) => {
    const { url } = await api<{ url: string }>(`/missions/${missionId}/report-url`);
    window.open(url, '_blank', 'noopener');
  };
}
