/**
 * Text size (Ctrl+ / Ctrl- / Ctrl+0, Ctrl+wheel). The conversation scales fully; the header and composer scale
 * half as much, so controls stay compact while the text people read grows.
 */
export const ZOOM_STEPS = [0.8, 0.9, 1, 1.1, 1.2, 1.35, 1.5, 1.75, 2];

export function stepZoom(current: number, dir: 1 | -1): number {
  const i = ZOOM_STEPS.findIndex((z) => z >= current - 0.001);
  const at = i < 0 ? ZOOM_STEPS.length - 1 : i;
  const exact = Math.abs(ZOOM_STEPS[at] - current) < 0.001;
  const next = dir > 0 ? (exact ? at + 1 : at) : at - 1;
  return ZOOM_STEPS[Math.max(0, Math.min(ZOOM_STEPS.length - 1, next))];
}

/** Sets the CSS variables the .zoom-content / .zoom-chrome classes read. */
export function applyZoom(zoom: number) {
  const z = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  const root = document.documentElement.style;
  root.setProperty("--zoom-content", String(z));
  root.setProperty("--zoom-chrome", String(1 + (z - 1) * 0.5));
}

/** Which zoom action a key press asks for, if any. */
export function zoomKey(e: KeyboardEvent): "in" | "out" | "reset" | null {
  if (!(e.ctrlKey || e.metaKey) || e.altKey) return null;
  if (e.key === "+" || e.key === "=" || e.code === "NumpadAdd") return "in";
  if (e.key === "-" || e.key === "_" || e.code === "NumpadSubtract") return "out";
  if (e.key === "0" || e.code === "Numpad0") return "reset";
  return null;
}
