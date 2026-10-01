import { useEffect, useRef } from 'react';
import Globe from 'globe.gl';
import * as THREE from 'three';
import { KIND_INFO, MODE_INFO, short } from './format.js';

const COLORS = {
  ocean: '#06101f',
  land: '#1b2b46',
  origin: '#5eead4',
  destination: '#ffb454',
  closed: '#ef4444',
};

function addStars(globe) {
  const n = 1400;
  const positions = new Float32Array(n * 3);
  for (let i = 0; i < n; i++) {
    const r = 900 + Math.random() * 400;
    const theta = Math.random() * Math.PI * 2;
    const phi = Math.acos(2 * Math.random() - 1);
    positions.set([r * Math.sin(phi) * Math.cos(theta), r * Math.cos(phi), r * Math.sin(phi) * Math.sin(theta)], i * 3);
  }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  globe.scene().add(new THREE.Points(geometry, new THREE.PointsMaterial({ color: 0xffffff, size: 1.6, sizeAttenuation: false, transparent: true, opacity: 0.7 })));
}

/** Mean direction of a set of points. */
function centre(points) {
  const rad = Math.PI / 180;
  let x = 0, y = 0, z = 0;
  for (const { lat, lng } of points) {
    x += Math.cos(lat * rad) * Math.cos(lng * rad);
    y += Math.cos(lat * rad) * Math.sin(lng * rad);
    z += Math.sin(lat * rad);
  }
  return { lat: Math.atan2(z, Math.hypot(x, y)) / rad, lng: Math.atan2(y, x) / rad };
}

/** Largest angular distance (degrees) from `c` to any of the points. */
function spreadFrom(c, points) {
  const rad = Math.PI / 180;
  let spread = 0;
  for (const p of points) {
    const cos = Math.sin(c.lat * rad) * Math.sin(p.lat * rad) + Math.cos(c.lat * rad) * Math.cos(p.lat * rad) * Math.cos((p.lng - c.lng) * rad);
    spread = Math.max(spread, Math.acos(Math.min(1, Math.max(-1, cos))) / rad);
  }
  return spread;
}

/** Where to point the camera for a route: at the centre of all its stops, zoomed out to fit them. */
function frame(points) {
  const c = centre(points);
  return { ...c, spread: spreadFrom(c, points) };
}

const rgba = (color, alpha) => {
  const n = parseInt(color.slice(1), 16);
  return `rgba(${n >> 16}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
};

/**
 * The 3-D globe. React owns the state; this component only paints it.
 * `route` is the selected planned route (with `hubs` and `legs`), or null.
 */
export default function GlobeView({ world, hubs, lanes, from, to, route, closed, modes, showNetwork, onPickHub, onToggleChokepoint, focus }) {
  const host = useRef(null);
  const globe = useRef(null);
  const latest = useRef({});
  latest.current = { onPickHub, onToggleChokepoint };

  // Build the globe once.
  useEffect(() => {
    const el = host.current;
    const g = Globe({ animateIn: true })(el)
      .backgroundColor('rgba(0,0,0,0)')
      .showAtmosphere(true)
      .atmosphereColor('#5aa9ff')
      .atmosphereAltitude(0.2)
      .showGraticules(true)
      .polygonCapColor(() => COLORS.land)
      .polygonAltitude(0.004)
      .polygonStrokeColor(() => 'rgba(160,190,230,0.22)')
      .polygonSideColor(() => 'rgba(20,40,70,0.9)')
      .polygonsTransitionDuration(0)
      .arcStartLat('startLat').arcStartLng('startLng').arcEndLat('endLat').arcEndLng('endLng')
      .arcColor('color')
      .arcStroke('stroke')
      .arcAltitude((d) => (d.mode === 'air' ? null : d.route ? 0.02 : 0.004))
      .arcAltitudeAutoScale(0.32)
      .arcDashLength((d) => (d.route ? 0.4 : 1))
      .arcDashGap((d) => (d.route ? 0.2 : 0))
      .arcDashAnimateTime((d) => (d.route ? 2600 : 0))
      .arcsTransitionDuration(0)
      .pointLat('lat').pointLng('lng')
      .pointAltitude(0.012)
      .pointColor('color')
      .pointRadius('radius')
      .pointsMerge(false)
      .pointsTransitionDuration(0)
      .pointLabel((d) => `<div class="tip"><b>${d.name}</b><br><span>${KIND_INFO[d.kind].label} · ${d.country}${d.kind === 'chokepoint' ? (d.isClosed ? ' · closed (click to reopen)' : ' · click to close') : ''}</span></div>`)
      .onPointClick((d) => (d.kind === 'chokepoint' ? latest.current.onToggleChokepoint(d.id) : latest.current.onPickHub(d.id)))
      .onPointHover((d) => { host.current.style.cursor = d ? 'pointer' : 'grab'; })
      .labelLat('lat').labelLng('lng')
      .labelText('label')
      .labelSize(0.95)
      .labelDotRadius(0)
      .labelAltitude(0.03)
      .labelColor(() => '#ffffff')
      .labelResolution(3)
      .labelsTransitionDuration(0)
      .ringLat('lat').ringLng('lng')
      .ringColor((d) => (t) => rgba(d.color, 1 - t))
      .ringMaxRadius(4)
      .ringPropagationSpeed(2)
      .ringRepeatPeriod(1400);

    const material = g.globeMaterial();
    material.color = new THREE.Color(COLORS.ocean);
    material.emissive = new THREE.Color('#06142b');
    material.shininess = 12;
    addStars(g);

    const controls = g.controls();
    controls.autoRotate = true;
    controls.autoRotateSpeed = 0.45;
    controls.enableDamping = true;
    controls.minDistance = 130;
    controls.maxDistance = 600;
    controls.addEventListener('start', () => (controls.autoRotate = false));

    const resize = () => g.width(el.clientWidth).height(el.clientHeight);
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(el);
    g.pointOfView({ lat: 30, lng: 60, altitude: 2.6 }, 0);

    globe.current = g;
    return () => {
      observer.disconnect();
      g._destructor?.();
      el.replaceChildren();
    };
  }, []);

  // Land outlines
  useEffect(() => {
    if (world && globe.current) globe.current.polygonsData(world.features);
  }, [world]);

  // Hubs, coloured by kind. Chokepoints turn red when closed.
  useEffect(() => {
    const g = globe.current;
    if (!g) return;
    const onRoute = new Set(route?.hubs ?? []);
    g.pointsData(hubs.map((h) => {
      const isClosed = closed.has(h.id);
      const end = h.id === from ? COLORS.origin : h.id === to ? COLORS.destination : null;
      const emphasised = end || onRoute.has(h.id);
      return {
        ...h,
        isClosed,
        color: isClosed ? COLORS.closed : end ?? KIND_INFO[h.kind].color,
        radius: end ? 0.9 : isClosed ? 0.75 : emphasised ? 0.6 : h.kind === 'chokepoint' ? 0.5 : 0.36,
      };
    }));
  }, [hubs, from, to, route, closed]);

  // Lanes: the faint network, plus the selected route's legs on top.
  useEffect(() => {
    const g = globe.current;
    if (!g) return;
    const at = Object.fromEntries(hubs.map((h) => [h.id, h]));
    const arc = (a, b, mode, extra) => ({ startLat: a.lat, startLng: a.lng, endLat: b.lat, endLng: b.lng, mode, ...extra });

    const background = showNetwork
      ? lanes
          .filter((l) => l.mode !== 'air' && modes.has(l.mode) && at[l.from] && at[l.to] && !closed.has(l.from) && !closed.has(l.to))
          .map((l) => arc(at[l.from], at[l.to], l.mode, { route: false, stroke: 0.12, color: rgba(MODE_INFO[l.mode].color, route ? 0.1 : 0.28) }))
      : [];
    const legs = (route?.legs ?? []).map((l) => arc(at[l.from], at[l.to], l.mode, { route: true, stroke: 0.55, color: [rgba(MODE_INFO[l.mode].color, 0.55), MODE_INFO[l.mode].color] }));
    g.arcsData([...background, ...legs]);
  }, [hubs, lanes, modes, showNetwork, route, closed]);

  // Name labels on the route, and pulses at both ends.
  useEffect(() => {
    const g = globe.current;
    if (!g) return;
    const at = Object.fromEntries(hubs.map((h) => [h.id, h]));
    g.labelsData((route?.hubs ?? []).map((id) => ({ ...at[id], label: short(at[id].name) })));
    g.ringsData([
      at[from] && { ...at[from], color: COLORS.origin },
      at[to] && { ...at[to], color: COLORS.destination },
    ].filter(Boolean));
  }, [hubs, route, from, to]);

  // Fly the camera
  useEffect(() => {
    const g = globe.current;
    if (!g || !focus) return;
    g.controls().autoRotate = false;
    const pts = focus.points;
    if (pts.length === 1) {
      g.pointOfView({ lat: pts[0].lat, lng: pts[0].lng, altitude: 1.7 }, 1200);
      return;
    }
    const { lat, lng, spread } = frame(pts);
    g.pointOfView({ lat, lng, altitude: Math.min(3.4, Math.max(1.2, 0.9 + spread / 26)) }, 1400);
  }, [focus]);

  return <div ref={host} className="globe" aria-label="Interactive 3-D globe of the logistics network" />;
}
