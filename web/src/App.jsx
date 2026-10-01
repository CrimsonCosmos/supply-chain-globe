import { useCallback, useEffect, useMemo, useState } from 'react';
import GlobeView from './GlobeView.jsx';
import HubPicker from './HubPicker.jsx';
import { getNetwork, getPlan, getWorld, planPath } from './api.js';
import { KIND_INFO, LABELS, MODES, MODE_INFO, co2, days, pct, short, usd, usdExact } from './format.js';

const params = new URLSearchParams(window.location.search);
const hubParam = (v) => (/^[A-Za-z0-9]{3,6}$/.test(v ?? '') ? v.toUpperCase() : null);
const numParam = (key, fallback) => {
  const v = parseFloat(params.get(key));
  return Number.isFinite(v) ? v : fallback;
};
const listParam = (key) => (params.get(key) ?? '').split(',').filter(Boolean);

const SERVICE_LEVELS = [0.9, 0.95, 0.98, 0.99];

export default function App() {
  const [network, setNetwork] = useState(null);
  const [world, setWorld] = useState(null);
  const [loadError, setLoadError] = useState(null);

  const [from, setFrom] = useState(hubParam(params.get('from')) ?? 'CNSHA');
  const [to, setTo] = useState(hubParam(params.get('to')) ?? (params.has('from') ? null : 'NLRTM'));
  const [active, setActive] = useState('to');
  const [cargo, setCargo] = useState({
    tonnes: numParam('tonnes', 20),
    value: numParam('value', 40000),
    demand: numParam('demand', 5),
    carry: numParam('carry', 0.2),
    service: numParam('service', 0.95),
  });
  const [closed, setClosed] = useState(() => new Set(listParam('avoid').map((s) => s.toUpperCase())));
  const [modes, setModes] = useState(() => {
    const m = listParam('modes').filter((x) => MODES.includes(x));
    return new Set(m.length ? m : MODES);
  });
  const [showNetwork, setShowNetwork] = useState(true);

  const [result, setResult] = useState(null);
  const [pending, setPending] = useState(false);
  const [selectedId, setSelectedId] = useState(null);
  const [focus, setFocus] = useState(null);

  useEffect(() => {
    Promise.all([getNetwork(), getWorld()])
      .then(([n, w]) => { setNetwork(n); setWorld(w); })
      .catch((e) => setLoadError(e.message));
  }, []);

  const hubs = network?.hubs ?? [];
  const byId = useMemo(() => Object.fromEntries(hubs.map((h) => [h.id, h])), [hubs]);
  const pickable = useMemo(() => hubs.filter((h) => h.kind !== 'chokepoint').sort((a, b) => a.name.localeCompare(b.name)), [hubs]);
  const chokepoints = useMemo(() => hubs.filter((h) => h.kind === 'chokepoint'), [hubs]);

  const request = useMemo(
    () => ({ from, to, cargo, avoid: [...closed].sort(), modes: MODES.filter((m) => modes.has(m)) }),
    [from, to, cargo, closed, modes],
  );
  const requestKey = JSON.stringify(request);

  // Keep the URL shareable.
  useEffect(() => {
    const q = new URLSearchParams();
    if (from) q.set('from', from);
    if (to) q.set('to', to);
    q.set('tonnes', cargo.tonnes); q.set('value', cargo.value); q.set('demand', cargo.demand);
    q.set('carry', cargo.carry); q.set('service', cargo.service);
    if (closed.size) q.set('avoid', [...closed].sort().join(','));
    if (modes.size < MODES.length) q.set('modes', MODES.filter((m) => modes.has(m)).join(','));
    window.history.replaceState(null, '', `?${q}`);
  }, [requestKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Ask the API to plan whenever the scenario changes (debounced so typing in a field is one request).
  useEffect(() => {
    setResult(null);
    if (!network || !from || !to) return;
    if (from === to) { setResult({ found: false, error: 'Origin and destination are the same hub.' }); return; }
    if (!(cargo.tonnes > 0 && cargo.demand > 0 && cargo.value >= 0)) return;
    const ctl = new AbortController();
    setPending(true);
    const timer = setTimeout(() => {
      getPlan(request, ctl.signal)
        .then((r) => { setResult(r); setSelectedId(r.recommended ?? null); setPending(false); })
        .catch((e) => { if (e.name !== 'AbortError') { setResult({ found: false, error: e.message }); setPending(false); } });
    }, 180);
    return () => { clearTimeout(timer); ctl.abort(); };
  }, [requestKey, network]); // eslint-disable-line react-hooks/exhaustive-deps

  const selected = result?.found ? result.routes.find((r) => r.id === selectedId) ?? result.routes[0] : null;

  // Frame the camera on the route, or on the lone selected hub.
  useEffect(() => {
    if (selected) setFocus({ points: selected.hubs.map((id) => byId[id]) });
    else if (!to && from && byId[from]) setFocus({ points: [byId[from]] });
    else if (!from && to && byId[to]) setFocus({ points: [byId[to]] });
  }, [selected, to, from, byId]);

  // A click fills the active end, then hands over to the other so two clicks make a lane.
  const pick = useCallback(
    (id) => {
      (active === 'from' ? setFrom : setTo)(id);
      setActive(active === 'from' ? 'to' : 'from');
    },
    [active],
  );
  const toggleChokepoint = useCallback((id) => setClosed((c) => { const n = new Set(c); n.has(id) ? n.delete(id) : n.add(id); return n; }), []);
  const toggleMode = (m) => setModes((s) => {
    const n = new Set(s);
    if (n.has(m)) { if (n.size > 1) n.delete(m); } else n.add(m);
    return n;
  });

  const swap = () => { setFrom(to); setTo(from); };
  const setField = (key) => (e) => {
    const v = parseFloat(e.target.value);
    setCargo((c) => ({ ...c, [key]: Number.isFinite(v) ? (key === 'carry' ? v / 100 : v) : 0 }));
  };
  const preset = network?.presets.find((p) => p.valuePerTonne === cargo.value && p.demandPerDay === cargo.demand)?.id ?? 'custom';
  const applyPreset = (id) => {
    const p = network.presets.find((x) => x.id === id);
    if (p) setCargo((c) => ({ ...c, value: p.valuePerTonne, demand: p.demandPerDay }));
  };

  return (
    <div className="app">
      <GlobeView
        world={world} hubs={hubs} lanes={network?.lanes ?? []} from={from} to={to} route={selected}
        closed={closed} modes={modes} showNetwork={showNetwork}
        onPickHub={pick} onToggleChokepoint={toggleChokepoint} focus={focus}
      />

      <aside className="panel" aria-label="Route planner">
        <header>
          <h1>Supply Chain <span>Globe</span></h1>
          <p>Plan multimodal freight by time, cost, CO₂ and total landed cost. Click a hub to set it, or a chokepoint to close it.</p>
        </header>

        {loadError && <div className="card error" role="alert">{loadError}. Is the API running?</div>}

        <div className="pickers">
          <HubPicker label="Origin" role="from" hubs={pickable} value={from} active={active === 'from'} onActivate={() => setActive('from')} onChange={setFrom} />
          <button type="button" className="swap" onClick={swap} disabled={!from && !to} aria-label="Swap origin and destination" title="Swap">⇅</button>
          <HubPicker label="Destination" role="to" hubs={pickable} value={to} active={active === 'to'} onActivate={() => setActive('to')} onChange={setTo} />
        </div>

        <Result {...{ from, to, byId, result, selected, selectedId, setSelectedId, pending, closed, setFocus }} />

        <details className="group">
          <summary>Cargo and inventory</summary>
          <div className="fields">
            <label className="wide">Cargo type
              <select value={preset} onChange={(e) => applyPreset(e.target.value)} disabled={!network}>
                {network?.presets.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                {preset === 'custom' && <option value="custom">Custom</option>}
              </select>
            </label>
            <label>Shipment (tonnes)
              <input type="number" min="0.1" step="any" value={cargo.tonnes} onChange={setField('tonnes')} />
            </label>
            <label>Value ($ per tonne)
              <input type="number" min="0" step="any" value={cargo.value} onChange={setField('value')} />
            </label>
            <label>Demand (tonnes/day)
              <input type="number" min="0.01" step="any" value={cargo.demand} onChange={setField('demand')} />
            </label>
            <label>Carrying cost (%/yr)
              <input type="number" min="0" max="100" step="any" value={Math.round(cargo.carry * 1000) / 10} onChange={setField('carry')} />
            </label>
            <label className="wide">Service level for safety stock
              <select value={cargo.service} onChange={(e) => setCargo((c) => ({ ...c, service: parseFloat(e.target.value) }))}>
                {SERVICE_LEVELS.map((s) => <option key={s} value={s}>{pct(s)}</option>)}
              </select>
            </label>
          </div>
        </details>

        <details className="group" open>
          <summary>Disruptions and modes {closed.size > 0 && <span className="count">{closed.size} closed</span>}</summary>
          <div className="chips" role="group" aria-label="Close chokepoints">
            {chokepoints.map((h) => (
              <button type="button" key={h.id} className={`chip ${closed.has(h.id) ? 'off' : ''}`} aria-pressed={closed.has(h.id)} onClick={() => toggleChokepoint(h.id)}>
                {closed.has(h.id) ? '⛔' : '✓'} {short(h.name)}
              </button>
            ))}
          </div>
          <div className="chips modes" role="group" aria-label="Allowed transport modes">
            {MODES.map((m) => (
              <button type="button" key={m} className={`chip ${modes.has(m) ? '' : 'off'}`} style={{ '--mode': MODE_INFO[m].color }} aria-pressed={modes.has(m)} onClick={() => toggleMode(m)}>
                {MODE_INFO[m].icon} {MODE_INFO[m].label}
              </button>
            ))}
          </div>
          <label className="check"><input type="checkbox" checked={showNetwork} onChange={(e) => setShowNetwork(e.target.checked)} /> Show sea, rail and road lanes</label>
        </details>

        <footer>
          {from && to && from !== to && <code title="The API call behind this view">GET {planPath(request)}</code>}
          <a href="/api" target="_blank" rel="noreferrer">API</a>
          <a href="/api/network" target="_blank" rel="noreferrer">Network data</a>
        </footer>
      </aside>

      {!world && !loadError && <div className="loading">Loading the network…</div>}
    </div>
  );
}

function Result({ from, to, byId, result, selected, selectedId, setSelectedId, pending, closed, setFocus }) {
  if (!from || !to) return <div className="card muted">Pick an origin and a destination to plan a route.</div>;
  if (pending || !result) return <div className="card muted">Planning…</div>;

  if (!result.found) {
    return (
      <div className="card none" role="status">
        <h2>No route</h2>
        <p>{result.error}</p>
        <p className="muted">Try reopening a chokepoint or allowing more modes.</p>
      </div>
    );
  }

  const { routes, baseline } = result;
  const t = selected.totals;
  const service = pct(result.cargo.serviceLevel);

  return (
    <>
      {baseline && <Impact baseline={baseline} closed={closed} byId={byId} />}

      <div className="routes" role="radiogroup" aria-label="Candidate routes">
        {routes.map((r) => (
          <button type="button" role="radio" aria-checked={r.id === selectedId} key={r.id} className={`rcard ${r.id === selectedId ? 'selected' : ''}`} onClick={() => setSelectedId(r.id)}>
            <span className="badges">
              {r.labels.length ? r.labels.map((l) => <span key={l} className={`badge ${l}`} title={LABELS[l].hint}>{LABELS[l].text}</span>) : <span className="badge alt">Alternative</span>}
            </span>
            <span className="modeseq" aria-label={`Modes: ${r.modes.join(', ')}`}>{modeSequence(r)}</span>
            <span className="nums">
              <b>{days(r.totals.days)}</b>
              <span>{usd(r.totals.landedUsd)}</span>
              <span>{co2(r.totals.kgCo2)} CO₂</span>
            </span>
          </button>
        ))}
      </div>

      <div className="card route" role="status">
        <h2>
          {selected.labels.length ? selected.labels.map((l) => LABELS[l].text).join(' · ') : 'Alternative route'}
          <span>{(t.km).toLocaleString('en-US', { maximumFractionDigits: 0 })} km</span>
        </h2>

        <dl className="metrics">
          <Metric label="Door to door" value={days(t.days)} sub={`${pct(t.waitingShare)} waiting at hubs`} hint="Time moving plus time dwelling at ports, terminals and chokepoints. Waiting is the non-value-adding share." />
          <Metric label={`Reliable by (${service})`} value={days(t.daysAtServiceLevel)} sub={`±${days(t.sigmaDays)} std. dev.`} hint="Expected time plus the buffer needed to arrive on time at the chosen service level." />
          <Metric label="Freight" value={usd(t.freightUsd)} sub="transport + handling" />
          <Metric label="Inventory in transit" value={usd(t.pipelineUsd)} sub="capital tied up" hint="Cargo value × annual carrying cost × days in transit." />
          <Metric label="Safety stock" value={usd(t.safetyStockUsd)} sub={`${t.safetyStockTonnes.toFixed(1)} t buffer`} hint="Extra stock needed to cover lead-time variability at the chosen service level." />
          <Metric label="Landed cost" value={usd(t.landedUsd)} sub={`${usdExact(t.landedUsdPerTonne)} / tonne`} strong hint="Freight + inventory in transit + safety stock." />
          <Metric label="Emissions" value={`${co2(t.kgCo2)} CO₂`} sub={`${(t.kgCo2 / result.cargo.tonnes).toFixed(0)} kg / tonne`} />
          {selected.chokepoints.length > 0 && <Metric label="Passes through" value={selected.chokepoints.map((id) => short(byId[id].name)).join(', ')} wide />}
        </dl>

        <Timeline route={selected} byId={byId} setFocus={setFocus} />
      </div>
    </>
  );
}

function Metric({ label, value, sub, strong, wide, hint }) {
  return (
    <div className={`metric ${strong ? 'strong' : ''} ${wide ? 'wide' : ''}`} title={hint}>
      <dt>{label}</dt>
      <dd>{value}</dd>
      {sub && <small>{sub}</small>}
    </div>
  );
}

/** Mode icons for a route, collapsing consecutive legs of one mode. */
function modeSequence(route) {
  const seq = [];
  for (const l of route.legs) if (seq.at(-1) !== l.mode) seq.push(l.mode);
  return seq.map((m, i) => <span key={i} style={{ color: MODE_INFO[m].color }}>{MODE_INFO[m].icon}{i < seq.length - 1 && <i>›</i>}</span>);
}

function Impact({ baseline, closed, byId }) {
  const { impact } = baseline;
  const names = [...closed].map((id) => short(byId[id]?.name ?? id)).join(', ');
  if (!impact.rerouted) {
    return <div className="card ok" role="status"><b>No impact.</b> Closing {names} does not change the best route.</div>;
  }
  const diff = (n, fmt, more, less) => (Math.abs(n) < 1e-9 ? 'about the same' : `${fmt(Math.abs(n))} ${n > 0 ? more : less}`);
  return (
    <div className="card warn" role="status">
      <h2>Rerouted around {names}</h2>
      <p>
        Versus the open network, the best route is now <b>{diff(impact.extraDays, days, 'slower', 'faster')}</b>,
        {' '}<b>{diff(impact.extraLandedUsd, usd, 'more', 'less')}</b> in landed cost, and
        {' '}<b>{diff(impact.extraKgCo2, (kg) => `${co2(kg)} CO₂`, 'more', 'less')}</b>.
      </p>
    </div>
  );
}

function Timeline({ route, byId, setFocus }) {
  return (
    <ol className="timeline">
      {route.hubs.map((id, i) => {
        const hub = byId[id];
        const wait = i > 0 && i < route.hubs.length - 1 ? route.legs[i].waitHours : 0;
        const leg = route.legs[i];
        return (
          <li key={`${id}-${i}`} className={i === 0 ? 'first' : i === route.hubs.length - 1 ? 'last' : ''}>
            <button type="button" className="stop" onClick={() => setFocus({ points: [hub] })}>
              <span className="flag" aria-hidden>{KIND_INFO[hub.kind].icon}</span>
              <span className="name">{hub.name}</span>
              {wait > 0 && <em title="Dwell time before the next leg">waits {days(wait / 24)}</em>}
              <span className="code">{hub.id}</span>
            </button>
            {leg && (
              <div className="leg" style={{ '--mode': MODE_INFO[leg.mode].color }}>
                {MODE_INFO[leg.mode].icon} {MODE_INFO[leg.mode].label} · {Math.round(leg.km).toLocaleString('en-US')} km · {days(leg.hours / 24)} · {usd(leg.usd)}
              </div>
            )}
          </li>
        );
      })}
    </ol>
  );
}
