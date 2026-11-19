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
  /** Callsign, e.g. "Amber Kestrel 07"; demo flights always have one. */
  name?: string;
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
  /** Set when a simulated flight was launched for this mission. */
  simulatedAt?: string;
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
  /** The simulated truck roll sent when the work order was assigned. */
  dispatch?: FieldDispatch;
}

/** Queued: still on an earlier job, comes here next. MovedOn: done here, drove on to the next job instead of back. */
export type FieldPhase = 'Queued' | 'EnRoute' | 'OnSite' | 'Returning' | 'Back' | 'MovedOn';

/**
 * A simulated truck roll along the roads: depot (or the previous job) → site → repair → depot (or on to the next job),
 * all timed when the work order was assigned (sped up for the demo).
 */
export interface FieldDispatch {
  assignee: string;
  crewId: string;
  /** The technician sent; absent when the whole crew went. */
  member?: string;
  depotId: string;
  depotName: string;
  depot: GeoPoint;
  site: GeoPoint;
  siteLabel: string;
  from: GeoPoint;
  fromLabel: string;
  /** The drive out, from → site. */
  route: GeoPoint[];
  /** The drive back to the depot; just the site when the truck goes on to another job. */
  returnRoute: GeoPoint[];
  /** False when no road route was found and the truck drives in straight lines. */
  onRoads: boolean;
  departedAt: string;
  arrivesAt: string;
  repairedAt: string;
  returnsAt: string;
  /** The work order this truck goes on to after this one. */
  nextFindingId?: string;
  nextSiteLabel?: string;
  /** Set when the work order was closed by hand before the repair finished; the truck turned back. */
  recalledAt?: string;
}

export interface FieldJob {
  findingId: string;
  structureLabel: string;
  type: FindingType;
  severity: number;
  dispatch: FieldDispatch;
  phase: FieldPhase;
}

export interface FieldJobs {
  now: string;
  jobs: FieldJob[];
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
  /** Ground crews: issue types this technician can fix; absent means whatever the crew handles. */
  skills?: FindingType[];
  /** Ground crews: the depot this technician departs from; absent means the crew's depot. */
  depotId?: string;
}

/** A yard ground crews and technicians depart from. */
export interface Depot {
  id: string;
  name: string;
  location: { lat: number; lon: number };
}

/** Someone a work order can go to, nearest first: a technician (member set) or a whole crew. */
export interface AssigneeOption {
  label: string;
  crewId: string;
  crewName: string;
  member?: string;
  role?: string;
  depotId?: string;
  depotName?: string;
  distanceKm?: number;
  crewOpenWorkOrders: number;
  /** Out on a job elsewhere until then; can't be assigned this one. */
  busyUntil?: string;
  /** Already out nearby: would come here after this job (structure label); distance is from there. */
  afterJob?: string;
  /** Simulated hours of their shift still free, and roughly how many this job would take. */
  hoursLeft?: number;
  jobHours?: number;
  /** Off, resting after a full shift, until then. */
  restingUntil?: string;
}

export type CrewStatus = 'Available' | 'OffDuty';

export interface Crew {
  id: string;
  name: string;
  specialty: string;
  handles: FindingType[];
  members: CrewMember[];
  status: CrewStatus;
  /** The depot the crew rolls out of. */
  depotId?: string;
}

export type DroneAssetStatus = 'Active' | 'Maintenance' | 'Retired';

export interface DroneAsset {
  id: string;
  model: string;
  homeBase: string;
  status: DroneAssetStatus;
  notes?: string;
}

export interface FlightCrew {
  id: string;
  name: string;
  homeBase: string;
  members: CrewMember[];
  status: CrewStatus;
}

export interface DroneResourceView {
  drone: DroneAsset;
  live?: DroneState;
  activeMissionId?: string;
  activeMissionName?: string;
  missions: number;
  flightHours: number;
  distanceMeters: number;
  lastFlight?: string;
}

export interface FlightCrewView {
  crew: FlightCrew;
  missions: number;
  activeMissions: number;
  lastFlight?: string;
}

export interface Resources {
  drones: DroneResourceView[];
  flightCrews: FlightCrewView[];
  groundCrews: CrewView[];
  depots: Depot[];
}

export interface CrewView {
  crew: Crew;
  openWorkOrders: number;
  /** Jobs this crew's people are out on right now. */
  inField?: FieldDispatch[];
  /** Each member's hours this shift. */
  shifts?: MemberShift[];
}

/** One technician's shift, in simulated hours (a shift is SHIFT_HOURS of driving and repairs). */
export interface MemberShift {
  member: string;
  /** Up to now. */
  workedHours: number;
  /** Including the jobs already lined up. */
  bookedHours: number;
  leftHours: number;
  startedAt?: string;
  /** Off, resting after a full shift, until then. */
  restingUntil?: string;
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

export type AlertType = 'CriticalFinding' | 'OverdueTriage' | 'FlightAlert' | 'MissionComplete';

/** Which events send email and to whom. Supervisors edit it; `version` guards against overwriting someone else's save. */
export interface SimulationSettings {
  version: number;
  /** Exactly this many findings per simulated flight, or absent for random. */
  findingsPerFlight?: number;
  /** With a random count: chance (0–100) that a flight finds nothing. */
  cleanFlightPercent: number;
  updatedBy?: string;
  updatedAt?: string;
}

export interface AlertSettings {
  version: number;
  criticalFindings: boolean;
  overdueTriage: boolean;
  flightAlerts: boolean;
  missionComplete: boolean;
  /** Findings below this severity (1–5) never send email, new or overdue. */
  minSeverity: number;
  recipients: string[];
  updatedBy?: string;
  updatedAt?: string;
}

/** Confirmed: receiving email. Pending: hasn't clicked the confirmation link yet. Local: dev mode, nothing is sent. */
export type RecipientState = 'Confirmed' | 'Pending' | 'NotSubscribed' | 'Local' | 'Unknown';

export interface AlertSettingsView {
  settings: AlertSettings;
  recipients: { email: string; status: RecipientState }[];
  emailEnabled: boolean;
  warning?: string;
}

// ---- Reports: technicians' shifts, drones' and pilots' flight logs ----

export type ShiftStatus = 'Working' | 'OnCall' | 'Resting' | 'Done';
export type JobOutcome = 'Repaired' | 'CalledBack' | 'InProgress' | 'LinedUp';

/** One job in a shift; hours are simulated. */
export interface ShiftJob {
  findingId: string;
  structureLabel: string;
  type: FindingType;
  severity: number;
  assignee: string;
  fromLabel: string;
  departedAt: string;
  arrivesAt: string;
  endsAt: string;
  driveHours: number;
  repairHours: number;
  outcome: JobOutcome;
  nextSiteLabel?: string;
  onRoads: boolean;
}

export interface ShiftReport {
  startedAt: string;
  endsAt: string;
  status: ShiftStatus;
  workedHours: number;
  bookedHours: number;
  driveHours: number;
  repairHours: number;
  repaired: number;
  /** The break before their next shift, once there is one. */
  restHours?: number;
  limitHours: number;
  jobs: ShiftJob[];
}

export interface TechnicianReport {
  crewId: string;
  crewName: string;
  member: string;
  role: string;
  depotName?: string;
  current: MemberShift;
  shifts: ShiftReport[];
  now: string;
}

export interface FlightLogEntry {
  missionId: string;
  name?: string;
  circuitId: string;
  circuitName: string;
  droneId: string;
  pilot: string;
  status: MissionStatus;
  plannedAt: string;
  startedAt?: string;
  endedAt?: string;
  flightMinutes: number;
  distanceMeters: number;
  coverage: number;
  structuresInspected: number;
  findings: number;
  alerts: number;
  criticalAlerts: number;
  endBatteryPct?: number;
}

export interface FlightReport {
  totals: {
    missions: number;
    completed: number;
    aborted: number;
    flightHours: number;
    distanceMeters: number;
    structuresInspected: number;
    findings: number;
    alertsByKind: Partial<Record<AlertKind, number>>;
    firstFlight?: string;
    lastFlight?: string;
  };
  flights: FlightLogEntry[];
}

export interface DroneReport { drone: DroneAsset; report: FlightReport; now: string }
export interface PilotReport { crewId: string; crewName: string; member: string; role: string; report: FlightReport; now: string }
