export interface GeoPoint {
  lat: number;
  lon: number;
}

export interface Structure {
  id: string;
  label: string;
  location: GeoPoint;
}

export interface Circuit {
  id: string;
  name: string;
  voltageKv: number;
  corridorMeters: number;
  structures: Structure[];
  lengthMeters: number;
}

export type MissionStatus = 'Planned' | 'InFlight' | 'Completed' | 'Aborted';
export type FindingStatus = 'New' | 'Triaged' | 'Assigned' | 'Resolved' | 'Dismissed';
export type FindingType =
  | 'VegetationEncroachment'
  | 'DamagedInsulator'
  | 'CrossarmDamage'
  | 'ThermalHotspot'
  | 'MissingHardware'
  | 'ConductorDamage';
export type AlertKind = 'LowBattery' | 'CriticalBattery' | 'CorridorBreach' | 'AltitudeCeiling' | 'WeakSignal' | 'LinkCritical';

export interface StatusChange<T> {
  from: T;
  to: T;
  at: string;
  by?: string;
  note?: string;
}

export interface DroneAlert {
  kind: AlertKind;
  level: 'Warning' | 'Critical';
  message: string;
  at: string;
  position: GeoPoint;
  acknowledgedAt?: string;
  acknowledgedBy?: string;
}

export interface Mission {
  id: string;
  circuitId: string;
  droneId: string;
  pilot: string;
  createdBy?: string;
  status: MissionStatus;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  endedAt?: string;
  lastPosition?: GeoPoint;
  batteryPct?: number;
  rssiDbm?: number;
  distanceFlownMeters: number;
  structuresInspected: string[];
  findingCount: number;
  recentAlerts: DroneAlert[];
  reportKey?: string;
  history: StatusChange<MissionStatus>[];
}

export interface MissionView {
  mission: Mission;
  circuitName: string;
  coverage: number;
  nextStatuses: MissionStatus[];
}

export interface Finding {
  id: string;
  missionId: string;
  circuitId: string;
  structureId: string;
  structureLabel: string;
  type: FindingType;
  severity: number;
  confidence: number;
  location: GeoPoint;
  description?: string;
  status: FindingStatus;
  assignedTo?: string;
  createdAt: string;
  updatedAt: string;
  triageDueAt?: string;
  escalated: boolean;
  history: StatusChange<FindingStatus>[];
}

export interface FindingView {
  finding: Finding;
  circuitName: string;
  nextStatuses: FindingStatus[];
  overdue: boolean;
  severityLabel: string;
  suggestedCrew: string;
}

export interface CountView {
  name: string;
  count: number;
}

export interface VisitorStats {
  days: number;
  pageViews: number;
  visitorDays: number;
  signedInVisitorDays: number;
  daily: { day: string; pageViews: number; uniqueVisitors: number }[];
  pages: CountView[];
  referrers: CountView[];
  devices: CountView[];
  browsers: CountView[];
  operatingSystems: CountView[];
  countries: CountView[];
  languages: CountView[];
}

export interface CrewMember {
  name: string;
  role: string;
}

export interface Crew {
  id: string;
  name: string;
  specialty: string;
  handles: FindingType[];
  members: CrewMember[];
}

export interface CrewView {
  crew: Crew;
  openWorkOrders: number;
}

export interface DroneState {
  droneId: string;
  status: 'Idle' | 'Flying';
  missionId?: string;
  circuitId?: string;
  position: GeoPoint;
  altitudeM: number;
  batteryPct: number;
  speedMps: number;
  rssiDbm?: number;
  lastSeen: string;
  activeAlerts: AlertKind[];
}

export interface TelemetryPoint {
  timestamp: string;
  lat: number;
  lon: number;
  altitudeM: number;
  batteryPct: number;
  speedMps: number;
  /** Control-link signal strength in dBm; absent for drones that don't report it. */
  rssiDbm?: number;
}

export interface OpsStats {
  activeMissions: number;
  dronesFlying: number;
  missionsLast7Days: number;
  milesFlownLast7Days: number;
  openFindings: number;
  openBySeverity: Record<string, number>;
  overdueTriage: number;
  medianHoursToTriage?: number;
}

export interface Me {
  id: string;
  name: string;
  isSupervisor: boolean;
  groups: string[];
  isGuest?: boolean;
}
