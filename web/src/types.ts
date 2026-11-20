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
  /** Which flight of the line (1-based): long lines take several ~20 min flights. Missing on older missions (whole line). */
  leg?: number;
  /** How many flights the whole line takes. */
  legs?: number;
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
  /** The stretch of line this mission flies (missing on missions from before lines were split). */
  flight?: CircuitLeg | null;
}

/** One flight's stretch of a line, structures firstIndex..lastIndex; neighbouring flights share their end structure. */
export interface CircuitLeg {
  number: number;
  of: number;
  firstIndex: number;
  lastIndex: number;
  fromLabel: string;
  toLabel: string;
  structures: number;
  lengthMeters: number;
  /** Expected time in the air, takeoff and landing included. */
  minutes: number;
}

export interface CircuitFlightView {
  leg: CircuitLeg;
  lastInspected?: string | null;
  /** A mission is planned or in the air for this flight now. */
  inProgress: boolean;
}

/** A line's flights, one battery each, and which one is due next. */
export interface CircuitFlightsView {
  circuitId: string;
  circuitName: string;
  lengthMeters: number;
  enduranceMinutes: number;
  flights: CircuitFlightView[];
  nextLeg: number;
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
  /** The truck, e.g. "T-071"; absent for truck rolls from before vehicles were tracked. */
  vehicleId?: string;
  /** e.g. "T-071 · Bucket truck". */
  vehicleLabel?: string;
  /** Everyone in the truck, the technician doing this repair included; absent when a whole crew went. */
  riders?: Rider[];
}

/** Someone in a truck: technicians from the same yard ride together, whatever their crew. */
export interface Rider {
  crewId: string;
  name: string;
}

export type VehicleKind = 'BucketTruck' | 'LineTruck' | 'Pickup';
export type VehicleStatus = 'InService' | 'OutOfService';

/** A truck kept at a depot; seats is how many people ride in it. */
export interface Vehicle {
  id: string;
  kind: VehicleKind;
  seats: number;
  depotId: string;
  status: VehicleStatus;
  notes?: string;
}

/** A truck with the trip it's on now, if any. */
export interface VehicleView {
  vehicle: Vehicle;
  kindLabel: string;
  trip?: FieldDispatch;
  phase?: FieldPhase;
  jobs: number;
  lastOut?: string;
  upkeep: TruckUpkeep;
}

/** What a truck or drone is doing about its upkeep; anything but Ready keeps it at the yard. */
export type UpkeepState = 'Ready' | 'Refueling' | 'Charging' | 'Servicing' | 'InShop' | 'OutOfService';
export type MaintenanceKind = 'Refuel' | 'Service' | 'Inspection' | 'BatterySwap' | 'BatteryReplaced' | 'Repair';
/** One line of a maintenance log; recordId is set for repairs and services a supervisor logged. */
export interface MaintenanceEvent {
  assetId: string; kind: MaintenanceKind; at: string; until?: string; detail: string; recordId?: string; by?: string; gallons?: number; cost?: number;
}
export interface TruckUpkeep {
  fuelPct: number; tankGallons: number; rangeMiles: number; milesDriven: number; milesToService: number;
  state: UpkeepState; busyUntil?: string; note?: string; events: MaintenanceEvent[]; costs: UpkeepCosts;
}
export interface BatteryPack { id: string; cycles: number; healthPct: number; chargePct: number; readyAt?: string; inUse: boolean }
export interface DroneUpkeep {
  packs: BatteryPack[]; readyPacks: number; nextReadyAt?: string; flightHours: number; hoursToInspection: number;
  state: UpkeepState; busyUntil?: string; note?: string; events: MaintenanceEvent[]; costs: UpkeepCosts;
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

/** An employee's record beyond their crew role. Dates are yyyy-mm-dd. */
export interface EmployeeProfile {
  employeeId?: string | null; hireDate?: string | null; phone?: string | null; email?: string | null; homeCity?: string | null;
  emergencyContact?: string | null; emergencyPhone?: string | null; certification?: string | null; certificationExpires?: string | null;
  notes?: string | null;
}

export interface PayChange { from: string; rate: number | null }

export interface CrewMember {
  name: string;
  role: string;
  /** Ground crews: issue types this technician can fix; absent means whatever the crew handles. */
  skills?: FindingType[];
  /** Ground crews: the depot this technician departs from; absent means the crew's depot. */
  depotId?: string;
  /** Their own hourly rate, set by a supervisor; absent means the standard rate for their role. Hidden from guests. */
  payRate?: number | null;
  /** Every change to their own rate, oldest first (rate null: back to standard). Hidden from guests. */
  payHistory?: PayChange[] | null;
  /** Their employee record. Hidden from guests. */
  profile?: EmployeeProfile | null;
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
  /** The truck they'd take, or the one they're already out in. */
  vehicle?: string;
  /** Who'd be in the truck with them. */
  ridesWith?: string;
  /** Every truck at their yard is out (until truckBackAt), or it has none in service. */
  noTruck?: boolean;
  truckBackAt?: string;
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
  upkeep: DroneUpkeep;
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
  vehicles: VehicleView[];
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
  /** The truck, and who else was in it. */
  vehicle?: string;
  with?: string;
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
  /** Hours worked at their rate when the shift started. */
  laborCost: number;
}

export interface TechnicianReport {
  crewId: string;
  crewName: string;
  member: string;
  role: string;
  depotName?: string;
  current: MemberShift;
  shifts: ShiftReport[];
  /** Every job they were on, by Pacific calendar day. */
  days: WorkDay[];
  now: string;
  /** Their hourly rate: their own, or the standard technician rate. */
  payRate: number;
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
    /** Flight hours at the flight crew rate. */
    crewCost: number;
  };
  flights: FlightLogEntry[];
  days: FlightDay[];
}

/** One Pacific calendar day of a flight log (flight hours are real time). */
export interface FlightDay {
  day: string; missions: number; completed: number; aborted: number; flightHours: number; distanceMeters: number;
  structuresInspected: number; findings: number; alerts: number; crewCost: number;
}

export interface DroneReport { drone: DroneAsset; report: FlightReport; upkeep: DroneUpkeep; now: string }

/** Simulated hours; personHours counts each person in the truck. A trip that went on from job to job counts once. */
export interface WorkTotals { trips: number; jobs: number; repaired: number; calledBack: number; driveHours: number; repairHours: number; personHours: number; laborCost: number }
/** One line of a breakdown: a person or a truck. */
export interface WorkBreakdown { name: string; detail?: string; jobs: number; repaired: number; hours: number }
/** Field work by a truck, a crew or out of a yard; in each job, `with` is everyone who was in the truck. */
export interface WorkReport { totals: WorkTotals; breakdown: WorkBreakdown[]; jobs: ShiftJob[]; days: WorkDay[] }
/** One Pacific calendar day of field work; a job counts on the day its truck left. */
export interface WorkDay { day: string; trips: number; jobs: number; repaired: number; calledBack: number; driveHours: number; repairHours: number; personHours: number; laborCost: number }
export interface VehicleReport { vehicle: Vehicle; kindLabel: string; depotName?: string; work: WorkReport; upkeep: TruckUpkeep; now: string }
export interface CrewWorkReport { crewId: string; crewName: string; specialty: string; depotName?: string; work: WorkReport; now: string }
export interface DepotReport { depot: Depot; trucks: number; people: number; work: WorkReport; truckUpkeep: UpkeepCosts; now: string }
/** One Pacific calendar day of the whole operation. Flight hours are real time; field hours simulated. */
export interface ActivityDay {
  day: string;
  missions: number; completed: number; aborted: number; flightHours: number; distanceMeters: number; structuresInspected: number;
  found: number; foundSevere: number; triaged: number; dismissed: number; escalated: number;
  trips: number; jobs: number; repaired: number; calledBack: number; driveHours: number; repairHours: number; personHours: number;
  refuels: number; gallons: number; fuelCost: number; batterySwaps: number; packsReplaced: number; shopVisits: number;
  maintenanceCost: number; laborCost: number; flightCost: number; totalCost: number;
}
export interface ActivityReport { days: ActivityDay[]; rates: RateCard; now: string }
/** The rates every report's costs come from. */
export interface RateCard {
  laborPerPersonHour: number; flightCrewPerHour: number; gasPerGallon: number; truckService: number; droneInspection: number;
  batteryPack: number; truckRepairDefault: number; droneRepairDefault: number;
  /** The flight crew rate's two shares. */
  pilotPerHour: number; observerPerHour: number;
}
/** Upkeep spend, by kind. */
export interface UpkeepCosts { fuel: number; service: number; batteries: number; repairs: number; total: number }
export interface FlightCrewReport { crewId: string; crewName: string; homeBase: string; report: FlightReport; now: string }
export interface PilotReport { crewId: string; crewName: string; member: string; role: string; report: FlightReport; now: string }

/** One work order on the forward schedule, most important first. Times are real (demo) time. */
export interface ScheduledJob {
  rank: number; findingId: string; structureLabel: string; circuitId: string; type: FindingType; severity: number;
  score: number; reasons: string[]; foundAt: string; repairBy?: string | null;
  assignee?: string | null; crewId?: string | null; member?: string | null; depotName?: string | null; vehicleId?: string | null;
  startsAt?: string | null; repairedAt?: string | null; backAt?: string | null; startsNow: boolean; late: boolean; blocked?: string | null;
}
export interface ScheduleView { jobs: ScheduledJob[]; untriaged: number; now: string; shiftHours: number; restHours: number }
export interface ScheduleDispatchResult {
  dispatched: { findingId: string; structureLabel: string; assignee: string }[];
  skipped: { findingId: string; structureLabel: string; reason: string }[];
  /** Stopped at its time budget with more ready to go. */
  more: boolean;
}
