import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { KIND_INFO } from './format.js';

/** A searchable combobox over hubs (ports, airports, rail terminals, distribution hubs). */
export default function HubPicker({ label, role, hubs, value, active, onActivate, onChange }) {
  const id = useId();
  const box = useRef(null);
  const selected = hubs.find((h) => h.id === value);
  const [text, setText] = useState('');
  const [open, setOpen] = useState(false);
  const [cursor, setCursor] = useState(0);

  // Show the chosen hub's name unless the user is typing.
  useEffect(() => { if (!open) setText(selected ? selected.name : ''); }, [selected, open]);

  const matches = useMemo(() => {
    const q = text.trim().toLowerCase();
    if (!q || (selected && q === selected.name.toLowerCase())) return hubs.slice(0, 60);
    return hubs
      .filter((h) => `${h.name} ${h.id} ${h.country} ${KIND_INFO[h.kind].label}`.toLowerCase().includes(q))
      .slice(0, 60);
  }, [text, hubs, selected]);

  useEffect(() => setCursor(0), [text, open]);

  const choose = (h) => { onChange(h.id); setOpen(false); box.current?.blur(); };
  const onKey = (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setCursor((c) => Math.min(c + 1, matches.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, 0)); }
    else if (e.key === 'Enter' && open && matches[cursor]) { e.preventDefault(); choose(matches[cursor]); }
    else if (e.key === 'Escape') { setOpen(false); box.current?.blur(); }
  };

  return (
    <div className={`picker ${role} ${active ? 'active' : ''}`}>
      <label htmlFor={id}>{label}</label>
      <div className="field">
        <span className="flag" aria-hidden>{selected ? KIND_INFO[selected.kind].icon : '🔍'}</span>
        <input
          id={id}
          ref={box}
          role="combobox"
          aria-expanded={open}
          aria-controls={`${id}-list`}
          aria-autocomplete="list"
          autoComplete="off"
          spellCheck={false}
          placeholder="Search a port, airport or city"
          value={text}
          onFocus={(e) => { onActivate(); setOpen(true); e.target.select(); }}
          onBlur={() => setTimeout(() => setOpen(false), 120)}
          onChange={(e) => { setText(e.target.value); setOpen(true); }}
          onKeyDown={onKey}
        />
        {value && <button type="button" className="clear" aria-label={`Clear ${label}`} onMouseDown={(e) => e.preventDefault()} onClick={() => { onChange(null); setText(''); }}>×</button>}
      </div>
      {open && (
        <ul className="options" id={`${id}-list`} role="listbox">
          {matches.length === 0 && <li className="empty">No matching hub</li>}
          {matches.map((h, i) => (
            <li key={h.id} role="option" aria-selected={h.id === value} className={i === cursor ? 'cursor' : ''}
                onMouseDown={(e) => e.preventDefault()} onMouseEnter={() => setCursor(i)} onClick={() => choose(h)}>
              <span className="flag" aria-hidden>{KIND_INFO[h.kind].icon}</span>
              <span className="name">{h.name}</span>
              <span className="code">{h.country} · {h.id}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
