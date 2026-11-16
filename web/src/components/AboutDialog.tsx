import { useEffect, useRef } from 'react';
import { Logo } from '../session';

const REPO = 'https://github.com/RobWiersma/GridSafety';

const STACK = [
  ['Backend', '.NET 10, ASP.NET Core Minimal APIs on AWS Lambda behind API Gateway (HTTP API, JWT auth, throttled)'],
  ['Drone ingest', 'AWS IoT Core (MQTT) → rules → Lambda; telemetry and findings in DynamoDB'],
  ['Workflows', 'EventBridge + Scheduler for triage SLAs and escalation, SQS, SNS alerts, S3 mission reports'],
  ['Frontend', 'React 19, TypeScript, TanStack Query and Leaflet, served from S3 through CloudFront'],
  ['Auth', 'Amazon Cognito with Inspector and Supervisor roles; signed-out visitors get a read-only guest view'],
  ['Infrastructure', 'AWS CDK in C#; GitHub Actions runs CI and deploys every green push to main'],
] as const;

/** Project info: what GridSafety is, what is real vs simulated, and how it is built. */
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
            <p className="muted small">Drone power-line inspection platform on AWS</p>
          </div>
          <button ref={closeRef} className="btn btn-ghost icon-btn" onClick={onClose} aria-label="Close">✕</button>
        </header>

        <div className="about-body">
          <p>
            Drones fly distribution and transmission circuits, stream telemetry over MQTT and report defects such as vegetation
            encroachment, damaged insulators and thermal hotspots. GridSafety turns that stream into a live ops map, flight-safety
            alerts, a triage queue with SLA escalation, crew work orders and an inspection report for every mission.
          </p>

          <h3>What's real and what's simulated</h3>
          <ul className="about-list">
            <li><strong>Real:</strong> the cloud architecture, APIs, auth, workflows and the power-line map, drawn from public OpenStreetMap data.</li>
            <li><strong>Simulated:</strong> the drones, their telemetry, the defects they find and the defect images. Crews are fictional.</li>
            <li>No utility's internal data is used anywhere.</li>
          </ul>

          <h3>How it's built</h3>
          <dl className="about-stack">
            {STACK.map(([k, v]) => (
              <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
            ))}
          </dl>

          <h3>Try it</h3>
          <p className="small">
            Guests can browse everything read-only. Signed-in users can plan missions, launch the demo drone from the Missions
            tab and triage findings, assigning them to field crews.
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
