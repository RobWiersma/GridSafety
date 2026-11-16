import { useCrews } from '../api';
import { FINDING_TYPE_LABEL } from '../lib/format';

/** The field crews work orders go to, with how many each has open right now. */
export function CrewsPanel() {
  const crews = useCrews();
  if (!crews.data) return null;

  return (
    <section className="panel">
      <h2>Field crews</h2>
      <div className="crew-grid">
        {crews.data.map(({ crew, openWorkOrders }) => (
          <article key={crew.id} className="crew-card">
            <header>
              <div>
                <h3>{crew.name}</h3>
                <p className="muted small">{crew.specialty}</p>
              </div>
              <span className={`crew-load ${openWorkOrders > 0 ? 'crew-load-busy' : ''}`} title="Open work orders">
                {openWorkOrders} open
              </span>
            </header>
            <ul className="crew-members">
              {crew.members.map((m) => (
                <li key={m.name}>
                  <span className="crew-avatar" aria-hidden="true">{m.name.split(' ').map((p) => p[0]).join('')}</span>
                  <span>{m.name}<span className="muted small"> · {m.role}</span></span>
                </li>
              ))}
            </ul>
            <p className="muted small">Handles: {crew.handles.map((t) => FINDING_TYPE_LABEL[t]).join(', ')}</p>
          </article>
        ))}
      </div>
    </section>
  );
}
