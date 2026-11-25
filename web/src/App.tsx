import { lazy, Suspense, useEffect, useState, type ComponentType } from 'react';
import { useMe } from './api';
import { AboutDialog } from './components/AboutDialog';
import { CrewsPanel } from './components/CrewsPanel';
import { OpsView } from './components/OpsView';
import { reportKey } from './lib/reportKey';
import { StatsBar } from './components/StatsBar';
import { UserMenu } from './components/UserMenu';
import { Toast, type ToastMessage } from './components/Toast';
import { recordVisit } from './lib/visits';
import { Logo, useSession } from './session';

// Only the Operations map loads up front; every other screen downloads the first time it's opened.
const named = <K extends string, M extends Record<K, ComponentType<any>>>(load: () => Promise<M>, key: K) =>
  lazy(() => load().then((m) => ({ default: m[key] })));
const AlertsView = named(() => import('./components/AlertsView'), 'AlertsView');
const SimulationSettingsPanel = named(() => import('./components/SimulationSettingsPanel'), 'SimulationSettingsPanel');
const FindingDrawer = named(() => import('./components/FindingDrawer'), 'FindingDrawer');
const FindingsView = named(() => import('./components/FindingsView'), 'FindingsView');
const MissionDetail = named(() => import('./components/MissionDetail'), 'MissionDetail');
const MissionsView = named(() => import('./components/MissionsView'), 'MissionsView');
const ScheduleView = named(() => import('./components/ScheduleView'), 'ScheduleView');
const ReportsView = named(() => import('./components/ReportsView'), 'ReportsView');
const ResourcesView = named(() => import('./components/ResourcesView'), 'ResourcesView');
const VisitorsView = named(() => import('./components/VisitorsView'), 'VisitorsView');
const Loading = () => <div className="center-pad" aria-label="Loading"><div className="spinner" /></div>;

type Tab = 'ops' | 'findings' | 'schedule' | 'missions' | 'resources' | 'reports' | 'visitors' | 'settings';
type Route = { tab: Tab; mission: string | null; report?: string | null };

/**
 * #ops, #findings, #missions, #resources, #reports, #visitors, #settings (#alerts still works), #mission/<id> for one
 * mission's overview, or #reports/<kind>/<id> for one report (linkable, works with back/forward).
 */
function parseHash(): Route {
  const h = location.hash.slice(1);
  if (h.startsWith('mission/')) return { tab: 'missions', mission: decodeURIComponent(h.slice('mission/'.length)) };
  if (h === 'reports' || h.startsWith('reports/')) return { tab: 'reports', mission: null, report: h.slice('reports/'.length) || null };
  if (h === 'alerts') return { tab: 'settings', mission: null }; // old link to the email alerts page
  return { tab: h === 'findings' || h === 'schedule' || h === 'missions' || h === 'resources' || h === 'visitors' || h === 'settings' ? h : 'ops', mission: null };
}

/**
 * Opening the site always starts on Operations, even when the address still has the tab you were on last. Links to one
 * mission or one report (other than the default All activity) still open it, so they can be shared.
 */
function startRoute(): Route {
  const r = parseHash();
  if (r.mission || (r.report && r.report !== 'activity')) return r;
  if (location.hash) history.replaceState(null, '', `${location.pathname}${location.search}`);
  return { tab: 'ops', mission: null };
}

export function App() {
  const session = useSession();
  const me = useMe();
  const [route, setRoute] = useState<Route>(startRoute);
  const { tab, mission: missionId } = route;
  useEffect(() => {
    const onHash = () => setRoute(parseHash());
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);
  const [findingId, setFindingId] = useState<string | null>(null);
  const [toast, setToast] = useState<ToastMessage | null>(null);
  const [aboutOpen, setAboutOpen] = useState<false | 'about' | 'tour'>(false);
  const [findingsFilter, setFindingsFilter] = useState('open');

  const isSupervisor = Boolean(me.data?.isSupervisor);
  const tabs: [Tab, string][] = [['ops', 'Operations'], ['findings', 'Findings'], ['schedule', 'Schedule'], ['missions', 'Missions'], ['resources', 'Resources'], ['reports', 'Reports']];

  const page = missionId ? 'mission' : tab;
  useEffect(() => recordVisit(page, !session.guest), [page, session.guest]);
  // On a phone the tab row scrolls sideways: keep the current tab in view.
  useEffect(() => { document.querySelector('.tabs .tab-active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }, [tab]);

  const go = (t: Tab) => {
    setRoute({ tab: t, mission: null });
    history.replaceState(null, '', `#${t}`);
  };
  const openMission = (id: string) => {
    setRoute({ tab: 'missions', mission: id });
    history.pushState(null, '', `#mission/${encodeURIComponent(id)}`);
    window.scrollTo(0, 0);
  };
  const openReport = (key: string | null) => {
    setRoute({ tab: 'reports', mission: null, report: key });
    history.pushState(null, '', key ? `#reports/${key}` : '#reports');
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
          <button className="btn btn-ghost" onClick={() => setAboutOpen('about')}>About</button>
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
            <UserMenu name={session.displayName} isSupervisor={isSupervisor} onVisitors={() => go('visitors')} onAlerts={() => go('settings')} onSignOut={session.signOut} />
          )}
        </div>
      </header>

      <main className="content">
        {session.guest && (
          <div className="banner banner-info guest-banner">
            <span>You're viewing the live demo as a guest, so everything is read-only. Signed-in users plan missions, fly the demo drone and triage findings.</span>
            <button className="btn btn-secondary guest-tour" onClick={() => setAboutOpen('tour')}>▶ Watch the 90-second tour</button>
          </div>
        )}
        <StatsBar onOpenFindings={() => go('findings')} />
        <Suspense fallback={<Loading />}>
        {tab === 'ops' && (
          <OpsView onOpenFinding={setFindingId} onToast={setToast} onOpenMission={openMission}
            onOpenTriageQueue={() => { setFindingsFilter('New'); go('findings'); window.scrollTo(0, 0); }} />
        )}
        {tab === 'findings' && (
          <>
            <FindingsView key={findingsFilter} onOpen={setFindingId} initialFilter={findingsFilter} />
            <CrewsPanel />
          </>
        )}
        {tab === 'schedule' && <ScheduleView onOpenFinding={setFindingId} onToast={setToast} />}
        {tab === 'resources' && <ResourcesView onToast={setToast} onOpenMission={openMission} onOpenReport={(t) => { openReport(reportKey(t)); window.scrollTo(0, 0); }} />}
        {tab === 'reports' && <ReportsView selected={route.report ?? null} onSelect={openReport} />}
        {tab === 'settings' && (session.guest ? <div className="banner banner-info">Sign in to see settings.</div> : (
          <div className="settings-page">
            <SimulationSettingsPanel onToast={setToast} />
            <AlertsView onToast={setToast} />
          </div>
        ))}
        {tab === 'visitors' && (isSupervisor ? <VisitorsView /> : <div className="banner banner-info">Visitor stats are for supervisors.</div>)}
        {tab === 'missions' && !missionId && <MissionsView onToast={setToast} onShowOnMap={() => go('ops')} onOpenMission={openMission} />}
        {tab === 'missions' && missionId && (
          <MissionDetail missionId={missionId} onBack={() => go('missions')} onOpenFinding={setFindingId} onToast={setToast} />
        )}
        </Suspense>
      </main>

      <Suspense fallback={null}>
        {findingId && (
          <FindingDrawer id={findingId} isSupervisor={Boolean(me.data?.isSupervisor)} onClose={() => setFindingId(null)} onToast={setToast} />
        )}
      </Suspense>
      {aboutOpen && <AboutDialog tour={aboutOpen === 'tour'} onClose={() => setAboutOpen(false)} />}
      <Toast message={toast} onDone={() => setToast(null)} />
    </div>
  );
}
