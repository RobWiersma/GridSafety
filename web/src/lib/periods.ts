/**
 * Day, week and month buckets for reports. The API reports Pacific calendar days ("2026-11-19"), the service area's
 * local time; weeks start on Monday, months on the 1st. Dates are handled as plain calendar dates (UTC midnight) so the
 * viewer's own time zone never shifts them.
 */
export type PeriodSize = 'day' | 'week' | 'month';

export const OPS_TIME_ZONE = 'America/Los_Angeles';

const DAY_MS = 86_400_000;
const parse = (day: string) => Date.parse(`${day}T00:00:00Z`);
const fmt = (ms: number) => new Date(ms).toISOString().slice(0, 10);

/** Today's Pacific date at the instant `now`. */
export function opsToday(now: string | number | Date): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: OPS_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(now));
}

/** The first day of the period `day` falls in (itself, that week's Monday, or the 1st of the month). */
export function periodStart(day: string, size: PeriodSize): string {
  if (size === 'day') return day;
  if (size === 'month') return `${day.slice(0, 7)}-01`;
  const ms = parse(day);
  return fmt(ms - ((new Date(ms).getUTCDay() + 6) % 7) * DAY_MS);
}

/** The `count` periods ending with the one `today` is in, oldest first. */
export function lastPeriods(today: string, size: PeriodSize, count: number): string[] {
  const end = parse(periodStart(today, size));
  if (size === 'month') {
    const d = new Date(end);
    return Array.from({ length: count }, (_, i) => fmt(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() - (count - 1 - i), 1)));
  }
  const step = size === 'day' ? DAY_MS : 7 * DAY_MS;
  return Array.from({ length: count }, (_, i) => fmt(end - (count - 1 - i) * step));
}

/** "Thu, Nov 19" for a day, "Nov 16 – 22" for a week, "November 2026" for a month ("Nov" on a chart axis). */
export function periodLabel(start: string, size: PeriodSize, short = false): string {
  const d = new Date(parse(start));
  const md = (x: Date) => x.toLocaleDateString([], { timeZone: 'UTC', month: 'short', day: 'numeric' });
  if (size === 'month') return d.toLocaleDateString([], { timeZone: 'UTC', month: short ? 'short' : 'long', year: short && d.getUTCMonth() !== 0 ? undefined : 'numeric' });
  if (size === 'day') return short ? md(d) : d.toLocaleDateString([], { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric' });
  if (short) return md(d);
  const end = new Date(parse(start) + 6 * DAY_MS);
  return end.getUTCMonth() === d.getUTCMonth() ? `${md(d)} – ${end.getUTCDate()}` : `${md(d)} – ${md(end)}`;
}

/** Sums each day's numbers into its period; periods with nothing in them are left out. */
export function bucket<T extends { day: string }>(days: T[], size: PeriodSize, values: ((d: T) => number)[]): Map<string, number[]> {
  const out = new Map<string, number[]>();
  for (const d of days) {
    const key = periodStart(d.day, size);
    const sums = out.get(key) ?? values.map(() => 0);
    values.forEach((v, i) => (sums[i] += v(d)));
    out.set(key, sums);
  }
  return out;
}

/** Bar with 4px rounded top corners, square at the baseline. */
export function roundedTop(x: number, y: number, w: number, h: number, r: number) {
  const rr = Math.min(r, h);
  return `M${x} ${y + h}V${y + rr}Q${x} ${y} ${x + rr} ${y}H${x + w - rr}Q${x + w} ${y} ${x + w} ${y + rr}V${y + h}Z`;
}
