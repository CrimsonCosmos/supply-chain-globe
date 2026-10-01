# Supply Chain Globe

An interactive 3-D globe that plans **multimodal freight routes** and shows what they cost in time, money,
CO₂ and tied-up inventory. It is "Google Maps for logistics": instead of the fastest drive, you get the
route that minimises **total landed cost** (freight plus the inventory sitting in transit plus the safety
stock needed to cover delays), next to the fastest, cheapest and lowest-carbon alternatives. Close a
chokepoint such as Suez or Panama and watch the network reroute.

It is a fork of [`country-route-globe`](https://github.com/CrimsonCosmos/country-route-globe) and keeps its
stack: ASP.NET Core minimal API, React 19 + Vite, [globe.gl](https://globe.gl) (three.js). The country border
graph is replaced by a network of ports, airports, rail terminals and sea chokepoints.

```
GET /api/route/CNSHA/NLRTM                         Shanghai -> Rotterdam, all modes
GET /api/route/CNSHA/NLRTM?avoid=SUEZ,BAB          ...with Suez and the Red Sea closed
GET /api/route/CNSHA/USCHI?value=1000000&modes=air,road
```

## What it does

- **Five candidate routes per query**, each labelled where it is best: **Lean pick** (lowest landed cost),
  **Fastest**, **Cheapest freight**, **Lowest CO₂**, plus the next-best distinct alternatives.
- **Landed cost** = freight and handling + inventory in transit (`value × carrying rate × days / 365`) +
  safety stock (extra stock to cover lead-time variability at a chosen service level). This is what makes
  a slow ship beat a plane for bulk goods and a plane beat a ship for high-value goods.
- **Waiting share**: the fraction of door-to-door time spent dwelling at hubs rather than moving, the
  "waste" a lean planner cares about. Routes also report the lead time you can *rely on* at your service
  level, not just the average.
- **Disruptions**: close any chokepoint (Suez, Bab-el-Mandeb, Hormuz, Panama, Gibraltar, Cape of Good
  Hope) from the panel or by clicking it on the globe. The response includes a `baseline` with the open-network
  route and the delta in days, dollars and CO₂.
- **Mode filter** (sea, air, rail, road), cargo presets (electronics, pharma, apparel, auto parts,
  machinery, bulk), and shareable URLs: every setting lives in the query string.

## API

| Endpoint | Returns |
| --- | --- |
| `GET /api/route/{from}/{to}` | `{ origin, destination, cargo, avoid, modes, recommended, routes[], baseline }` |
| `GET /api/network` | Hubs, lanes, mode profiles (the cost model's assumptions) and cargo presets. |
| `GET /api` | Usage info. |

Query parameters for `/api/route`: `tonnes` (20), `value` USD per tonne (40000), `demand` tonnes per day (5),
`carry` annual carrying-cost rate (0.2), `service` level for safety stock (0.95), `avoid` comma-separated hub
ids, `modes` comma-separated subset of `sea,air,rail,road`.

Errors are JSON `{ "error": ... }`: `400` for malformed input (bad hub code, number out of range, unknown mode,
same origin and destination, closing the origin), `404` for an unknown hub or when the closures and modes
leave no route.

## How the planning works

`Planner.cs` runs Dijkstra over the network under many objectives: time, freight, CO₂, a lean
objective (freight + holding cost + a linear bound on safety stock), and 15 weighted blends of time, cost and
CO₂. It then re-runs those with each interior hub of the leading routes closed in turn, which surfaces
genuinely different alternatives (the Cape route when Suez is shut, a rail land bridge, a Panama routing).
Every distinct candidate is scored once with the **exact** cost model, and the best on each metric gets
its label. The lean pick is the candidate with the lowest landed cost.

This is **deterministic optimisation, not machine learning**. There is no trained model. The place a
learned component would slot in is the lane parameters in `Network.cs` (transit times, dwell and variability), which are static
estimates today.

| Path | Role |
| --- | --- |
| `src/SupplyChain.Api/Data/network.json` | 65 hubs and the curated lanes (compiled in as an embedded resource). |
| `src/SupplyChain.Api/Network.cs` | Loads the network, generates air lanes and in-metro road drayage, holds the mode profiles. |
| `src/SupplyChain.Api/Planner.cs` | Candidate generation, exact cost model, labelling, disruption baseline. |
| `src/SupplyChain.Api/Program.cs` | Routes, validation, static UI hosting. |
| `web/` | React UI. `vite build` writes into the API's `wwwroot`, so one app serves both. |
| `tests/SupplyChain.Tests/` | 42 xUnit tests: network integrity, planner behaviour, and HTTP endpoints. |

## Assumptions and limits

These numbers are **illustrative planning values, not quotes**. Treat the output as a way to compare
options and see sensitivities, not to price a shipment.

1. **Mode profiles** (`Network.cs`): sea 30 km/h, $0.010 per tonne-km, 16 g CO₂ per tonne-km; air 750 km/h,
   $0.55, 600 g; rail 45 km/h, $0.035, 25 g; road 55 km/h, $0.09, 95 g. Distances are great-circle
   distance times a circuity factor (1.05 air, 1.25 sea and rail, 1.30 road). A lane is a straight line
   between hubs, so a sea lane can cut across a corner of land; it is a cost estimate, not a ship track.
2. **Hubs dwell**: port 48 h, airport 10 h, rail terminal 24 h, distribution hub 8 h, chokepoints from 0 h
   (Cape) to 30 h (Panama), with per-hub overrides. The Caspian crossing and Poti are given long, highly
   variable dwells to reflect the Middle Corridor's ferry bottlenecks. Dwell is paid at every intermediate
   hub and not at the origin or destination.
3. **Variability**: each leg's duration has a coefficient of variation by mode (sea 0.20, rail 0.18,
   road 0.10, air 0.06) and each hub's dwell has its own. Independent variances add, and safety stock is
   `z × σ × demand`, carried for one replenishment cycle.
4. **The network is a sample, not the world.** 65 hubs: 27 ports, 20 airports, 10 rail terminals, 2 distribution
   hubs and 6 chokepoints, covering the main Asia, Europe, North America, Middle East, Africa, South America and
   Australia flows. It omits most of Latin America's west coast, much of Africa, the Russian rail corridors
   (left out deliberately) and all last-mile detail. Add a hub and its lanes in `network.json` and it is live.
5. **Air lanes** are generated between every pair of airports within 13,500 km, and **road drayage** links hubs in the
   same metro (for example Shanghai port and Pudong airport), so a route can change mode at a gateway.
6. **No capacity, schedules, tariffs, customs or live data.** Lanes are always available at their modelled
   cost. Closures are a what-if you set, not a feed of real events.
7. **Disruptions remove hubs.** A closed chokepoint or hub cannot be used at all; there is no partial
   degradation (such as a delay surcharge).

## Run locally

Requires the .NET 10 SDK and Node 20+.

```bash
dotnet test                                   # unit + integration tests
(cd web && npm ci && npm run build)           # build the UI into wwwroot
dotnet run --project src/SupplyChain.Api      # http://localhost:5127 (the port it prints)
```

For UI hot reload, run the API on port 5080 and Vite alongside it; Vite proxies `/api` to it:

```bash
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/SupplyChain.Api --no-launch-profile
(cd web && npm run dev)
```

## Deploy to Azure

One App Service hosts the API and the UI, so there is no CORS setup. Requires `az login`. This uses the
Basic B1 tier with Always On (about $13/month); `SKU=F1 ./deploy.sh` deploys for free but cold-starts.

```bash
./deploy.sh supply-chain-globe-dg   # app name must be globally unique
```

## Data

Land outlines (`web/public/world.json`) come from [Natural Earth](https://www.naturalearthdata.com/)
(public domain), carried over from the country globe. Hub locations are approximate public coordinates.
