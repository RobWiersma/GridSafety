using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class YardStaffingTests
{
    private static int People(ResourceCatalog c, string depotId) =>
        c.GroundCrews.Sum(crew => crew.Members.Count(m => crew.DepotOf(m) == depotId));

    private static int Seats(ResourceCatalog c, string depotId) =>
        c.Vehicles.Where(v => v.DepotId == depotId).Sum(v => v.Seats);

    [Fact]
    public void Every_yard_has_at_least_as_many_people_as_its_trucks_have_seats()
    {
        var catalog = ResourceCatalog.Default();
        Assert.All(catalog.Depots, d => Assert.True(People(catalog, d.Id) >= Seats(catalog, d.Id),
            $"{d.Id}: {People(catalog, d.Id)} people for {Seats(catalog, d.Id)} seats"));

        var names = catalog.GroundCrews.SelectMany(c => c.Members).Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.GroundCrews.Where(c => c.Id.StartsWith("yard-")), c => Assert.Equal("Foreman", c.Members[0].Role));
    }

    [Fact]
    public void A_stored_catalog_is_staffed_once_and_keeps_its_own_people()
    {
        var catalog = ResourceCatalog.Default();
        catalog.GroundCrews.RemoveAll(c => c.Id.StartsWith("yard-"));
        catalog.SeedVersion = 3;

        catalog.Normalize();
        Assert.Equal(ResourceCatalog.CurrentSeedVersion, catalog.SeedVersion);
        Assert.All(catalog.Depots, d => Assert.True(People(catalog, d.Id) >= Seats(catalog, d.Id), d.Id));

        var count = catalog.GroundCrews.Count;
        catalog.Normalize();
        Assert.Equal(count, catalog.GroundCrews.Count);
        Assert.Equal(ResourceCatalog.Default().GroundCrews.Select(c => c.Id), catalog.GroundCrews.Select(c => c.Id));
    }
}
