namespace SupplyChain.Api;

/// <summary>What is being shipped and how it is replenished. Drives inventory carrying cost.</summary>
public record Cargo(double Tonnes, double ValuePerTonne, double DemandPerDay, double CarryRate, double ServiceLevel)
{
    public double Value => Tonnes * ValuePerTonne;
    /// <summary>Days of demand one shipment covers.</summary>
    public double CycleDays => Tonnes / DemandPerDay;
    /// <summary>Standard normal quantile for the service level (Abramowitz and Stegun 26.2.23).</summary>
    public double Z => InverseNormal(ServiceLevel);

    private static double InverseNormal(double p)
    {
        var t = Math.Sqrt(-2 * Math.Log(1 - p));
        return t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
    }
}

public record PlanRequest(int Origin, int Destination, Cargo Cargo, IReadOnlySet<int> Closed, IReadOnlySet<Mode> Modes);

public record Leg(string From, string To, string Mode, double Km, double Hours, double WaitHours, double Usd, double KgCo2);

public record RouteTotals(
    double Km, double Days, double MovingDays, double WaitingDays, double WaitingShare,
    double SigmaDays, double DaysAtServiceLevel,
    double FreightUsd, double PipelineUsd, double SafetyStockTonnes, double SafetyStockUsd, double LandedUsd, double LandedUsdPerTonne,
    double KgCo2);

public record PlannedRoute(string Id, string[] Labels, string[] Hubs, string[] Modes, string[] Chokepoints, Leg[] Legs, RouteTotals Totals);

public record Impact(double ExtraDays, double ExtraLandedUsd, double ExtraKgCo2, bool Rerouted);

public record Baseline(PlannedRoute Route, Impact Impact);

public record Plan(PlannedRoute[] Routes, string Recommended, Baseline? Baseline);

/// <summary>
/// Plans routes through the network. It runs Dijkstra under many objectives (time, freight cost, CO2, total
/// landed cost, and blends of the first three), re-runs with each interior hub closed to surface genuinely
/// different alternatives, then scores every distinct candidate with the exact cost model and labels the best.
/// This is deterministic optimisation, not machine learning.
/// </summary>
public sealed class Planner(Network network)
{
    private const double HoursPerDay = 24, DaysPerYear = 365;

    /// <summary>One lane traversal, including the wait at the hub it leaves (none at the origin).</summary>
    private readonly record struct Step(int From, int To, Lane Lane, double Hours, double WaitHours, double UsdPerTonne, double KgPerTonne, double VarianceHours2);

    public Plan? Find(PlanRequest request)
    {
        var best = Candidates(request).OrderBy(r => r.Totals.LandedUsd).ToList();
        if (best.Count == 0) return null;

        var routes = Select(best);
        var recommended = routes.First(r => r.Labels.Contains("lean"));

        Baseline? baseline = null;
        if (request.Closed.Count > 0)
        {
            var open = Candidates(request with { Closed = new HashSet<int>() }).OrderBy(r => r.Totals.LandedUsd).FirstOrDefault();
            if (open is not null)
            {
                var rerouted = !SameRoute(open, recommended);
                baseline = new Baseline(open with { Id = "baseline", Labels = ["baseline"] }, new Impact(
                    recommended.Totals.Days - open.Totals.Days,
                    recommended.Totals.LandedUsd - open.Totals.LandedUsd,
                    recommended.Totals.KgCo2 - open.Totals.KgCo2,
                    rerouted));
            }
        }

        return new Plan(routes, recommended.Id, baseline);
    }

    /// <summary>The best route for each label, then the next-best distinct routes, up to five in all.</summary>
    private static PlannedRoute[] Select(List<PlannedRoute> byLanded)
    {
        var labelled = new Dictionary<string, PlannedRoute>
        {
            ["lean"] = byLanded[0],
            ["fastest"] = byLanded.MinBy(r => r.Totals.Days)!,
            ["cheapest"] = byLanded.MinBy(r => r.Totals.FreightUsd)!,
            ["greenest"] = byLanded.MinBy(r => r.Totals.KgCo2)!,
        };
        var chosen = byLanded.Where(r => labelled.ContainsValue(r)).ToList();
        chosen.AddRange(byLanded.Where(r => !chosen.Contains(r)).Take(Math.Max(0, 5 - chosen.Count)));

        return chosen.OrderBy(r => r.Totals.LandedUsd).Select((r, i) => r with
        {
            Id = $"r{i + 1}",
            Labels = labelled.Where(kv => kv.Value == r).Select(kv => kv.Key).ToArray(),
        }).ToArray();
    }

    private static bool SameRoute(PlannedRoute a, PlannedRoute b) => Key(a) == Key(b);
    private static string Key(PlannedRoute r) => string.Join('>', r.Legs.Select(l => $"{l.From}:{l.Mode}")) + ">" + r.Hubs[^1];

    private List<PlannedRoute> Candidates(PlanRequest req)
    {
        var found = new Dictionary<string, PlannedRoute>();
        void Consider(List<Step>? steps)
        {
            if (steps is null) return;
            var route = Evaluate(req, steps);
            found.TryAdd(Key(route), route);
        }

        var cargo = req.Cargo;
        double holdPerHour = cargo.Value * cargo.CarryRate / DaysPerYear / HoursPerDay;
        double safetyPerSigmaHour = cargo.Value * cargo.CarryRate / DaysPerYear / HoursPerDay * cargo.Z;

        Func<Step, double> time = s => s.Hours + s.WaitHours;
        Func<Step, double> freight = s => s.UsdPerTonne * cargo.Tonnes;
        Func<Step, double> co2 = s => s.KgPerTonne * cargo.Tonnes;
        // Freight plus inventory in transit plus a linear upper bound on the safety stock needed.
        Func<Step, double> lean = s => freight(s) + holdPerHour * time(s) + safetyPerSigmaHour * Math.Sqrt(s.VarianceHours2);

        var single = new[] { time, freight, co2, lean }.Select(f => Route(req, f, req.Closed)).ToList();
        if (single.Any(s => s is null)) return [];
        foreach (var s in single) Consider(s);

        // Blends of time, cost and CO2, each scaled by its own optimum so the weights are comparable.
        double t0 = Sum(single[0]!, time), c0 = Sum(single[1]!, freight), e0 = Sum(single[2]!, co2);
        for (int wt = 0; wt <= 4; wt++)
            for (int wc = 0; wc <= 4 - wt; wc++)
            {
                int we = 4 - wt - wc;
                Func<Step, double> blend = s => wt * time(s) / t0 + wc * freight(s) / c0 + we * co2(s) / Math.Max(e0, 1e-9);
                Consider(Route(req, blend, req.Closed));
            }

        // Close each interior hub of the leading routes in turn to find the next-best alternatives.
        foreach (var seed in single.Select(s => s!).ToList())
            foreach (var hub in seed.Skip(1).Select(s => s.From).Distinct())
            {
                var closed = new HashSet<int>(req.Closed) { hub };
                foreach (var f in new[] { lean, time, freight, co2 }) Consider(Route(req, f, closed));
            }

        return [.. found.Values];
    }

    private static double Sum(List<Step> steps, Func<Step, double> f) => Math.Max(steps.Sum(f), 1e-9);

    private Step MakeStep(int from, int origin, (int To, Lane Lane) edge)
    {
        var hub = network.Hubs[from];
        var p = Network.Modes[edge.Lane.Mode];
        bool intermediate = from != origin;
        double wait = intermediate ? hub.DwellHours : 0;
        double variance = Math.Pow(p.LeadTimeCv * edge.Lane.Hours, 2) + Math.Pow(hub.DwellCv * wait, 2);
        return new Step(from, edge.To, edge.Lane, edge.Lane.Hours, wait,
            edge.Lane.UsdPerTonne + (intermediate ? hub.HandlingUsdPerTonne : 0), edge.Lane.KgCo2PerTonne, variance);
    }

    /// <summary>Dijkstra over hubs; closed hubs and disallowed modes are skipped. Costs are non-negative.</summary>
    private List<Step>? Route(PlanRequest req, Func<Step, double> cost, IReadOnlySet<int> closed)
    {
        var dist = new double[network.Count];
        var prev = new Step?[network.Count];
        Array.Fill(dist, double.PositiveInfinity);
        dist[req.Origin] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(req.Origin, 0);

        while (queue.TryDequeue(out var u, out var d))
        {
            if (d > dist[u]) continue;
            if (u == req.Destination) break;
            foreach (var edge in network.Neighbours(u))
            {
                if (closed.Contains(edge.To) || !req.Modes.Contains(edge.Lane.Mode)) continue;
                var step = MakeStep(u, req.Origin, edge);
                var nd = d + cost(step);
                if (nd < dist[edge.To])
                {
                    dist[edge.To] = nd;
                    prev[edge.To] = step;
                    queue.Enqueue(edge.To, nd);
                }
            }
        }

        if (double.IsPositiveInfinity(dist[req.Destination])) return null;
        var path = new List<Step>();
        for (var at = req.Destination; at != req.Origin; at = prev[at]!.Value.From) path.Add(prev[at]!.Value);
        path.Reverse();
        return path;
    }

    /// <summary>The exact cost model for one concrete route.</summary>
    private PlannedRoute Evaluate(PlanRequest req, List<Step> steps)
    {
        var cargo = req.Cargo;
        double moving = steps.Sum(s => s.Hours), waiting = steps.Sum(s => s.WaitHours);
        double totalDays = (moving + waiting) / HoursPerDay;
        double sigmaDays = Math.Sqrt(steps.Sum(s => s.VarianceHours2)) / HoursPerDay;
        double freightUsd = steps.Sum(s => s.UsdPerTonne) * cargo.Tonnes;

        double carryPerDay = cargo.Value * cargo.CarryRate / DaysPerYear;
        double pipelineUsd = carryPerDay * totalDays;
        double safetyTonnes = cargo.Z * sigmaDays * cargo.DemandPerDay;
        double safetyUsd = safetyTonnes * cargo.ValuePerTonne * cargo.CarryRate / DaysPerYear * cargo.CycleDays;
        double landed = freightUsd + pipelineUsd + safetyUsd;

        var legs = steps.Select(s => new Leg(
            network.Hubs[s.From].Id, network.Hubs[s.To].Id, s.Lane.Mode.ToString().ToLowerInvariant(),
            s.Lane.Km, s.Hours, s.WaitHours, s.UsdPerTonne * cargo.Tonnes, s.KgPerTonne * cargo.Tonnes)).ToArray();

        var hubs = steps.Select(s => network.Hubs[s.From].Id).Append(network.Hubs[steps[^1].To].Id).ToArray();
        var chokepoints = hubs.Skip(1).SkipLast(1).Where(id => network.TryGet(id, out var h) && h.IsChokepoint).ToArray();

        return new PlannedRoute(
            Id: "", Labels: [], Hubs: hubs,
            Modes: legs.Select(l => l.Mode).Distinct().ToArray(),
            Chokepoints: chokepoints, Legs: legs,
            Totals: new RouteTotals(
                steps.Sum(s => s.Lane.Km), totalDays, moving / HoursPerDay, waiting / HoursPerDay, waiting / (moving + waiting),
                sigmaDays, totalDays + cargo.Z * sigmaDays,
                freightUsd, pipelineUsd, safetyTonnes, safetyUsd, landed, landed / cargo.Tonnes,
                steps.Sum(s => s.KgPerTonne) * cargo.Tonnes));
    }
}
