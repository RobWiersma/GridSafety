import { useState, type ReactNode } from 'react';

const storageKey = (id: string) => `gridsafety.panel.${id}`;

function readOpen(id: string, fallback: boolean) {
  try {
    const v = localStorage.getItem(storageKey(id));
    return v === null ? fallback : v === '1';
  } catch {
    return fallback;
  }
}

/** A sidebar card whose header toggles it open or closed. The choice is remembered per browser. */
export function SidePanel({ id, title, count, defaultOpen = true, children }: {
  id: string;
  title: string;
  count?: number;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(() => readOpen(id, defaultOpen));
  const toggle = () => {
    setOpen((o) => {
      try { localStorage.setItem(storageKey(id), o ? '0' : '1'); } catch { /* storage blocked: just don't remember */ }
      return !o;
    });
  };

  return (
    <section className={`panel side-panel ${open ? '' : 'side-panel-closed'}`}>
      <button className="side-panel-toggle" onClick={toggle} aria-expanded={open} aria-controls={`panel-${id}`}>
        <h2>{title}</h2>
        {count !== undefined && <span className="side-panel-count">{count}</span>}
        <span className="side-panel-chevron" aria-hidden="true">▾</span>
      </button>
      {open && <div id={`panel-${id}`} className="side-panel-body">{children}</div>}
    </section>
  );
}
