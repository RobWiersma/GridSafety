namespace GridSafety.Core;

/// <summary>
/// The rate card every report's costs come from: illustrative US utility figures for the demo, kept in one place so a
/// real deployment would swap in its own. Labor is per simulated person-hour (field work runs at demo speed); flight crew
/// time is per real flight hour (drones fly in real time).
/// </summary>
public static class Costs
{
    /// <summary>A line technician, fully loaded (wages, benefits, overhead), per hour on a job or driving to it.</summary>
    public const double LaborPerPersonHour = 95;
    /// <summary>A remote pilot in command, per flight hour.</summary>
    public const double PilotPerHour = 85;
    /// <summary>The visual observer flying with them, per flight hour.</summary>
    public const double ObserverPerHour = 55;
    /// <summary>A remote pilot in command plus a visual observer, per flight hour.</summary>
    public const double FlightCrewPerHour = PilotPerHour + ObserverPerHour;
    /// <summary>The range a supervisor can set someone's own hourly rate in.</summary>
    public const double MinPayRate = 20, MaxPayRate = 400;
    public const double GasPerGallon = 4.90;
    public const double TruckService = 650, DroneInspection = 120, BatteryPack = 450;
    /// <summary>Used when a supervisor logs a repair or service without a cost.</summary>
    public const double TruckRepairDefault = 1200, DroneRepairDefault = 600, TruckServiceLogged = 650, DroneServiceLogged = 150;

    public static RateCard Card { get; } = new(LaborPerPersonHour, FlightCrewPerHour, GasPerGallon, TruckService, DroneInspection, BatteryPack,
        TruckRepairDefault, DroneRepairDefault, PilotPerHour, ObserverPerHour);

    public static double Money(double dollars) => Math.Round(dollars, 2);
}

/// <summary>The rates in use, for reports to show where their numbers come from.</summary>
public sealed record RateCard(double LaborPerPersonHour, double FlightCrewPerHour, double GasPerGallon, double TruckService, double DroneInspection,
    double BatteryPack, double TruckRepairDefault, double DroneRepairDefault, double PilotPerHour, double ObserverPerHour);
