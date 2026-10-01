using SupplyChain.Api;

namespace SupplyChain.Tests;

public class PlannerTests
{
    private static readonly Network Net = Network.LoadEmbedded();
    private static readonly Planner Planner = new(Net);
    private static readonly IReadOnlySet<Mode> AllModes = new HashSet<Mode>(Enum.GetValues<Mode>());

    private static int Id(string hub) => Net.TryIndex(hub, out var i) ? i : throw new ArgumentException(hub);

    private static Plan? Run(string from, string to, double valuePerTonne = 40_000, string[]? closed = null, IReadOnlySet<Mode>? modes = null,
        double tonnes = 20, double demand = 5, double carry = 0.2, double service = 0.95) =>
        Planner.Find(new PlanRequest(Id(from), Id(to), new Cargo(tonnes, valuePerTonne, demand, carry, service),
            (closed ?? []).Select(Id).ToHashSet(), modes ?? AllModes));

    private static PlannedRoute Labelled(Plan plan, string label) => plan.Routes.First(r => r.Labels.Contains(label));

    [Fact]
    public void Fastest_IsByAir_AndCheapest_IsBySea()
    {
        var plan = Run("CNSHA", "NLRTM")!;
        Assert.Contains("air", Labelled(plan, "fastest").Modes);
        Assert.Contains("SUEZ", Labelled(plan, "cheapest").Hubs);
        Assert.True(Labelled(plan, "fastest").Totals.Days < 3);
        Assert.True(Labelled(plan, "cheapest").Totals.Days > 25);
    }

    [Fact]
    public void ModeChoice_FollowsValueDensity()
    {
        // Low-value freight is not worth flying; very high-value freight pays for speed in inventory savings.
        var bulk = Run("CNSHA", "USCHI", valuePerTonne: 800)!;
        Assert.DoesNotContain("air", Labelled(bulk, "lean").Modes);

        var precious = Run("CNSHA", "USCHI", valuePerTonne: 1_000_000)!;
        Assert.Contains("air", Labelled(precious, "lean").Modes);
    }

    [Fact]
    public void EachLabel_IsTheBestRouteOnItsMetric()
    {
        var plan = Run("CNSHA", "USNYC")!;
        Assert.Equal(plan.Routes.Min(r => r.Totals.LandedUsd), Labelled(plan, "lean").Totals.LandedUsd);
        Assert.Equal(plan.Routes.Min(r => r.Totals.Days), Labelled(plan, "fastest").Totals.Days);
        Assert.Equal(plan.Routes.Min(r => r.Totals.FreightUsd), Labelled(plan, "cheapest").Totals.FreightUsd);
        Assert.Equal(plan.Routes.Min(r => r.Totals.KgCo2), Labelled(plan, "greenest").Totals.KgCo2);
        Assert.Equal(Labelled(plan, "lean").Id, plan.Recommended);
    }

    [Fact]
    public void Routes_AreDistinctAndSortedByLandedCost()
    {
        var plan = Run("SGSIN", "GBFXT")!;
        Assert.InRange(plan.Routes.Length, 2, 5);
        Assert.Equal(plan.Routes.Length, plan.Routes.Select(r => string.Join('>', r.Legs.Select(l => $"{l.From}:{l.Mode}"))).Distinct().Count());
        Assert.Equal(plan.Routes.OrderBy(r => r.Totals.LandedUsd).Select(r => r.Id), plan.Routes.Select(r => r.Id));
    }

    [Fact]
    public void Totals_AreConsistentWithTheLegs()
    {
        var plan = Run("CNSHA", "NLRTM")!;
        Assert.All(plan.Routes, r =>
        {
            var t = r.Totals;
            Assert.Equal(r.Legs.Sum(l => l.Km), t.Km, 3);
            Assert.Equal(r.Legs.Sum(l => l.Usd), t.FreightUsd, 3);
            Assert.Equal(r.Legs.Sum(l => l.KgCo2), t.KgCo2, 3);
            Assert.Equal(r.Legs.Sum(l => l.Hours + l.WaitHours) / 24, t.Days, 6);
            Assert.Equal(t.FreightUsd + t.PipelineUsd + t.SafetyStockUsd, t.LandedUsd, 3);
            Assert.Equal(t.Days, t.MovingDays + t.WaitingDays, 6);
            Assert.InRange(t.WaitingShare, 0, 1);
            Assert.True(t.DaysAtServiceLevel > t.Days);
            Assert.Equal("CNSHA", r.Hubs[0]);
            Assert.Equal("NLRTM", r.Hubs[^1]);
            Assert.Equal(r.Legs.Length + 1, r.Hubs.Length);
        });
    }

    [Fact]
    public void FirstLeg_HasNoWait_AndLaterLegsWaitAtTheirHub()
    {
        var route = Labelled(Run("CNSHA", "NLRTM")!, "cheapest");
        Assert.Equal(0, route.Legs[0].WaitHours);
        Assert.All(route.Legs.Skip(1), l => Assert.True(Net.TryGet(l.From, out var h) && l.WaitHours == h.DwellHours));
    }

    [Fact]
    public void InventoryCost_RisesWithCargoValue()
    {
        var cheap = Labelled(Run("CNSHA", "NLRTM", valuePerTonne: 1_000)!, "cheapest").Totals;
        var dear = Labelled(Run("CNSHA", "NLRTM", valuePerTonne: 100_000)!, "cheapest").Totals;
        Assert.Equal(cheap.FreightUsd, dear.FreightUsd, 3); // same route, same freight
        Assert.Equal(100, dear.PipelineUsd / cheap.PipelineUsd, 3);
        Assert.True(dear.SafetyStockUsd > cheap.SafetyStockUsd);
    }

    [Fact]
    public void HigherServiceLevel_NeedsMoreSafetyStock()
    {
        var lax = Labelled(Run("CNSHA", "NLRTM", service: 0.90)!, "cheapest").Totals;
        var strict = Labelled(Run("CNSHA", "NLRTM", service: 0.99)!, "cheapest").Totals;
        Assert.True(strict.SafetyStockTonnes > lax.SafetyStockTonnes);
    }

    [Fact]
    public void ClosingSuez_RemovesItFromEveryRoute_AndReportsTheImpact()
    {
        var plan = Run("SGSIN", "GBFXT", closed: ["BAB"])!;
        Assert.All(plan.Routes, r => Assert.DoesNotContain("BAB", r.Hubs));
        Assert.NotNull(plan.Baseline);
        Assert.True(plan.Baseline!.Impact.Rerouted);
        Assert.True(plan.Baseline.Impact.ExtraLandedUsd > 0);
        Assert.Contains("CAPE", Labelled(plan, "lean").Hubs); // the long way round
    }

    [Fact]
    public void Closure_ThatDoesNotAffectTheBestRoute_HasNoImpact()
    {
        var plan = Run("CNSHA", "USCHI", closed: ["SUEZ"])!;
        Assert.NotNull(plan.Baseline);
        Assert.False(plan.Baseline!.Impact.Rerouted);
        Assert.Equal(0, plan.Baseline.Impact.ExtraLandedUsd, 6);
    }

    [Fact]
    public void NoClosures_MeansNoBaseline() => Assert.Null(Run("CNSHA", "NLRTM")!.Baseline);

    [Fact]
    public void ModeFilter_IsRespected()
    {
        var plan = Run("CNSHA", "NLRTM", modes: new HashSet<Mode> { Mode.Sea })!;
        Assert.All(plan.Routes, r => Assert.All(r.Legs, l => Assert.Equal("sea", l.Mode)));
    }

    [Fact]
    public void Impossible_ReturnsNull()
    {
        Assert.Null(Run("CNSHA", "NLRTM", modes: new HashSet<Mode> { Mode.Road }));
        Assert.Null(Run("AUSYD", "NLRTM", closed: ["SGSIN", "CNSHA", "USLAX", "SYD"], modes: new HashSet<Mode> { Mode.Sea }));
    }

    [Fact]
    public void ChokepointsOnTheRoute_AreListed()
    {
        var route = Labelled(Run("CNSHA", "NLRTM")!, "cheapest");
        Assert.Contains("SUEZ", route.Chokepoints);
        Assert.DoesNotContain("CNSHA", route.Chokepoints);
    }
}
