import { useCrews, useResources } from '../api';
import type { FieldDispatch } from '../types';
import { PHASE_LABEL, involves, phaseAt } from '../lib/field';
import { FINDING_TYPE_LABEL } from '../lib/format';

/** "Driving to site", "Repairing"… for someone out on a job; nothing when they're free. */
export function OutBadge({ jobs }: { jobs: FieldDispatch[] }) {
  const now = Date.now();
  const live = jobs.find((j) => !['Queued', 'Back', 'MovedOn'].includes(phaseAt(j, now)));
  if (!live) return null;
  const queued = jobs.filter((j) => phaseAt(j, now) === 'Queued').length;
  return (
    <span className="out-badge" title={`Out until about ${new Date(Math.max(...jobs.map((j) => Date.parse(j.returnsAt)))).toLocaleTimeString()}`}>
      {PHASE_LABEL[phaseAt(live, now)]}{queued ? ` · ${queued} more` : ''}
    </span>
  );
}

/** The field crews work orders go to: where each technician departs from, what they can fix, and open work per crew. */
export function CrewsPanel() {
  const crews = useCrews();
  const resources = useResources();
  if (!crews.data) return null;
  const depotName = (id?: string) => resources.data?.depots.find((d) => d.id === id)?.name;

  return (
    <section className="panel">
      <h2>Field crews</h2>
      <div className="crew-grid">
        {crews.data.map(({ crew, openWorkOrders, inField = [] }) => (
          <article key={crew.id} className={`crew-card ${crew.status === 'OffDuty' ? 'crew-off' : ''}`}>
            <header>
              <div>
                <h3>{crew.name}</h3>
                <p className="muted small">
                  {crew.specialty}
                  {depotName(crew.depotId) && ` · departs ${depotName(crew.depotId)}`}
                  {crew.status === 'OffDuty' && ' · off duty'}
                </p>
              </div>
              <span className={`crew-load ${openWorkOrders > 0 ? 'crew-load-busy' : ''}`} title="Open work orders">
                {openWorkOrders} open
              </span>
            </header>
            <ul className="crew-members">
              {crew.members.map((m) => (
                <li key={m.name}>
                  <span className="crew-avatar" aria-hidden="true">{m.name.split(' ').map((p) => p[0]).join('')}</span>
                  <span>
                    {m.name}<span className="muted small"> · {m.role}</span>
                    <OutBadge jobs={inField.filter((d) => involves(d, crew.id, m.name))} />
                    {m.depotId && m.depotId !== crew.depotId && depotName(m.depotId) && (
                      <span className="muted small"> · departs {depotName(m.depotId)}</span>
                    )}
                    <span className="skill-tags">
                      {(m.skills ?? crew.handles).map((t) => <span key={t} className="skill-tag">{FINDING_TYPE_LABEL[t]}</span>)}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          </article>
        ))}
      </div>
    </section>
  );
}
