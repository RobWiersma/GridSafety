namespace GridSafety.Core;

/// <summary>
/// Who costs what per hour: each person's own rate when a supervisor has set one, otherwise the standard rate for their
/// role (<see cref="Costs"/>). Field labor is charged for everyone in the truck; a flight for its pilot in command and a
/// visual observer from the pilot's crew (missions record the pilot only).
/// </summary>
public sealed class PayRates
{
    private readonly Dictionary<(string Crew, string Name), double> _technicians = [];
    private readonly Dictionary<string, double> _pilots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _observers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everyone at the standard rates.</summary>
    public static readonly PayRates Standard = new([], []);

    public PayRates(IEnumerable<Crew> groundCrews, IEnumerable<FlightCrew> flightCrews)
    {
        ArgumentNullException.ThrowIfNull(groundCrews);
        ArgumentNullException.ThrowIfNull(flightCrews);
        foreach (var c in groundCrews)
            foreach (var m in c.Members)
                if (m.PayRate is { } r) _technicians[(c.Id, m.Name.ToUpperInvariant())] = r;
        foreach (var c in flightCrews)
        {
            // The pilot's observer: the crew's first visual observer.
            var observer = c.Members.FirstOrDefault(m => m.Role == FlightCrew.ObserverRole);
            foreach (var p in c.Pilots)
            {
                _pilots.TryAdd(p.Name, p.PayRate ?? Costs.PilotPerHour);
                _observers.TryAdd(p.Name, observer?.PayRate ?? Costs.ObserverPerHour);
            }
        }
    }

    public static PayRates Of(ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new(catalog.GroundCrews, catalog.FlightCrews);
    }

    /// <summary>A technician's hourly rate.</summary>
    public double Technician(string crewId, string name) =>
        _technicians.TryGetValue((crewId, name.ToUpperInvariant()), out var r) ? r : Costs.LaborPerPersonHour;

    /// <summary>What an hour of this truck roll costs: everyone in the truck at their rate (a whole crew sent counts once).</summary>
    public double Truck(FieldDispatch d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.Riders is { Count: > 0 } riders ? riders.Sum(r => Technician(r.CrewId, r.Name))
            : d.Member is { } m ? Technician(d.CrewId, m)
            : Costs.LaborPerPersonHour;
    }

    /// <summary>What an hour of flight under this pilot costs: the pilot plus an observer.</summary>
    public double Flight(string pilot) =>
        _pilots.GetValueOrDefault(pilot, Costs.PilotPerHour) + _observers.GetValueOrDefault(pilot, Costs.ObserverPerHour);
}
