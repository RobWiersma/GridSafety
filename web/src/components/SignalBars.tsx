/** Same thresholds as the server's flight rules (FlightRules.WeakSignalDbm / LinkCriticalDbm). */
export const WEAK_SIGNAL_DBM = -85;
export const LINK_CRITICAL_DBM = -95;

/** 0–4 bars for a control-link RSSI in dBm. */
export function signalBars(rssi: number) {
  if (rssi > -65) return 4;
  if (rssi > -75) return 3;
  if (rssi > WEAK_SIGNAL_DBM) return 2;
  if (rssi > LINK_CRITICAL_DBM) return 1;
  return 0;
}

/** Phone-style signal bars with the dBm reading; colored by link health. */
export function SignalBars({ rssi, showValue = true }: { rssi?: number | null; showValue?: boolean }) {
  if (rssi == null) return null;
  const bars = signalBars(rssi);
  const tone = rssi <= LINK_CRITICAL_DBM ? 'text-danger' : rssi <= WEAK_SIGNAL_DBM ? 'text-warn' : '';
  return (
    <span className={`signal ${tone}`} title={`Control link ${Math.round(rssi)} dBm`}>
      <svg viewBox="0 0 16 12" width="16" height="12" aria-hidden="true">
        {[0, 1, 2, 3].map((i) => (
          <rect key={i} x={i * 4} y={9 - i * 3} width="3" height={3 + i * 3} rx="0.6"
            fill="currentColor" opacity={i < bars ? 1 : 0.25} />
        ))}
      </svg>
      {showValue && <span>{Math.round(rssi)} dBm</span>}
    </span>
  );
}
