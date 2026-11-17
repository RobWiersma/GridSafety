import { useEffect, useRef, useState } from 'react';
import type { MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from 'react';

/** NAIP is about 60 cm per pixel, so a full-screen frame is already upscaled; past 4× it is only bigger blocks. */
const MAX_ZOOM = 4;
const STEP = 1.5;

interface View { scale: number; x: number; y: number }
const FIT: View = { scale: 1, x: 0, y: 0 };

/** Keep the photo covering the stage, so it can't be dragged off screen. */
function clamp(el: HTMLElement | null, v: View): View {
  if (!el) return v;
  const maxX = (el.clientWidth * (v.scale - 1)) / 2;
  const maxY = (el.clientHeight * (v.scale - 1)) / 2;
  return { scale: v.scale, x: Math.min(maxX, Math.max(-maxX, v.x)), y: Math.min(maxY, Math.max(-maxY, v.y)) };
}

/** Zoom to `scale`, keeping the point (px, py), relative to the stage centre, fixed on screen. */
function zoomAt(el: HTMLElement | null, v: View, scale: number, px = 0, py = 0): View {
  const s = Math.min(MAX_ZOOM, Math.max(1, scale));
  if (s === 1) return FIT;
  const k = s / v.scale;
  return clamp(el, { scale: s, x: px - (px - v.x) * k, y: py - (py - v.y) * k });
}

/** A client point relative to the stage centre. */
function local(el: HTMLElement, clientX: number, clientY: number) {
  const r = el.getBoundingClientRect();
  return { x: clientX - r.left - r.width / 2, y: clientY - r.top - r.height / 2 };
}

/**
 * Full-screen photo viewer: wheel or pinch to zoom at the pointer, drag to pan, double-click to toggle 2×,
 * + / − / 0 on the keyboard, Esc to close. A modal <dialog>, so it sits above the drawer.
 */
export function PhotoZoom({ src, alt, caption, onClose }: { src: string; alt: string; caption?: string; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<View>(FIT);
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const gesture = useRef<{ dist: number; view: View; mid: { x: number; y: number } } | null>(null);

  useEffect(() => {
    const d = dialog.current;
    if (d && !d.open) d.showModal();
    return () => d?.close();
  }, []);

  // React's wheel listener is passive, and the page behind must not scroll while zooming.
  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const p = local(el, e.clientX, e.clientY);
      setView((v) => zoomAt(el, v, v.scale * Math.exp(-e.deltaY * 0.002), p.x, p.y));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  // Keys go to the viewer wherever focus is, and in the capture phase, so its Esc never reaches the drawer underneath.
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = stage.current;
      let next: ((v: View) => View) | null = null;
      if (e.key === 'Escape') close.current();
      else if (e.key === '+' || e.key === '=') next = (v) => zoomAt(el, v, v.scale * STEP);
      else if (e.key === '-' || e.key === '_') next = (v) => zoomAt(el, v, v.scale / STEP);
      else if (e.key === '0') next = () => FIT;
      else return;
      e.preventDefault();
      e.stopPropagation();
      if (next) setView(next);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, []);

  const at = (clientX: number, clientY: number) => local(stage.current!, clientX, clientY);
  const zoom = (v: View, scale: number, px = 0, py = 0) => zoomAt(stage.current, v, scale, px, py);

  const onPointerDown = (e: ReactPointerEvent) => {
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* the pointer is already gone */ }
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.current.size === 2) {
      const [a, b] = [...pointers.current.values()];
      gesture.current = { dist: Math.hypot(a.x - b.x, a.y - b.y), view, mid: at((a.x + b.x) / 2, (a.y + b.y) / 2) };
    }
  };

  const onPointerMove = (e: ReactPointerEvent) => {
    const prev = pointers.current.get(e.pointerId);
    if (!prev) return;
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.current.size === 2 && gesture.current) {
      const [a, b] = [...pointers.current.values()];
      const g = gesture.current;
      const mid = at((a.x + b.x) / 2, (a.y + b.y) / 2);
      const zoomed = zoom(g.view, g.view.scale * (Math.hypot(a.x - b.x, a.y - b.y) / g.dist), g.mid.x, g.mid.y);
      setView(clamp(stage.current, { ...zoomed, x: zoomed.x + mid.x - g.mid.x, y: zoomed.y + mid.y - g.mid.y }));
    } else if (pointers.current.size === 1) {
      setView((v) => (v.scale === 1 ? v : clamp(stage.current, { ...v, x: v.x + e.clientX - prev.x, y: v.y + e.clientY - prev.y })));
    }
  };

  const onPointerUp = (e: ReactPointerEvent) => {
    pointers.current.delete(e.pointerId);
    if (pointers.current.size < 2) gesture.current = null;
  };

  const onDoubleClick = (e: ReactMouseEvent) => {
    const p = at(e.clientX, e.clientY);
    setView((v) => (v.scale > 1 ? FIT : zoom(v, 2, p.x, p.y)));
  };

  const zoomed = view.scale > 1;
  return (
    <dialog
      ref={dialog}
      className="photo-zoom"
      aria-label="Zoomed photo"
      onCancel={(e) => { e.preventDefault(); onClose(); }}
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div className="photo-zoom-bar">
        <span className="muted small">{Math.round(view.scale * 100)}%</span>
        <button className="btn btn-ghost" onClick={() => setView((v) => zoom(v, v.scale / STEP))} disabled={!zoomed} aria-label="Zoom out">−</button>
        <button className="btn btn-ghost" onClick={() => setView((v) => zoom(v, v.scale * STEP))} disabled={view.scale >= MAX_ZOOM} aria-label="Zoom in">+</button>
        <button className="btn btn-ghost" onClick={() => setView(FIT)} disabled={!zoomed}>Fit</button>
        <button className="btn btn-ghost" onClick={onClose} aria-label="Close" autoFocus>✕</button>
      </div>
      <div
        ref={stage}
        className={`photo-zoom-stage ${zoomed ? 'is-zoomed' : ''}`}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onDoubleClick={onDoubleClick}
      >
        <img
          src={src}
          alt={alt}
          draggable={false}
          style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.scale})` }}
        />
      </div>
      <p className="muted small photo-zoom-hint">
        {caption ? `${caption} ` : ''}Scroll or pinch to zoom, drag to pan, double-click for 2×.
      </p>
    </dialog>
  );
}
