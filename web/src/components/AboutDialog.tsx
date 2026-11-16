import { useEffect, useRef } from 'react';
import { Logo } from '../session';

const REPO = 'https://github.com/RobWiersma/GridSafety';

const STEPS = [
  ['🛰️', 'Inspect', 'Drones fly the lines and visually check every pole and tower, streaming live position and photos of anything wrong.'],
  ['🖥️', 'Monitor', 'One live map shows every drone, every line and every flight-safety alert as it happens.'],
  ['🚦', 'Triage', 'Each problem lands in a work queue ranked by severity. Urgent ones carry a deadline and escalate if nobody acts.'],
  ['👷', 'Assign', 'Supervisors send each issue to the right field crew: tree trimmers for vegetation, linemen for damaged hardware.'],
  ['🧰', 'Manage', 'The drone fleet, flight crews and ground crews are managed in one place, so only airworthy drones and on-duty crews get the work.'],
] as const;

const PROBLEMS = ['Vegetation growing into lines', 'Cracked or missing insulators', 'Damaged crossarms and conductors', 'Overheating connections'];

const STACK = ['.NET 10', 'AWS Lambda', 'IoT Core (MQTT)', 'DynamoDB', 'EventBridge', 'Cognito', 'CloudFront', 'AWS CDK', 'React 19', 'TypeScript', 'Leaflet', 'GitHub Actions'];

/** Project info: what GridSafety is for, how the workflow runs, and what is simulated. */
export function AboutDialog({ onClose }: { onClose(): void }) {
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <div className="modal about" role="dialog" aria-modal="true" aria-labelledby="about-title">
        <header className="about-header">
          <Logo size={44} />
          <div>
            <h2 id="about-title">About GridSafety</h2>
            <p className="muted small">Inspect, monitor, triage and dispatch for safer power lines</p>
          </div>
          <button ref={closeRef} className="btn btn-ghost icon-btn" onClick={onClose} aria-label="Close">✕</button>
        </header>

        <div className="about-body">
          <p className="about-lead">
            GridSafety is an <strong>operations dashboard for keeping power lines clear and inspected</strong>. Drone
            inspections, live monitoring, triage and crew assignments all happen in one place, so problems get found and
            fixed before they cause a fire or an outage.
          </p>

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
            <strong>This is a demo.</strong> The power lines on the map are real, from public OpenStreetMap data, and the aerial
            photos of each structure are real public-domain USDA NAIP imagery. The drones, the problems they find and the crews
            are simulated. No utility's internal data is used.
          </section>

          <section>
            <h3>Built with</h3>
            <ul className="about-chips about-chips-muted">
              {STACK.map((t) => <li key={t}>{t}</li>)}
            </ul>
          </section>

          <p className="muted small">
            Browse as a guest, or sign in to plan missions, fly the demo drone and assign findings to crews.
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
