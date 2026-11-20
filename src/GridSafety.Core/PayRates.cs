namespace GridSafety.Core;

/// <summary>A change to someone's pay: from this moment on, this own hourly rate (null: back to the standard rate).</summary>
public sealed record PayChange(DateTimeOffset From, double? Rate);

/// <summary>
/// Who costs what per hour, and when: each person's own rate in effect at the time of the work when a supervisor has set
/// one, otherwise the standard rate for their role (<see cref="Costs"/>). A raise doesn't reprice work already done.
/// Field labor is charged for everyone in the truck; a flight for its pilot in command and a visual observer from the
/// pilot's crew (missions record the pilot only).
/// </summary>
public sealed class PayRates
{
    private readonly Dictionary<(string Crew, string Name), CrewMember> _technicians = [];
    private readonly Dictionary<string, CrewMember> _pilots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CrewMember?> _observers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everyone at the standard rates.</summary>
    public static readonly PayRates Standard = new([], []);

    public PayRates(IEnumerable<Crew> groundCrews, IEnumerable<FlightCrew> flightCrews)
    {
        ArgumentNullException.ThrowIfNull(groundCrews);
        ArgumentNullException.ThrowIfNull(flightCrews);
        foreach (var c in groundCrews)
            foreach (var m in c.Members)
                _technicians[(c.Id, m.Name.ToUpperInvariant())] = m;
        foreach (var c in flightCrews)
        {
            // The pilot's observer: the crew's first visual observer.
            var observer = c.Members.FirstOrDefault(m => m.Role == FlightCrew.ObserverRole);
            foreach (var p in c.Pilots)
            {
                _pilots.TryAdd(p.Name, p);
                _observers.TryAdd(p.Name, observer);
            }
        }
    }

    public static PayRates Of(ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new(catalog.GroundCrews, catalog.FlightCrews);
    }

    /// <summary>
    /// Someone's own rate at a moment (null: the standard rate). With no history on record, their current rate is taken
    /// to have always applied; before their first recorded change, they were on the standard rate.
    /// </summary>
    public static double? OwnRateAt(CrewMember member, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.PayHistory is not { Count: > 0 } history) return member.PayRate;
        PayChange? inEffect = null;
        foreach (var c in history)
            if (c.From <= at && (inEffect is null || c.From >= inEffect.From)) inEffect = c;
        return inEffect?.Rate;
    }

    /// <summary>
    /// The member with their own rate set to <paramref name="rate"/> from <paramref name="now"/> on, recorded in their pay
    /// history so earlier work keeps its rate. Unchanged when the rate is the same.
    /// </summary>
    public static CrewMember Change(CrewMember member, double? rate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.PayRate == rate) return member;
        // A rate set before history was kept applied from the start.
        List<PayChange> history = member.PayHistory is { Count: > 0 } h ? [.. h]
            : member.PayRate is { } old ? [new PayChange(DateTimeOffset.MinValue, old)] : [];
        history.RemoveAll(c => c.From >= now); // a correction made at the same moment replaces it
        history.Add(new PayChange(now, rate));
        return member with { PayRate = rate, PayHistory = history };
    }

    /// <summary>A technician's hourly rate at a moment (by default: now).</summary>
    public double Technician(string crewId, string name, DateTimeOffset? at = null) =>
        _technicians.TryGetValue((crewId, name.ToUpperInvariant()), out var m)
            ? OwnRateAt(m, at ?? DateTimeOffset.MaxValue) ?? Costs.LaborPerPersonHour
            : Costs.LaborPerPersonHour;

    /// <summary>
    /// What an hour of this truck roll costs: everyone in the truck at their rate when it left (a whole crew sent counts once).
    /// </summary>
    public double Truck(FieldDispatch d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.Riders is { Count: > 0 } riders ? riders.Sum(r => Technician(r.CrewId, r.Name, d.DepartedAt))
            : d.Member is { } m ? Technician(d.CrewId, m, d.DepartedAt)
            : Costs.LaborPerPersonHour;
    }

    /// <summary>What an hour of flight under this pilot costs at a moment (by default: now): the pilot plus an observer.</summary>
    public double Flight(string pilot, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.MaxValue;
        var own = _pilots.TryGetValue(pilot, out var p) ? OwnRateAt(p, when) : null;
        var observer = _observers.TryGetValue(pilot, out var o) && o is not null ? OwnRateAt(o, when) : null;
        return (own ?? Costs.PilotPerHour) + (observer ?? Costs.ObserverPerHour);
    }

    /// <summary>What an hour of this mission costs, at the rates in effect when it flew.</summary>
    public double Flight(Mission m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return Flight(m.Pilot, m.StartedAt ?? m.CreatedAt);
    }
}
