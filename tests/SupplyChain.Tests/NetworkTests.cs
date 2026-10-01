using SupplyChain.Api;

namespace SupplyChain.Tests;

public class NetworkTests
{
    private static readonly Network Net = Network.LoadEmbedded();

    [Fact]
    public void Hubs_HaveUniqueIdsAndValidCoordinates()
    {
        Assert.Equal(Net.Hubs.Count, Net.Hubs.Select(h => h.Id).Distinct().Count());
        Assert.All(Net.Hubs, h =>
        {
            Assert.InRange(h.Lat, -90, 90);
            Assert.InRange(h.Lng, -180, 180);
            Assert.True(h.DwellHours >= 0 && h.HandlingUsdPerTonne >= 0);
        });
    }

    [Fact]
    public void Network_IsFullyConnected()
    {
        var seen = new HashSet<int> { 0 };
        var queue = new Queue<int>([0]);
        while (queue.TryDequeue(out var u))
            foreach (var (to, _) in Net.Neighbours(u))
                if (seen.Add(to)) queue.Enqueue(to);

        var unreachable = Net.Hubs.Where((_, i) => !seen.Contains(i)).Select(h => h.Id);
        Assert.Empty(unreachable);
    }

    [Fact]
    public void Lanes_HaveSensibleCostsAndSpeeds()
    {
        Assert.All(Net.Lanes, l =>
        {
            Assert.True(l.Km > 0 && l.Hours > 0 && l.UsdPerTonne > 0 && l.KgCo2PerTonne > 0);
            var p = Network.Modes[l.Mode];
            Assert.Equal(l.Km / p.SpeedKmh, l.Hours, 6);
        });
    }

    [Fact]
    public void Lanes_AreLongerThanTheStraightLine()
    {
        Assert.True(Net.TryGet("CNSHA", out var shanghai));
        Assert.True(Net.TryGet("NLRTM", out var rotterdam));
        var greatCircle = Network.Haversine(shanghai.Lat, shanghai.Lng, rotterdam.Lat, rotterdam.Lng);
        Assert.InRange(greatCircle, 8_500, 9_500);
        Assert.All(Net.Lanes, l =>
        {
            Net.TryGet(l.A, out var a);
            Net.TryGet(l.B, out var b);
            Assert.True(l.Km >= Network.Haversine(a.Lat, a.Lng, b.Lat, b.Lng));
        });
    }

    [Fact]
    public void AirLanes_OnlyJoinAirports()
    {
        var air = Net.Lanes.Where(l => l.Mode == Mode.Air).ToList();
        Assert.NotEmpty(air);
        Assert.All(air, l =>
        {
            Net.TryGet(l.A, out var a);
            Net.TryGet(l.B, out var b);
            Assert.Equal("air", a.Kind);
            Assert.Equal("air", b.Kind);
        });
    }

    [Fact]
    public void Hubs_InTheSameMetro_AreLinkedByRoad()
    {
        Assert.Contains(Net.Lanes, l => l.Mode == Mode.Road && new[] { l.A, l.B }.Order().SequenceEqual(["CNSHA", "PVG"]));
        Assert.Contains(Net.Lanes, l => l.Mode == Mode.Road && new[] { l.A, l.B }.Order().SequenceEqual(["SGSIN", "SIN"]));
    }

    [Fact]
    public void TryIndex_IsCaseInsensitive()
    {
        Assert.True(Net.TryIndex("cnsha", out var lower));
        Assert.True(Net.TryIndex("CNSHA", out var upper));
        Assert.Equal(upper, lower);
        Assert.False(Net.TryIndex("NOPE", out _));
    }

    [Theory]
    [InlineData(0.95, 1.645)]
    [InlineData(0.99, 2.326)]
    [InlineData(0.90, 1.282)]
    public void ServiceLevel_MapsToTheNormalQuantile(double service, double z)
    {
        Assert.InRange(new Cargo(1, 1, 1, 0.2, service).Z, z - 0.001, z + 0.001);
    }
}
