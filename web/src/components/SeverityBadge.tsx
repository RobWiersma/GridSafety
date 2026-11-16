import { severityColor } from '../lib/format';

export function SeverityBadge({ severity }: { severity: number }) {
  return (
    <span className="sev-badge" style={{ background: severityColor(severity) }} aria-label={`Severity ${severity}`}>
      {severity}
    </span>
  );
}
