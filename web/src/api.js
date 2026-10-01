// Thin client for the ASP.NET Core API. All planning happens server-side.
export async function getNetwork() {
  const res = await fetch('/api/network');
  if (!res.ok) throw new Error(`Could not load the network (${res.status})`);
  return res.json();
}

export async function getWorld() {
  const res = await fetch('/world.json');
  if (!res.ok) throw new Error(`Could not load map (${res.status})`);
  return res.json();
}

/** The query string for a plan request. Also shown in the UI as the equivalent API call. */
export function planPath({ from, to, cargo, avoid, modes }) {
  const q = new URLSearchParams({
    tonnes: cargo.tonnes,
    value: cargo.value,
    demand: cargo.demand,
    carry: cargo.carry,
    service: cargo.service,
  });
  if (avoid.length) q.set('avoid', avoid.join(','));
  if (modes.length < 4) q.set('modes', modes.join(','));
  return `/api/route/${from}/${to}?${q}`;
}

/** Resolves to the API body for 200 (found) and 404 (no route); throws on anything else. */
export async function getPlan(request, signal) {
  const res = await fetch(planPath(request), { signal });
  if (res.ok) return { found: true, ...(await res.json()) };
  if (res.status === 404 || res.status === 400) return { found: false, ...(await res.json()) };
  throw new Error(`Route planning failed (${res.status})`);
}
