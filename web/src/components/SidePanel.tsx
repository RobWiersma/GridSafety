import { Fragment, createContext, useContext, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';

const storageKey = (id: string) => `gridsafety.panel.${id}`;

function readOpen(id: string, fallback: boolean) {
  try {
    const v = localStorage.getItem(storageKey(id));
    return v === null ? fallback : v === '1';
  } catch {
    return fallback;
  }
}

type Reorder = {
  startDrag(id: string, e: PointerEvent<HTMLElement>): void;
  moveBy(id: string, delta: number): void;
  dragging: string | null;
  dropBefore: string | null;
  dropAtEnd: boolean;
  offset: number;
};
const ReorderContext = createContext<Reorder | null>(null);

/** A sidebar card whose header toggles it open or closed. The choice is remembered per browser. Inside a PanelStack it also gets a drag handle. */
export function SidePanel({ id, title, count, defaultOpen = true, children }: {
  id: string;
  title: string;
  count?: number;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(() => readOpen(id, defaultOpen));
  const reorder = useContext(ReorderContext);
  const toggle = () => {
    setOpen((o) => {
      try { localStorage.setItem(storageKey(id), o ? '0' : '1'); } catch { /* storage blocked: just don't remember */ }
      return !o;
    });
  };

  const dragging = reorder?.dragging === id;
  const onHandleKey = (e: KeyboardEvent) => {
    const delta = e.key === 'ArrowUp' ? -1 : e.key === 'ArrowDown' ? 1 : 0;
    if (!delta || !reorder) return;
    e.preventDefault();
    reorder.moveBy(id, delta);
  };

  return (
    <section
      data-panel-id={id}
      className={['panel side-panel', open ? '' : 'side-panel-closed', dragging ? 'side-panel-dragging' : '',
        reorder?.dropBefore === id ? 'side-panel-drop-before' : ''].join(' ')}
      style={dragging ? { transform: `translateY(${reorder!.offset}px)` } : undefined}
    >
      <div className="side-panel-head">
        {reorder && (
          <button className="side-panel-handle" aria-label={`Move the ${title} panel (arrow keys move it up or down)`}
            title="Drag to reorder" onPointerDown={(e) => reorder.startDrag(id, e)} onKeyDown={onHandleKey}>
            <span aria-hidden="true">⠿</span>
          </button>
        )}
        <button className="side-panel-toggle" onClick={toggle} aria-expanded={open} aria-controls={`panel-${id}`}>
          <h2>{title}</h2>
          {count !== undefined && <span className="side-panel-count">{count}</span>}
          <span className="side-panel-chevron" aria-hidden="true">▾</span>
        </button>
      </div>
      {open && <div id={`panel-${id}`} className="side-panel-body">{children}</div>}
    </section>
  );
}

function readSavedOrder(key: string): string[] {
  try {
    const saved = JSON.parse(localStorage.getItem(key) ?? '[]');
    return Array.isArray(saved) ? saved.filter((x) => typeof x === 'string') : [];
  } catch {
    return []; // storage blocked or a bad value
  }
}

/** Lays out SidePanels in an order the user can change by dragging their handles (mouse or touch) or with arrow keys. The order is remembered per browser. */
export function PanelStack({ storageKey: key, panels, className }: {
  storageKey: string;
  panels: { id: string; node: ReactNode }[];
  className?: string;
}) {
  const ids = panels.map((p) => p.id);
  const [order, setOrder] = useState(() => readSavedOrder(key));
  const shown = readOrderFrom(order, ids);
  const save = (next: string[]) => {
    setOrder(next);
    try { localStorage.setItem(key, JSON.stringify(next)); } catch { /* storage blocked: just don't remember */ }
  };
  const isDefault = shown.join() === ids.join();

  const ref = useRef<HTMLElement>(null);
  const [drag, setDrag] = useState<{ id: string; startY: number; offset: number; slot: number } | null>(null);

  const refocus = (id: string) => requestAnimationFrame(() =>
    ref.current?.querySelector<HTMLElement>(`[data-panel-id="${id}"] .side-panel-handle`)?.focus());

  const value = useMemo<Reorder>(() => {
    const others = (id: string) => shown.filter((x) => x !== id);
    // Which gap the pointer is over: the number of other panels whose middle is above it.
    const slotAt = (id: string, y: number) => {
      let slot = 0;
      for (const other of others(id)) {
        const el = ref.current?.querySelector<HTMLElement>(`[data-panel-id="${other}"]`);
        if (!el) continue;
        const r = el.getBoundingClientRect();
        if (y > r.top + r.height / 2) slot++;
      }
      return slot;
    };
    const commit = (id: string, slot: number) => {
      const rest = others(id);
      save([...rest.slice(0, slot), id, ...rest.slice(slot)]);
    };
    return {
      startDrag(id, e) {
        if (e.button !== 0) return;
        e.preventDefault();
        const handle = e.currentTarget;
        handle.setPointerCapture(e.pointerId);
        const startY = e.clientY;
        let slot = shown.indexOf(id);
        setDrag({ id, startY, offset: 0, slot });
        const move = (ev: globalThis.PointerEvent) => {
          slot = slotAt(id, ev.clientY);
          // Scroll the sidebar when dragging near its edges.
          const box = ref.current?.getBoundingClientRect();
          if (box && ev.clientY < box.top + 40) ref.current!.scrollBy(0, -12);
          if (box && ev.clientY > box.bottom - 40) ref.current!.scrollBy(0, 12);
          setDrag({ id, startY, offset: ev.clientY - startY, slot });
        };
        const end = (ev: globalThis.PointerEvent) => {
          handle.removeEventListener('pointermove', move);
          handle.removeEventListener('pointerup', end);
          handle.removeEventListener('pointercancel', end);
          setDrag(null);
          if (ev.type === 'pointerup') commit(id, slot);
          refocus(id);
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', end);
        handle.addEventListener('pointercancel', end);
      },
      moveBy(id, delta) {
        const at = shown.indexOf(id);
        const to = Math.max(0, Math.min(shown.length - 1, at + delta));
        if (to === at) return;
        commit(id, to);
        refocus(id);
      },
      dragging: drag?.id ?? null,
      dropBefore: drag ? others(drag.id)[drag.slot] ?? null : null,
      dropAtEnd: drag ? drag.slot >= others(drag.id).length : false,
      offset: drag?.offset ?? 0,
    };
  }, [shown.join(), drag]); // eslint-disable-line react-hooks/exhaustive-deps

  const byId = new Map(panels.map((p) => [p.id, p.node]));
  return (
    <ReorderContext.Provider value={value}>
      <aside ref={ref} className={`${className ?? ''} ${drag ? 'panel-stack-dragging' : ''}`}>
        {shown.map((id) => <Fragment key={id}>{byId.get(id)}</Fragment>)}
        <div className={`panel-stack-end ${value.dropAtEnd ? 'side-panel-drop-before' : ''}`} aria-hidden="true" />
        {!isDefault && (
          <button className="link-button panel-stack-reset" onClick={() => save(ids)}>Reset panel order</button>
        )}
      </aside>
    </ReorderContext.Provider>
  );
}

/** The saved order, kept to the panels that exist now; a panel added since goes in its default place. */
function readOrderFrom(order: string[], ids: string[]) {
  const known = order.filter((id) => ids.includes(id));
  return [...known, ...ids.filter((id) => !known.includes(id))];
}
