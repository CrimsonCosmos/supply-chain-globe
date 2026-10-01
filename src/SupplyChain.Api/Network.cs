using System.Reflection;
using System.Text.Json;

namespace SupplyChain.Api;

public enum Mode { Sea, Air, Rail, Road }

/// <summary>Physical and economic parameters of a transport mode. Illustrative planning values, not quotes.</summary>
public record ModeProfile(Mode Mode, double SpeedKmh, double UsdPerTonneKm, double KgCo2PerTonneKm, double Circuity, double LeadTimeCv);

public record Hub(string Id, string Name, string Country, string Kind, double Lat, double Lng, string? Metro, double DwellHours, double HandlingUsdPerTonne, double DwellCv)
{
    public bool IsChokepoint => Kind == "chokepoint";
}

/// <summary>An undirected link between two hubs. Cost and emissions are per tonne.</summary>
public record Lane(string A, string B, Mode Mode, double Km, double Hours, double UsdPerTonne, double KgCo2PerTonne)
{
    public string Other(string hub) => hub == A ? B : A;
}

/// <summary>
/// The logistics network: hubs (ports, airports, rail terminals, distribution centres, sea chokepoints)
/// and the lanes between them. Curated lanes come from Data/network.json; air lanes between airports
/// and short road "drayage" lanes inside a metro area (port to airport) are generated.
/// </summary>
public sealed class Network
{
    public static readonly IReadOnlyDictionary<Mode, ModeProfile> Modes = new[]
    {
        new ModeProfile(Mode.Sea, SpeedKmh: 30, UsdPerTonneKm: 0.010, KgCo2PerTonneKm: 0.016, Circuity: 1.25, LeadTimeCv: 0.20),
        new ModeProfile(Mode.Air, SpeedKmh: 750, UsdPerTonneKm: 0.550, KgCo2PerTonneKm: 0.600, Circuity: 1.05, LeadTimeCv: 0.06),
        new ModeProfile(Mode.Rail, SpeedKmh: 45, UsdPerTonneKm: 0.035, KgCo2PerTonneKm: 0.025, Circuity: 1.25, LeadTimeCv: 0.18),
        new ModeProfile(Mode.Road, SpeedKmh: 55, UsdPerTonneKm: 0.090, KgCo2PerTonneKm: 0.095, Circuity: 1.30, LeadTimeCv: 0.10),
    }.ToDictionary(m => m.Mode);

    // Dwell (hours waiting for the next leg), handling (USD per tonne) and dwell variability (coefficient
    // of variation) by kind of hub. Any hub can override them in network.json.
    private static readonly Dictionary<string, (double Dwell, double Handling, double Cv)> KindDefaults = new()
    {
        ["port"] = (48, 28, 0.6),
        ["air"] = (10, 45, 0.3),
        ["rail"] = (24, 14, 0.3),
        ["dc"] = (8, 9, 0.3),
        ["chokepoint"] = (0, 0, 0.8),
    };

    private const double MaxAirKm = 13_500;

    public IReadOnlyList<Hub> Hubs { get; }
    public IReadOnlyList<Lane> Lanes { get; }
    private readonly Dictionary<string, int> _index;
    private readonly List<(int To, Lane Lane)>[] _adjacency;

    private Network(List<Hub> hubs, List<Lane> lanes)
    {
        Hubs = hubs;
        Lanes = lanes;
        _index = hubs.Select((h, i) => (h.Id, i)).ToDictionary(t => t.Id, t => t.i);
        _adjacency = hubs.Select(_ => new List<(int, Lane)>()).ToArray();
        foreach (var lane in lanes)
        {
            int a = _index[lane.A], b = _index[lane.B];
            _adjacency[a].Add((b, lane));
            _adjacency[b].Add((a, lane));
        }
    }

    public int Count => Hubs.Count;
    public bool TryIndex(string id, out int index) => _index.TryGetValue(id.ToUpperInvariant(), out index);
    public bool TryGet(string id, out Hub hub)
    {
        if (TryIndex(id, out var i)) { hub = Hubs[i]; return true; }
        hub = null!;
        return false;
    }
    public IReadOnlyList<(int To, Lane Lane)> Neighbours(int hub) => _adjacency[hub];

    public static Network LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("network.json")
            ?? throw new InvalidOperationException("network.json is not embedded.");
        var file = JsonSerializer.Deserialize<NetworkFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("network.json is empty.");
        return Build(file);
    }

    private static Network Build(NetworkFile file)
    {
        var hubs = file.Hubs.Select(h =>
        {
            var (dwell, handling, cv) = KindDefaults.GetValueOrDefault(h.Kind, (0, 0, 0.3));
            return new Hub(h.Id, h.Name, h.Country, h.Kind, h.Lat, h.Lng, h.Metro, h.Dwell ?? dwell, h.Handling ?? handling, h.DwellCv ?? cv);
        }).ToList();

        var byId = hubs.ToDictionary(h => h.Id);
        if (byId.Count != hubs.Count) throw new InvalidDataException("Duplicate hub id in network.json.");

        var lanes = new List<Lane>();
        var seen = new HashSet<(string, string, Mode)>();

        void Add(Hub a, Hub b, Mode mode)
        {
            var key = string.CompareOrdinal(a.Id, b.Id) < 0 ? (a.Id, b.Id, mode) : (b.Id, a.Id, mode);
            if (a.Id == b.Id || !seen.Add(key)) return;
            var p = Modes[mode];
            var km = Haversine(a.Lat, a.Lng, b.Lat, b.Lng) * p.Circuity;
            lanes.Add(new Lane(a.Id, b.Id, mode, km, km / p.SpeedKmh, km * p.UsdPerTonneKm, km * p.KgCo2PerTonneKm));
        }

        foreach (var (modeName, pairs) in file.Lanes)
        {
            var mode = Enum.Parse<Mode>(modeName, ignoreCase: true);
            foreach (var pair in pairs)
            {
                var ends = pair.Split('-');
                if (ends.Length != 2 || !byId.TryGetValue(ends[0], out var a) || !byId.TryGetValue(ends[1], out var b))
                    throw new InvalidDataException($"Lane \"{pair}\" ({modeName}) references an unknown hub.");
                Add(a, b, mode);
            }
        }

        // Every pair of airports is a direct flight if it is within range.
        var airports = hubs.Where(h => h.Kind == "air").ToList();
        for (int i = 0; i < airports.Count; i++)
            for (int j = i + 1; j < airports.Count; j++)
                if (Haversine(airports[i].Lat, airports[i].Lng, airports[j].Lat, airports[j].Lng) <= MaxAirKm)
                    Add(airports[i], airports[j], Mode.Air);

        // Hubs in the same metro area (a port and its airport) are linked by short road drayage.
        foreach (var group in hubs.Where(h => h.Metro is not null).GroupBy(h => h.Metro))
        {
            var members = group.ToList();
            for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                    Add(members[i], members[j], Mode.Road);
        }

        return new Network(hubs, lanes);
    }

    /// <summary>Great-circle distance in kilometres.</summary>
    public static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0088, rad = Math.PI / 180;
        double dLat = (lat2 - lat1) * rad, dLng = (lng2 - lng1) * rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private sealed class NetworkFile
    {
        public List<HubFile> Hubs { get; set; } = [];
        public Dictionary<string, string[]> Lanes { get; set; } = [];
    }

    private sealed class HubFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Country { get; set; } = "";
        public string Kind { get; set; } = "";
        public double Lat { get; set; }
        public double Lng { get; set; }
        public string? Metro { get; set; }
        public double? Dwell { get; set; }
        public double? Handling { get; set; }
        public double? DwellCv { get; set; }
    }
}
