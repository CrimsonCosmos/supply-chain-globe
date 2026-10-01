using System.Globalization;
using System.Text.RegularExpressions;
using SupplyChain.Api;

var builder = WebApplication.CreateBuilder(args);
var network = Network.LoadEmbedded();
builder.Services.AddSingleton(network);
builder.Services.AddSingleton(new Planner(network));
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
var app = builder.Build();

app.UseResponseCompression();

// Serves the React UI (built into wwwroot) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

var idPattern = new Regex("^[A-Za-z0-9]{3,6}$", RegexOptions.Compiled);

// Typical cargo, so the UI and API users have sensible starting points. Values are USD per tonne.
var presets = new[]
{
    new { id = "electronics", name = "Consumer electronics", valuePerTonne = 40_000, demandPerDay = 5.0 },
    new { id = "pharma", name = "Pharmaceuticals", valuePerTonne = 250_000, demandPerDay = 2.0 },
    new { id = "apparel", name = "Apparel", valuePerTonne = 12_000, demandPerDay = 8.0 },
    new { id = "auto", name = "Auto parts", valuePerTonne = 9_000, demandPerDay = 10.0 },
    new { id = "machinery", name = "Industrial machinery", valuePerTonne = 6_000, demandPerDay = 4.0 },
    new { id = "bulk", name = "Bulk goods", valuePerTonne = 800, demandPerDay = 40.0 },
};

// --- Request parsing ---------------------------------------------------------------------------

bool TryHub(Network net, string raw, string role, out int index, out IResult error)
{
    index = -1;
    if (!idPattern.IsMatch(raw))
    {
        error = Results.BadRequest(new { error = $"\"{raw}\" is not a hub code (3 to 6 letters or digits)." });
        return false;
    }
    if (!net.TryIndex(raw, out index))
    {
        error = Results.NotFound(new { error = $"Unknown {role} hub \"{raw.ToUpperInvariant()}\"." });
        return false;
    }
    error = null!;
    return true;
}

bool TryNumber(IQueryCollection q, string name, double fallback, double min, double max, out double value, out IResult error)
{
    value = fallback;
    error = null!;
    if (!q.TryGetValue(name, out var raw) || raw.Count == 0 || raw[0] is "") return true;
    if (!double.TryParse(raw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < min || value > max)
    {
        error = Results.BadRequest(new { error = $"\"{name}\" must be a number between {min} and {max}." });
        return false;
    }
    return true;
}

IResult Plan(Network net, Planner planner, string from, string to, IQueryCollection q)
{
    if (!TryHub(net, from, "origin", out var origin, out var error)) return error;
    if (!TryHub(net, to, "destination", out var destination, out error)) return error;
    if (origin == destination) return Results.BadRequest(new { error = "Origin and destination are the same hub." });

    if (!TryNumber(q, "tonnes", 20, 0.1, 100_000, out var tonnes, out error)) return error;
    if (!TryNumber(q, "value", 40_000, 0, 5_000_000, out var value, out error)) return error;
    if (!TryNumber(q, "demand", 5, 0.01, 100_000, out var demand, out error)) return error;
    if (!TryNumber(q, "carry", 0.20, 0, 1, out var carry, out error)) return error;
    if (!TryNumber(q, "service", 0.95, 0.5, 0.999, out var service, out error)) return error;

    var closed = new HashSet<int>();
    foreach (var id in (q["avoid"].FirstOrDefault() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!net.TryIndex(id, out var hub))
            return Results.NotFound(new { error = $"Unknown hub \"{id.ToUpperInvariant()}\" in avoid." });
        closed.Add(hub);
    }
    if (closed.Contains(origin) || closed.Contains(destination))
        return Results.BadRequest(new { error = "The origin or destination is in the avoid list." });

    var modes = new HashSet<Mode>();
    var modeList = q["modes"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(modeList)) modes.UnionWith(Enum.GetValues<Mode>());
    else
        foreach (var m in modeList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<Mode>(m, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
                return Results.BadRequest(new { error = $"Unknown mode \"{m}\". Use sea, air, rail or road." });
            modes.Add(mode);
        }

    var cargo = new Cargo(tonnes, value, demand, carry, service);
    var plan = planner.Find(new PlanRequest(origin, destination, cargo, closed, modes));
    if (plan is null)
    {
        var a = net.Hubs[origin].Name;
        var b = net.Hubs[destination].Name;
        return Results.NotFound(new
        {
            error = closed.Count > 0 || modes.Count < 4
                ? $"No route from {a} to {b} with those closures and modes."
                : $"No route from {a} to {b}.",
            origin = net.Hubs[origin].Id,
            destination = net.Hubs[destination].Id,
        });
    }

    return Results.Ok(new
    {
        origin = net.Hubs[origin].Id,
        destination = net.Hubs[destination].Id,
        cargo = new { tonnes, valuePerTonne = value, demandPerDay = demand, carryRate = carry, serviceLevel = service },
        avoid = closed.Select(i => net.Hubs[i].Id).Order().ToArray(),
        modes = modes.Select(m => m.ToString().ToLowerInvariant()).Order().ToArray(),
        recommended = plan.Recommended,
        routes = plan.Routes,
        baseline = plan.Baseline,
    });
}

// --- Endpoints ---------------------------------------------------------------------------------

app.MapGet("/api", (Network net) => Results.Ok(new
{
    usage = new[]
    {
        "GET /api/route/{from}/{to}   plan routes between two hubs, e.g. /api/route/CNSHA/NLRTM",
        "    ?tonnes=20&value=40000&demand=5&carry=0.2&service=0.95   cargo and inventory assumptions",
        "    &avoid=SUEZ,PANAMA                                      close hubs or chokepoints",
        "    &modes=sea,rail                                         restrict modes (sea, air, rail, road)",
        "GET /api/network             hubs, lanes, mode profiles and cargo presets",
    },
    hubs = net.Hubs.Count,
    lanes = net.Lanes.Count,
}));

app.MapGet("/api/network", (Network net, HttpContext http) =>
{
    http.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Ok(new
    {
        hubs = net.Hubs.Select(h => new { h.Id, h.Name, h.Country, h.Kind, h.Lat, h.Lng, h.Metro, dwellHours = h.DwellHours, handlingUsdPerTonne = h.HandlingUsdPerTonne }),
        lanes = net.Lanes.Select(l => new { from = l.A, to = l.B, mode = l.Mode.ToString().ToLowerInvariant(), km = Math.Round(l.Km) }),
        modes = Network.Modes.Values.Select(m => new
        {
            mode = m.Mode.ToString().ToLowerInvariant(), m.SpeedKmh, m.UsdPerTonneKm, m.KgCo2PerTonneKm, m.Circuity, m.LeadTimeCv,
        }),
        presets,
    });
});

app.MapGet("/api/route/{from}/{to}", (Network net, Planner planner, string from, string to, HttpContext http) =>
    Plan(net, planner, from, to, http.Request.Query));

app.Run();

public partial class Program; // lets the test project use WebApplicationFactory
