import { useEffect, useRef, useState } from 'react';

/** The signed-in user's name as a dropdown: supervisor tools and sign-out live here. */
export function UserMenu({ name, isSupervisor, onVisitors, onAlerts, onSignOut }: {
  name: string;
  isSupervisor: boolean;
  onVisitors(): void;
  onAlerts(): void;
  onSignOut(): void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false);
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const pick = (fn: () => void) => () => {
    setOpen(false);
    fn();
  };

  return (
    <div className="user-menu" ref={ref}>
      <button className="btn btn-ghost user-menu-button" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <span className="user-name">{name}</span>
        <span aria-hidden="true">▾</span>
      </button>
      {open && (
        <div className="user-menu-list" role="menu">
          {isSupervisor && (
            <button role="menuitem" onClick={pick(onVisitors)}>
              Visitor stats
              <span className="muted small">Anonymous site traffic</span>
            </button>
          )}
          <button role="menuitem" onClick={pick(onAlerts)}>
            Settings
            <span className="muted small">{isSupervisor ? 'Simulated flights and email alerts' : 'Simulated flights and email alerts (view only)'}</span>
          </button>
          <button role="menuitem" onClick={pick(onSignOut)}>Sign out</button>
        </div>
      )}
    </div>
  );
}
