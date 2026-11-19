namespace GridSafety.Core;

public enum VehicleKind { BucketTruck, LineTruck, Pickup }

public enum VehicleStatus { InService, OutOfService }

/// <summary>
/// A truck ground crews drive out to jobs, kept at a depot. <see cref="Seats"/> is how many people ride in it: a
/// technician sent from the yard takes a free truck and, when there's room, a crewmate from the same yard rides along.
/// </summary>
public sealed record Vehicle(string Id, VehicleKind Kind, int Seats, string DepotId, VehicleStatus Status = VehicleStatus.InService, string? Notes = null)
{
    public string Label => $"{Id} · {KindLabel(Kind)}";

    public static string KindLabel(VehicleKind k) => k switch
    {
        VehicleKind.BucketTruck => "Bucket truck",
        VehicleKind.LineTruck => "Line truck",
        _ => "Pickup",
    };

    public static int DefaultSeats(VehicleKind k) => k == VehicleKind.LineTruck ? 3 : 2;
}

/// <summary>Which trucks are free, and the demo's starting fleet.</summary>
public static class Fleet
{
    /// <summary>
    /// The demo fleet: a bucket truck and a line truck at every yard, and a pickup at the busier ones. Ids are
    /// T-(yard number)(truck number), e.g. T-071 is the Richmond yard's first truck.
    /// </summary>
    public static IReadOnlyList<Vehicle> Default(IReadOnlyList<Depot> depots) =>
    [
        .. depots.SelectMany((d, i) => new[]
        {
            new Vehicle($"T-{i + 1:00}1", VehicleKind.BucketTruck, 2, d.Id),
            new Vehicle($"T-{i + 1:00}2", VehicleKind.LineTruck, 3, d.Id),
        }.Concat(Busy.Contains(d.Id) ? [new Vehicle($"T-{i + 1:00}3", VehicleKind.Pickup, 2, d.Id)] : [])),
    ];

    private static readonly string[] Busy = ["concord", "walnut-creek", "antioch", "san-ramon", "richmond", "fremont", "vacaville"];

    /// <summary>Whether this truck is out on a trip (driving, at a job, driving back, or with jobs lined up).</summary>
    public static bool IsOut(Vehicle v, IReadOnlyList<FieldDispatch> active) => active.Any(d => d.VehicleId == v.Id);

    /// <summary>
    /// A free, in-service truck at <paramref name="depotId"/> with at least <paramref name="seats"/> seats, smallest that
    /// fits first (big trucks stay free for bigger crews); null when there's none.
    /// </summary>
    public static Vehicle? FreeAt(IReadOnlyList<Vehicle> vehicles, string depotId, IReadOnlyList<FieldDispatch> active, int seats = 1) =>
        vehicles.Where(v => v.DepotId == depotId && v.Status == VehicleStatus.InService && v.Seats >= seats && !IsOut(v, active))
            .OrderBy(v => v.Seats).ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The roomiest free, in-service truck at the yard (for sending a whole crew); null when there's none.</summary>
    public static Vehicle? RoomiestAt(IReadOnlyList<Vehicle> vehicles, string depotId, IReadOnlyList<FieldDispatch> active) =>
        vehicles.Where(v => v.DepotId == depotId && v.Status == VehicleStatus.InService && !IsOut(v, active))
            .OrderByDescending(v => v.Seats).ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>When the yard's next truck is back, or null when it has no trucks in service at all.</summary>
    public static DateTimeOffset? NextBackAt(IReadOnlyList<Vehicle> vehicles, string depotId, IReadOnlyList<FieldDispatch> active) =>
        vehicles.Where(v => v.DepotId == depotId && v.Status == VehicleStatus.InService)
            .Select(v => active.Where(d => d.VehicleId == v.Id).Max(d => (DateTimeOffset?)d.ReturnsAt))
            .Where(t => t is not null).Min();

    /// <summary>The trip a truck is on now: the job it's driving to, working at or driving back from (not ones lined up).</summary>
    public static FieldDispatch? CurrentTrip(Vehicle v, IReadOnlyList<FieldDispatch> active, DateTimeOffset now) =>
        active.Where(d => d.VehicleId == v.Id && d.PhaseAt(now) != FieldPhase.Queued).MinBy(d => d.DepartedAt)
        ?? active.Where(d => d.VehicleId == v.Id).MinBy(d => d.DepartedAt);
}
