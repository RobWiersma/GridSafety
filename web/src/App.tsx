import { useEffect, useState } from 'react';
import { useMe } from './api';
import { AboutDialog } from './components/AboutDialog';
import { AlertsView } from './components/AlertsView';
import { FindingDrawer } from './components/FindingDrawer';
import { CrewsPanel } from './components/CrewsPanel';
import { FindingsView } from './components/FindingsView';
import { MissionDetail } from './components/MissionDetail';
import { MissionsView } from './components/MissionsView';
import { OpsView } from './components/OpsView';
import { ResourcesView } from './components/ResourcesView';
import { StatsBar } from './components/StatsBar';
import { UserMenu } from './components/UserMenu';
import { VisitorsView } from './components/VisitorsView';
import { Toast, type ToastMessage } from './components/Toast';
import { recordVisit } from './lib/visits';
import { Logo, useSession } from './session';

type Tab = 'ops' | 'findings' | 'missions' | 'resources' | 'visitors' | 'alerts';

/** #ops, #findings, #missions, #resources, #visitors, #alerts, or #mission/<id> for one mission's overview (linkable, works with back/forward). */
function parseHash(): { tab: Tab; mission: string | null } {
  const h = location.hash.slice(1);
  if (h.startsWith('mission/')) return { tab: 'missions', mission: decodeURIComponent(h.slice('mission/'.length)) };
  return { tab: h === 'findings' || h === 'missions' || h === 'resources' || h === 'visitors' || h === 'alerts' ? h : 'ops', mission: null };
}

export function App() {
  const session = useSession();
  const me = useMe();
  const [route, setRoute] = useState(parseHash);
  const { tab, mission: missionId } = route;
  useEffect(() => {
    const onHash = () => setRoute(parseHash());
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);
  const [findingId, setFindingId] = useState<string | null>(null);
  const [toast, setToast] = useState<ToastMessage | null>(null);
  const [aboutOpen, setAboutOpen] = useState(false);
  const [findingsFilter, setFindingsFilter] = useState('open');

  const isSupervisor = Boolean(me.data?.isSupervisor);
  const tabs: [Tab, string][] = [['ops', 'Operations'], ['findings', 'Findings'], ['missions', 'Missions'], ['resources', 'Resources']];

  const page = missionId ? 'mission' : tab;
  useEffect(() => recordVisit(page, !session.guest), [page, session.guest]);

  const go = (t: Tab) => {
    setRoute({ tab: t, mission: null });
    history.replaceState(null, '', `#${t}`);
  };
  const openMission = (id: string) => {
    setRoute({ tab: 'missions', mission: id });
    history.pushState(null, '', `#mission/${encodeURIComponent(id)}`);
    window.scrollTo(0, 0);
  };

  return (
    <div className="app">
      <header className="topbar">
        <a className="brand" href="#ops" aria-label="GridSafety home: Operations"
          onClick={(e) => { e.preventDefault(); go('ops'); window.scrollTo(0, 0); }}>
          <Logo />
          <span>GridSafety</span>
        </a>
        <nav className="tabs" aria-label="Sections">
          {tabs.map(([key, label]) => (
            <button key={key} className={`tab ${tab === key ? 'tab-active' : ''}`} onClick={() => go(key)} aria-current={tab === key}>
              {label}
            </button>
          ))}
        </nav>
        <div className="topbar-right">
          <button className="btn btn-ghost" onClick={() => setAboutOpen(true)}>About</button>
          {session.devMode && session.setDevRole ? (
            <label className="role-switch" title="Dev mode: switch role to see RBAC in action">
              <span className="pill pill-warn">dev</span>
              <select value={session.devRole} onChange={(e) => session.setDevRole!(e.target.value as 'Inspector' | 'Supervisor' | 'Guest')}>
                <option>Supervisor</option>
                <option>Inspector</option>
                <option>Guest</option>
              </select>
            </label>
          ) : session.guest ? (
            <span className="pill">Read-only</span>
          ) : (
            me.data?.isSupervisor && <span className="pill pill-accent">Supervisor</span>
          )}
          {session.guest ? (
            <>
              <span className="muted user-name">{session.displayName}</span>
              {session.signIn && <button className="btn btn-primary" onClick={session.signIn}>Sign in</button>}
            </>
          ) : (
            <UserMenu name={session.displayName} isSupervisor={isSupervisor} onVisitors={() => go('visitors')} onAlerts={() => go('alerts')} onSignOut={session.signOut} />
          )}
        </div>
      </header>

      <main className="content">
        {session.guest && (
          <div className="banner banner-info guest-banner">
            You're viewing the live demo as a guest, so everything is read-only. Signed-in users plan missions, fly the demo drone and triage findings.
          </div>
        )}
        <StatsBar onOpenFindings={() => go('findings')} />
        {tab === 'ops' && (
          <OpsView onOpenFinding={setFindingId} onOpenMission={openMission}
            onOpenTriageQueue={() => { setFindingsFilter('New'); go('findings'); window.scrollTo(0, 0); }} />
        )}
        {tab === 'findings' && (
          <>
            <FindingsView key={findingsFilter} onOpen={setFindingId} initialFilter={findingsFilter} />
            <CrewsPanel />
          </>
        )}
        {tab === 'resources' && <ResourcesView onToast={setToast} onOpenMission={openMission} />}
        {tab === 'alerts' && (session.guest ? <div className="banner banner-info">Sign in to see email alert settings.</div> : <AlertsView onToast={setToast} />)}
        {tab === 'visitors' && (isSupervisor ? <VisitorsView /> : <div className="banner banner-info">Visitor stats are for supervisors.</div>)}
        {tab === 'missions' && !missionId && <MissionsView onToast={setToast} onShowOnMap={() => go('ops')} onOpenMission={openMission} />}
        {tab === 'missions' && missionId && (
          <MissionDetail missionId={missionId} onBack={() => go('missions')} onOpenFinding={setFindingId} onToast={setToast} />
        )}
      </main>

      {findingId && (
        <FindingDrawer id={findingId} isSupervisor={Boolean(me.data?.isSupervisor)} onClose={() => setFindingId(null)} onToast={setToast} />
      )}
      {aboutOpen && <AboutDialog onClose={() => setAboutOpen(false)} />}
      <Toast message={toast} onDone={() => setToast(null)} />
    </div>
  );
}
