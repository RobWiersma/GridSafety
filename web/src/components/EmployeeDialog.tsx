import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useSaveEmployee } from '../api';
import { FINDING_TYPE_LABEL, money } from '../lib/format';
import type { CrewMember, Depot, EmployeeProfile, FindingType } from '../types';

export interface EmployeeTarget {
  kind: 'ground-crews' | 'flight-crews';
  crewId: string;
  crewName: string;
  member: CrewMember;
  /** The standard hourly rate for their role. */
  standard: number;
  /** Ground crews: what the crew as a whole handles, for "same as crew" skills. */
  handles?: FindingType[];
}

const PILOT = 'Remote pilot (PIC)';
const OBSERVER = 'Visual observer';
const ALL_TYPES = Object.keys(FINDING_TYPE_LABEL) as FindingType[];

/** When a rate took effect; a rate set before history was kept shows as "From the start". */
const since = (iso: string) =>
  iso.startsWith('0001-') ? 'From the start:' : `From ${new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}:`;

/**
 * Supervisor-only: one employee's crew details (name, role, and for technicians their yard and skills), their employee
 * record (ID, hire date, contact and emergency details, certification, notes) and their own hourly rate.
 */
export function EmployeeDialog({ target, depots, onClose, onDone }: { target: EmployeeTarget; depots: Depot[]; onClose(): void; onDone(text: string): void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => ref.current?.showModal(), []);
  const save = useSaveEmployee();
  const [error, setError] = useState<string | null>(null);
  const { member, standard, kind } = target;
  const ground = kind === 'ground-crews';
  const p: EmployeeProfile = member.profile ?? {};
  const [skills, setSkills] = useState<FindingType[] | undefined>(member.skills ?? undefined);
  const [ownRate, setOwnRate] = useState(member.payRate != null);
  const history = [...(member.payHistory ?? [])].reverse(); // newest first

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const text = (k: string) => String(f.get(k) ?? '').trim();
    const date = (k: string) => text(k) || null;
    setError(null);
    try {
      const name = text('name');
      await save.mutateAsync({
        kind, crewId: target.crewId, member: member.name,
        body: {
          name, role: text('role'),
          skills: ground ? skills ?? null : null,
          depotId: ground ? text('depotId') || null : null,
          payRate: ownRate && text('payRate') ? Number(text('payRate')) : null,
          profile: {
            employeeId: text('employeeId'), hireDate: date('hireDate'), phone: text('phone'), email: text('email'), homeCity: text('homeCity'),
            emergencyContact: text('emergencyContact'), emergencyPhone: text('emergencyPhone'),
            certification: text('certification'), certificationExpires: date('certificationExpires'), notes: text('notes'),
          },
        },
      });
      onDone(`${name} saved`);
      ref.current?.close();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <dialog ref={ref} className="dialog employee-dialog" onClose={onClose} onCancel={onClose}>
      <form onSubmit={submit}>
        <h2>Edit employee · {member.name}</h2>
        <p className="muted small">{target.crewName}</p>

        <fieldset className="resource-fieldset">
          <legend>Job</legend>
          <div className="employee-grid">
            <label>Name<input className="input" name="name" defaultValue={member.name} maxLength={60} required /></label>
            <label>
              Role
              {ground ? <input className="input" name="role" defaultValue={member.role} maxLength={60} required placeholder="e.g. Journeyman lineman" />
                : (
                  <select className="input" name="role" defaultValue={member.role}>
                    <option value={PILOT}>{PILOT}</option>
                    <option value={OBSERVER}>{OBSERVER}</option>
                  </select>
                )}
            </label>
            <label>Employee ID<input className="input" name="employeeId" defaultValue={p.employeeId ?? ''} maxLength={20} placeholder="e.g. E-10442" /></label>
            <label>Hire date<input className="input" name="hireDate" type="date" defaultValue={p.hireDate ?? ''} /></label>
            {ground && (
              <label>
                Departs from
                <select className="input" name="depotId" defaultValue={member.depotId ?? ''}>
                  <option value="">Crew's depot</option>
                  {depots.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </label>
            )}
          </div>
          {ground && (
            <div className="member-skills" role="group" aria-label="Qualified to fix">
              <span className="muted small">Qualified to fix</span>
              <label className="check">
                <input type="checkbox" checked={!skills} onChange={(e) => setSkills(e.target.checked ? undefined : [...(target.handles ?? [])])} />
                Same as crew
              </label>
              {skills && ALL_TYPES.map((t) => (
                <label key={t} className="check">
                  <input type="checkbox" checked={skills.includes(t)}
                    onChange={(e) => setSkills((s) => (e.target.checked ? [...(s ?? []), t] : (s ?? []).filter((x) => x !== t)))} />
                  {FINDING_TYPE_LABEL[t]}
                </label>
              ))}
            </div>
          )}
        </fieldset>

        <fieldset className="resource-fieldset">
          <legend>Contact</legend>
          <div className="employee-grid">
            <label>Phone<input className="input" name="phone" type="tel" defaultValue={p.phone ?? ''} maxLength={25} /></label>
            <label>Email<input className="input" name="email" type="email" defaultValue={p.email ?? ''} maxLength={100} /></label>
            <label>Home city<input className="input" name="homeCity" defaultValue={p.homeCity ?? ''} maxLength={60} /></label>
            <span />
            <label>Emergency contact<input className="input" name="emergencyContact" defaultValue={p.emergencyContact ?? ''} maxLength={60} placeholder="Name and relationship" /></label>
            <label>Emergency phone<input className="input" name="emergencyPhone" type="tel" defaultValue={p.emergencyPhone ?? ''} maxLength={25} /></label>
          </div>
        </fieldset>

        <fieldset className="resource-fieldset">
          <legend>Certification</legend>
          <div className="employee-grid">
            <label>
              Certification
              <input className="input" name="certification" defaultValue={p.certification ?? ''} maxLength={80}
                placeholder={ground ? 'e.g. Qualified electrical worker' : 'e.g. FAA Part 107'} />
            </label>
            <label>Expires<input className="input" name="certificationExpires" type="date" defaultValue={p.certificationExpires ?? ''} /></label>
          </div>
        </fieldset>

        <fieldset className="resource-fieldset">
          <legend>Pay</legend>
          <label className="check">
            <input type="checkbox" checked={!ownRate} onChange={(e) => setOwnRate(!e.target.checked)} />
            Standard rate: {money(standard)} an hour{ground ? ' on a job or driving to it' : ' of flight'}
          </label>
          {ownRate && (
            <label>
              Their own hourly rate ($)
              <input className="input" name="payRate" type="number" min={20} max={400} step={0.5} required defaultValue={member.payRate ?? standard} />
              <span className="muted small">
                {ground ? 'Fully loaded (wages, benefits, overhead). Hours they spend in a truck from now on are costed at it.'
                  : "Their share of the flight crew rate. Flights from now on are costed at the pilot's rate plus their crew's first observer's."}
              </span>
            </label>
          )}
          <p className="muted small">A change applies from when you save it; work already done keeps the rate it was done at.</p>
          {history.length > 0 && (
            <div className="pay-history">
              <span className="muted small">Rate history</span>
              <ul className="small">
                {history.map((c, i) => (
                  <li key={`${c.from}-${i}`}>
                    <span className="muted">{since(c.from)}</span> {c.rate != null ? <strong>{money(c.rate)}/h</strong> : <>standard ({money(standard)}/h)</>}
                    {i === history.length - 1 && <span className="muted"> · current</span>}
                  </li>
                ))}
                {!history[history.length - 1].from.startsWith('0001-') && (
                  <li><span className="muted">Before that:</span> standard ({money(standard)}/h)</li>
                )}
              </ul>
            </div>
          )}
        </fieldset>

        <label>Notes<textarea className="input" name="notes" rows={2} maxLength={500} defaultValue={p.notes ?? ''} /></label>
        <p className="muted small">Visible to signed-in users only; the public demo never shows employee records or pay.</p>

        {error && <div className="banner banner-error">{error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => ref.current?.close()}>Cancel</button>
          <button className="btn btn-primary" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save'}</button>
        </div>
      </form>
    </dialog>
  );
}
