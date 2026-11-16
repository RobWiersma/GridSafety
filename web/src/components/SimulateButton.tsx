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

  return (
    <button
      className={className}
      disabled={!isSupervisor || takingOff}
      title={isSupervisor ? `Fly this mission now with a simulated ${mission.droneId} (about 2–4 minutes)` : 'Only supervisors can launch simulated flights'}
      onClick={() =>
        simulate.mutate(mission.id, {
          onSuccess: () => {
            onToast({ kind: 'success', text: `${label} is taking off with a simulated drone` });
            onLaunched?.();
          },
          onError: (e) => onToast({ kind: 'error', text: e.message }),
        })}
    >
      {isSupervisor ? '▶ ' : '🔒 '}{takingOff ? 'Taking off…' : 'Simulate flight'}
    </button>
  );
}
