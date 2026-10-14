import type { SwThemeInfo, Theme } from "./types";

/**
 * Palette engine. In "Match SolidWorks" mode the panel takes its base colour from SolidWorks' own
 * PropertyManager/FeatureManager background (read through the API) and derives every other tone from it,
 * so it blends in with the Light, Medium Light, Medium and Dark interface themes and custom backgrounds.
 */

// SolidWorks interface themes (swInterfaceBrightnessTheme_e).
export const SW_THEME_NAMES: Record<number, string> = { 0: "Light", 1: "Medium", 2: "Dark", 3: "Medium Light", 4: "3DEXPERIENCE" };

// Fallback bases measured from SolidWorks 2026 (Dark: #494949 FeatureManager/PropertyManager background).
const BASE_DARK = "#494949";
const BASE_LIGHT = "#F0F0F0";

type Rgb = [number, number, number];

function parse(hex: string): Rgb {
  const h = hex.replace("#", "");
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

function toHex([r, g, b]: Rgb): string {
  const c = (n: number) => Math.round(Math.max(0, Math.min(255, n))).toString(16).padStart(2, "0");
  return `#${c(r)}${c(g)}${c(b)}`;
}

function mix(a: string, b: string, t: number): string {
  const x = parse(a);
  const y = parse(b);
  return toHex([x[0] + (y[0] - x[0]) * t, x[1] + (y[1] - x[1]) * t, x[2] + (y[2] - x[2]) * t]);
}

const lighten = (c: string, t: number) => mix(c, "#ffffff", t);
const darken = (c: string, t: number) => mix(c, "#000000", t);

function luminance(hex: string): number {
  const [r, g, b] = parse(hex).map((v) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function isHex(c: unknown): c is string {
  return typeof c === "string" && /^#[0-9a-f]{6}$/i.test(c);
}

export function darkPalette(base: string): Record<string, string> {
  const accent = "#4C9DE3";
  return {
    bg: base,
    panel: darken(base, 0.1),
    card: lighten(base, 0.05),
    "card-2": darken(base, 0.06),
    line: darken(base, 0.36),
    "line-strong": darken(base, 0.52),
    text: "#EEEEEE",
    "text-2": "#C6C6C6",
    "text-3": "#9D9D9D",
    accent,
    "accent-hover": lighten(accent, 0.15),
    "accent-soft": mix(base, accent, 0.24),
    "accent-text": "#FFFFFF",
    "accent-top": lighten(accent, 0.18),
    "accent-border": darken(accent, 0.45),
    ok: "#62C774",
    warn: "#EBBE4F",
    err: "#F07272",
    "err-soft": mix(base, "#F07272", 0.16),
    "code-bg": darken(base, 0.24),
    "user-bubble": lighten(base, 0.09),
    "btn-top": lighten(base, 0.16),
    "btn-bot": lighten(base, 0.03),
    "btn-top-hover": lighten(base, 0.24),
    "btn-border": darken(base, 0.48),
    "input-bg": darken(base, 0.26),
    "input-border": darken(base, 0.55),
    "bevel-hi": "rgba(255,255,255,0.13)",
    "bevel-lo": "rgba(0,0,0,0.35)",
    "head-top": lighten(base, 0.1),
    "head-bot": lighten(base, 0.01),
    shadow: "0 1px 2px rgba(0,0,0,0.35)",
  };
}

export function lightPalette(base: string): Record<string, string> {
  const accent = "#1F6FC9";
  return {
    bg: base,
    panel: darken(base, 0.045),
    card: lighten(base, 0.65),
    "card-2": darken(base, 0.025),
    line: darken(base, 0.17),
    "line-strong": darken(base, 0.3),
    text: "#1C1C1C",
    "text-2": "#474747",
    "text-3": "#767676",
    accent,
    "accent-hover": darken(accent, 0.12),
    "accent-soft": mix(lighten(base, 0.5), accent, 0.13),
    "accent-text": "#FFFFFF",
    "accent-top": lighten(accent, 0.22),
    "accent-border": darken(accent, 0.3),
    ok: "#2C9640",
    warn: "#B07A00",
    err: "#C42F2F",
    "err-soft": mix(lighten(base, 0.6), "#C42F2F", 0.09),
    "code-bg": lighten(base, 0.45),
    "user-bubble": mix(lighten(base, 0.55), accent, 0.08),
    "btn-top": "#FFFFFF",
    "btn-bot": darken(base, 0.07),
    "btn-top-hover": "#FFFFFF",
    "btn-border": darken(base, 0.33),
    "input-bg": "#FFFFFF",
    "input-border": darken(base, 0.32),
    "bevel-hi": "rgba(255,255,255,0.95)",
    "bevel-lo": "rgba(0,0,0,0.12)",
    "head-top": lighten(base, 0.55),
    "head-bot": darken(base, 0.035),
    shadow: "0 1px 2px rgba(0,0,0,0.12)",
  };
}

/** Resolve the theme setting to a palette and write it onto :root. */
export function applyTheme(setting: Theme, sw: SwThemeInfo | null | undefined, systemDark: boolean): { dark: boolean; source: string } {
  let base: string;
  let dark: boolean;
  let source: string;

  if (setting === "solidworks" && sw) {
    const swBase = isHex(sw.pmBg) ? sw.pmBg : isHex(sw.treeBg) ? sw.treeBg : null;
    dark = swBase ? luminance(swBase) < 0.2 : sw.theme === 2 || sw.theme === 1;
    base = swBase ?? (dark ? BASE_DARK : BASE_LIGHT);
    source = `SolidWorks ${SW_THEME_NAMES[sw.theme] ?? "theme"}`;
  } else if (setting === "light" || setting === "dark") {
    dark = setting === "dark";
    base = dark ? BASE_DARK : BASE_LIGHT;
    source = setting;
  } else {
    // "system", or "solidworks" before the add-in has reported its theme (browser preview).
    dark = systemDark;
    base = dark ? BASE_DARK : BASE_LIGHT;
    source = "Windows";
  }

  const palette = dark ? darkPalette(base) : lightPalette(base);
  const root = document.documentElement;
  for (const [k, v] of Object.entries(palette)) root.style.setProperty(`--${k}`, v);
  root.dataset.theme = dark ? "dark" : "light";
  root.style.colorScheme = dark ? "dark" : "light";
  return { dark, source };
}
