import { severityHex } from './format';

/**
 * A symbol for each severity level, so the levels can be told apart without relying on colour:
 * 5 critical (bolt), 4 high (!), 3 moderate (chevron up), 2 low (minus), 1 informational (i). Drawn in a 24×24 box.
 */
const GLYPH: Record<number, string> = {
  5: '<path d="M13.5 2 4.5 13.5H11L9.8 22l9.2-12h-6.6z" fill="currentColor"/>',
  4: '<rect x="10.2" y="3.5" width="3.6" height="11.5" rx="1.8" fill="currentColor"/><circle cx="12" cy="19.3" r="2.1" fill="currentColor"/>',
  3: '<path d="M5 15.5 12 8.5l7 7" fill="none" stroke="currentColor" stroke-width="3.6" stroke-linecap="round" stroke-linejoin="round"/>',
  2: '<path d="M5.5 12h13" stroke="currentColor" stroke-width="3.6" stroke-linecap="round"/>',
  1: '<circle cx="12" cy="5.2" r="2.1" fill="currentColor"/><rect x="10.2" y="9.5" width="3.6" height="11" rx="1.8" fill="currentColor"/>',
};

const level = (sev: number) => Math.min(5, Math.max(1, Math.round(sev)));

/** The symbol for a severity level as inline SVG markup, sized in px. */
export const severityGlyph = (sev: number, size: number) =>
  `<svg viewBox="0 0 24 24" width="${size}" height="${size}" aria-hidden="true">${GLYPH[level(sev)]}</svg>`;

/** Ink for the symbol: dark on the yellow of level 3, white on the rest. */
export const severityInk = (sev: number) => (level(sev) === 3 ? '#1a1405' : '#fff');

/** Marker diameter in px: worse findings are bigger. */
export const severityMarkerSize = (sev: number) => 16 + level(sev) * 3;

/** A finding's map marker: a severity-coloured disc with its symbol, ringed white, or blue when selected. */
export const severityMarkerHtml = (sev: number, picked: boolean) => {
  const size = severityMarkerSize(sev);
  return `<div class="finding-marker${picked ? ' finding-picked' : ''}" style="width:${size}px;height:${size}px;background:${severityHex(sev)};color:${severityInk(sev)}">`
    + `${severityGlyph(sev, Math.round(size * 0.58))}</div>`;
};
