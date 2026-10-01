export const MODES = ['sea', 'air', 'rail', 'road'];
export const MODE_INFO = {
  sea: { label: 'Sea', icon: '🚢', color: '#38bdf8' },
  air: { label: 'Air', icon: '✈️', color: '#f472b6' },
  rail: { label: 'Rail', icon: '🚆', color: '#fbbf24' },
  road: { label: 'Road', icon: '🚚', color: '#a3e635' },
};
export const KIND_INFO = {
  port: { label: 'Port', icon: '⚓', color: '#7dd3fc' },
  air: { label: 'Airport', icon: '✈️', color: '#f9a8d4' },
  rail: { label: 'Rail terminal', icon: '🚆', color: '#fcd34d' },
  dc: { label: 'Distribution hub', icon: '📦', color: '#bef264' },
  chokepoint: { label: 'Chokepoint', icon: '⛔', color: '#fb923c' },
};
export const LABELS = {
  lean: { text: 'Lean pick', hint: 'Lowest total landed cost: freight plus inventory in transit plus safety stock' },
  fastest: { text: 'Fastest', hint: 'Shortest door-to-door time' },
  cheapest: { text: 'Cheapest freight', hint: 'Lowest transport and handling charges, ignoring inventory' },
  greenest: { text: 'Lowest CO₂', hint: 'Lowest emissions' },
};

export function usd(n) {
  const a = Math.abs(n);
  const s = a >= 1e6 ? `${(a / 1e6).toFixed(2)}M` : a >= 1e4 ? `${Math.round(a / 1e3)}k` : a >= 1e3 ? `${(a / 1e3).toFixed(1)}k` : `${Math.round(a)}`;
  return `${n < 0 ? '-' : ''}$${s}`;
}
export const usdExact = (n) => `$${Math.round(n).toLocaleString('en-US')}`;

export function days(d) {
  const a = Math.abs(d);
  if (a < 1) return `${Math.round(a * 24)} h`;
  return `${a.toFixed(a < 10 ? 1 : 0)} d`;
}

export function co2(kg) {
  const a = Math.abs(kg);
  return a >= 1000 ? `${(a / 1000).toFixed(a >= 1e4 ? 0 : 1)} t` : `${Math.round(a)} kg`;
}

export const pct = (x) => `${Math.round(x * 100)}%`;
export const short = (name) => name.split(' (')[0].replace(/ Airport$/, '');
