import { useMe, useSimulateMission } from '../api';
import { useSession } from '../session';
import type { Mission } from '../types';
import type { ToastMessage } from './Toast';

/** "Fly it with a simulated drone" for a planned mission. Supervisors only, like demo flights; hidden for guests. */
export function SimulateButton({ mission, onToast, onLaunched, className = 'btn btn-secondary' }: {
  mission: Mission;
  onToast(t: ToastMessage): void;
  onLaunched?(): void;
  className?: string;
}) {
  const me = useMe();
  const { guest } = useSession();
  const simulate = useSimulateMission();
  if (guest || mission.status !== 'Planned') return null;

  const isSupervisor = Boolean(me.data?.isSupervisor);
  const takingOff = simulate.isPending || Boolean(mission.simulatedAt);
  const label = mission.name ?? mission.droneId;
  const outing = mission.outingFlights && mission.outingFlights > 1 ? mission.outingFlights : 0;

  return (
    <button
      className={className}
      disabled={!isSupervisor || takingOff}
      title={isSupervisor ? (outing ? `Fly this outing's planned flights now, back to back, with a simulated ${mission.droneId}`
        : `Fly this mission now with a simulated ${mission.droneId} (about 2–4 minutes)`) : 'Only supervisors can launch simulated flights'}
      onClick={() =>
        simulate.mutate(mission.id, {
          onSuccess: () => {
            onToast({ kind: 'success', text: outing ? `The outing is taking off: ${label} first, then the rest back to back` : `${label} is taking off with a simulated drone` });
            onLaunched?.();
          },
          onError: (e) => onToast({ kind: 'error', text: e.message }),
        })}
    >
      {isSupervisor ? '▶ ' : '🔒 '}{takingOff ? (mission.simulatedAt && outing ? 'Queued in outing' : 'Taking off…') : outing ? 'Simulate outing' : 'Simulate flight'}
    </button>
  );
}
