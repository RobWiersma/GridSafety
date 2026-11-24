import { useEffect, useRef } from 'react';
import { Logo } from '../session';

const REPO = 'https://github.com/RobWiersma/GridSafety';

const STEPS = [
  ['🛰️', 'Inspect', 'Drones fly the lines and visually check every pole and tower, streaming live position and photos of anything wrong. A line longer than one 20-minute battery is flown in several flights, one stretch at a time.'],
  ['🖥️', 'Monitor', 'One live map shows every drone, every line and every flight-safety alert as it happens.'],
  ['🚦', 'Triage', 'Each problem lands in a work queue ranked by severity. Urgent ones carry a deadline and escalate if nobody acts.'],
  ['🗓️', 'Schedule', 'Repairs take far longer than inspections, so work backs up. A forward schedule ranks what is waiting by importance and plans each job onto the qualified technician who would finish it soonest, around their shifts, rests and free trucks.'],
  ['🚚', 'Dispatch', 'Each work order goes to someone qualified for that defect: tree trimmers for vegetation, linemen for damaged hardware. Their truck leaves the nearest yard and drives real roads to the site, repairs it and comes back.'],
  ['🧰', 'Manage', 'Drones, battery packs, trucks, yards, flight crews and technicians are managed in one place. Only airworthy drones, fuelled and serviced trucks, and people with hours left in their shift get the work.'],
  ['📊', 'Report', 'Every drone, truck, crew, person and yard has a report by day, week or month, with labor, flight, fuel and maintenance costs. Pay keeps a dated history, so a raise never reprices past work.'],
] as const;

const PROBLEMS = ['Vegetation growing into lines', 'Cracked or missing insulators', 'Damaged crossarms and conductors', 'Overheating connections'];

const STACK = ['.NET 10', 'AWS Lambda', 'IoT Core (MQTT)', 'DynamoDB', 'EventBridge', 'Cognito', 'CloudFront', 'AWS CDK', 'React 19', 'TypeScript', 'TanStack Query', 'Leaflet', 'OpenStreetMap', 'OSRM', 'GitHub Actions'];

/** Project info: what GridSafety is for, how the workflow runs, and what is simulated. */
export function AboutDialog({ onClose, tour = false }: { onClose(): void; /** Opened from "Watch the tour": start the video. */ tour?: boolean }) {
  const closeRef = useRef<HTMLButtonElement>(null);
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    if (tour && videoRef.current) {
      videoRef.current.focus();
      videoRef.current.play().catch(() => { /* autoplay blocked: the controls are there */ });
    } else closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, tour]);

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <div className="modal about" role="dialog" aria-modal="true" aria-labelledby="about-title">
        <header className="about-header">
          <Logo size={44} />
          <div>
            <h2 id="about-title">About GridSafety</h2>
            <p className="muted small">Inspect, monitor, triage, schedule and dispatch for safer power lines</p>
          </div>
          <button ref={closeRef} className="btn btn-ghost icon-btn" onClick={onClose} aria-label="Close">✕</button>
        </header>

        <div className="about-body">
          <p className="about-lead">
            GridSafety is an <strong>operations dashboard for keeping power lines clear and inspected</strong>. Drone
            inspections, live monitoring, triage, scheduling, crew dispatch and reporting all happen in one place, so
            problems get found and fixed before they cause a fire or an outage.
          </p>

          <section>
            <h3>Watch the 90-second tour</h3>
            <video ref={videoRef} className="about-video" src="/tour/gridsafety-tour.mp4" poster="/tour/poster.jpg"
              controls muted playsInline preload={tour ? 'auto' : 'none'}
              aria-label="Walkthrough: a demo drone flight, triaging and assigning a severity 5 finding, the truck driving out, the repair schedule, a flight overview, costs and resources">
              <a href="/tour/gridsafety-tour.mp4">Download the tour (MP4)</a>
            </video>
          </section>

          <section>
            <h3>How it works</h3>
            <ol className="about-steps">
              {STEPS.map(([icon, title, text], i) => (
                <li key={title}>
                  <span className="about-step-icon" aria-hidden="true">{icon}</span>
                  <div>
                    <strong>{i + 1}. {title}</strong>
                    <p>{text}</p>
                  </div>
                </li>
              ))}
            </ol>
          </section>

          <section>
            <h3>What it looks for</h3>
            <ul className="about-chips">
              {PROBLEMS.map((p) => <li key={p}>{p}</li>)}
            </ul>
          </section>

          <section className="about-note">
            <strong>This is a demo.</strong> The power lines on the map are real, from public OpenStreetMap data for Contra Costa,
            Alameda and Solano counties. The aerial photos of each structure are real public-domain USDA NAIP imagery, and trucks
            follow real roads using public OSRM routing. The drones, the problems they find, the crews, pay rates and costs are
            simulated, and field work runs sped up (1 second is 1 minute). No utility's internal data is used.
          </section>

          <section>
            <h3>Built with</h3>
            <ul className="about-chips about-chips-muted">
              {STACK.map((t) => <li key={t}>{t}</li>)}
            </ul>
          </section>

          <p className="muted small">
            Browse as a guest, including the schedule and reports, or sign in to plan missions, fly the demo drone, assign
            findings and dispatch work from the schedule.
          </p>
        </div>

        <footer className="about-footer">
          <span className="muted small">A portfolio project by Rob Wiersma</span>
          <a className="btn btn-primary" href={REPO} target="_blank" rel="noopener noreferrer">Source on GitHub ↗</a>
        </footer>
      </div>
    </>
  );
}
