import { useState } from 'react';
import { useMe } from './api';
import { FindingDrawer } from './components/FindingDrawer';
import { FindingsView } from './components/FindingsView';
import { MissionsView } from './components/MissionsView';
import { OpsView } from './components/OpsView';
import { StatsBar } from './components/StatsBar';
import { Toast, type ToastMessage } from './components/Toast';
import { Logo, useSession } from './session';

type Tab = 'ops' | 'findings' | 'missions';

export function App() {
  const session = useSession();
  const me = useMe();
  const [tab, setTab] = useState<Tab>(() => (location.hash.slice(1) as Tab) || 'ops');
  const [findingId, setFindingId] = useState<string | null>(null);
  const [toast, setToast] = useState<ToastMessage | null>(null);

  const go = (t: Tab) => {
    setTab(t);
    history.replaceState(null, '', `#${t}`);
  };

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <Logo />
          <span>GridSafety</span>
        </div>
        <nav className="tabs" aria-label="Sections">
          {([['ops', 'Operations'], ['findings', 'Findings'], ['missions', 'Missions']] as const).map(([key, label]) => (
            <button key={key} className={`tab ${tab === key ? 'tab-active' : ''}`} onClick={() => go(key)} aria-current={tab === key}>
              {label}
            </button>
          ))}
        </nav>
        <div className="topbar-right">
          {session.devMode && session.setDevRole ? (
            <label className="role-switch" title="Dev mode: switch role to see RBAC in action">
              <span className="pill pill-warn">dev</span>
              <select value={session.devRole} onChange={(e) => session.setDevRole!(e.target.value as 'Inspector' | 'Supervisor')}>
                <option>Supervisor</option>
                <option>Inspector</option>
              </select>
            </label>
          ) : (
            me.data?.isSupervisor && <span className="pill pill-accent">Supervisor</span>
          )}
          <span className="muted user-name">{session.displayName}</span>
          <button className="btn btn-ghost" onClick={session.signOut}>Sign out</button>
        </div>
      </header>

      <main className="content">
        <StatsBar onOpenFindings={() => go('findings')} />
        {tab === 'ops' && <OpsView onOpenFinding={setFindingId} />}
        {tab === 'findings' && <FindingsView onOpen={setFindingId} />}
        {tab === 'missions' && <MissionsView onToast={setToast} onShowOnMap={() => go('ops')} />}
      </main>

      {findingId && (
        <FindingDrawer id={findingId} isSupervisor={Boolean(me.data?.isSupervisor)} onClose={() => setFindingId(null)} onToast={setToast} />
      )}
      <Toast message={toast} onDone={() => setToast(null)} />
    </div>
  );
}
