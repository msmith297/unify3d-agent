/**
 * The panel's tool form (Tools/ToolFormBridge.cs): the state the add-in sends, and the small pure helpers the card uses to
 * show it. The form logic itself (cascades, fills, units, validation) stays in C#; nothing here decides a value.
 * No imports, so lib.check.mjs runs it under node.
 */

export type ToolFormInputType = "selection" | "number" | "integer" | "checkbox" | "choice" | "options" | "text" | "files" | "size" | "info";

export interface ToolFormInput {
  id: string;
  type: ToolFormInputType;
  label: string;
  tip?: string | null;
  visible: boolean;
  /** False while an Optional input is switched off. */
  enabled: boolean;
  required: boolean;
  /** Why this input holds OK back, or a value out of range. */
  error?: string | null;
  /** number/integer: the value in `unit`; size: one per box; checkbox: bool; choice/options/text: the text. */
  value?: number | number[] | boolean | string | null;
  /** The unit the value is shown in ("psi", "mm", "°C", "%", "" for plain numbers). */
  unit?: string;
  /** Units the user may switch between (sent back as { unit }). */
  unitChoices?: string[];
  min?: number;
  max?: number;
  step?: number;
  optional?: boolean;
  on?: boolean;
  /** A value another choice filled in (in `unit`, or the entry), and whether the user changed it since. */
  table?: number | string;
  edited?: boolean;
  notApplicable?: boolean;
  dims?: number;
  choices?: string[];
  /** info: the rendered line. */
  text?: string;
  files?: { name: string; path: string }[];
  accept?: string[];
  multiple?: boolean;
  items?: { type: string; label?: string | null; detail?: string | null }[];
  filter?: string[];
  /** While a profile is loaded: "set" (the profile set this input) or "edited" (changed after loading it). */
  mark?: "set" | "edited" | null;
}

/** The form's Profile row (Tools/Profiles/ProfileSession.cs State). */
export interface ToolFormProfile {
  /** The profile loaded, or null (tool defaults, or the values remembered from the last run). */
  name: string | null;
  /** It came with the tool: it can't be updated, only saved under a new name. */
  locked: boolean;
  /** Something differs from the loaded profile. */
  modified: boolean;
  isDefault: boolean;
  default: string | null;
  profiles: { name: string; locked: boolean; isDefault: boolean }[];
  /** One line about the last action ("Loaded your default profile: Shop A."). */
  note?: string | null;
  /** Why the last action was refused. */
  error?: string | null;
  /** What loading ignored or kept at the default (an outdated profile). */
  warnings?: string[] | null;
}

/** An action of the Profile row, sent with toolForm.profile (formId is added by the store). */
export type ToolFormProfileAction =
  | { action: "load"; name: string | null }
  | { action: "save"; name: string; makeDefault?: boolean; includeDocument?: boolean; onlyChanged?: boolean }
  | { action: "update" }
  | { action: "setDefault"; name: string | null }
  | { action: "rename"; name: string; newName: string }
  | { action: "duplicate"; name: string }
  | { action: "delete"; name: string }
  | { action: "import"; content: string }
  | { action: "export"; name: string }
  | { action: "loadFile" }
  | { action: "openFolder" };

export interface ToolFormGroup {
  id: string;
  title: string;
  expanded: boolean;
  inputs: ToolFormInput[];
}

export interface ToolFormState {
  formId: string;
  tool: string;
  title: string;
  description?: string | null;
  message?: string | null;
  imperial: boolean;
  groups: ToolFormGroup[];
  okEnabled: boolean;
  okReason?: string | null;
  okField?: string | null;
  /** Tools with an agent: the intelligence it runs at (the page's "Intelligence" dropdown). */
  agent?: { tier: string; tiers: string[] } | null;
  /** Tools with preview.cs: compiled yet, following every change (else on the Preview button), and what to say. */
  preview?: { available: boolean; live: boolean; message?: string | null } | null;
  /** The Profile row (older add-ins: none). */
  profile?: ToolFormProfile | null;
}

/** A change sent with toolForm.change (formId is added by the store). */
export type ToolFormChange =
  | { id: string; value: unknown; index?: number }
  | { id: string; unit: string }
  | { id: string; on: boolean }
  | { id: string; remove: number }
  | { id: string; clear: true }
  | { tier: string }
  | { preview: true };

/** A number for the box: up to 6 significant digits, no exponent, no group separators (as Units.Format does). */
export function formatNumber(v: number | null | undefined, significant = 6): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return "";
  if (v === 0) return "0";
  const magnitude = Math.floor(Math.log10(Math.abs(v)));
  const decimals = Math.max(0, Math.min(12, significant - 1 - magnitude));
  const r = Number(v.toFixed(decimals));
  if (r === 0) return "0";
  const s = r.toFixed(decimals);
  return s.includes(".") ? s.replace(/0+$/, "").replace(/\.$/, "") : s;
}

let decimalCommaLocale: boolean | undefined;

/** Whether the user's locale writes 1.5 as "1,5" (WebView2 reports the Windows display language). */
export function localeUsesDecimalComma(): boolean {
  if (decimalCommaLocale === undefined) {
    try {
      decimalCommaLocale = new Intl.NumberFormat((globalThis as { navigator?: { language?: string } }).navigator?.language).format(1.5).includes(",");
    } catch {
      decimalCommaLocale = false;
    }
  }
  return decimalCommaLocale;
}

/**
 * What the user typed, as a number: "1.5", "1,5" (decimal comma), " 2 000 "; null when it isn't one. With a point as
 * well, commas are group separators. "2,000" (one comma, three digits after it) is 2000 unless the locale writes
 * decimal commas, and "1,234,567" is always grouped: a US user typing 2,000 psi must not get 2 psi.
 */
export function parseNumber(text: string, decimalComma = localeUsesDecimalComma()): number | null {
  let t = text.trim().replace(/\s+/g, "");
  if (!t) return null;
  if (t.includes(".")) t = t.replace(/,/g, "");
  else if (/^[-+]?\d{1,3}(,\d{3})+$/.test(t) && (!decimalComma || /,.*,/.test(t))) t = t.replace(/,/g, "");
  else t = t.replace(",", ".");
  if (!/^[-+]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?$/i.test(t)) return null;
  const n = Number(t);
  return Number.isFinite(n) ? n : null;
}

/** One step up or down from the box's value, kept in range and free of float noise (0.1 + 0.2). */
export function stepNumber(value: number, step: number | undefined, dir: 1 | -1, min?: number, max?: number): number {
  const s = step && step > 0 ? step : 1;
  let next = Number((value + dir * s).toPrecision(12));
  if (min !== undefined && next < min) next = min;
  if (max !== undefined && next > max) next = max;
  return next;
}

/** The groups that have something to show (a group whose inputs are all hidden by ShowWhen or n/a is left out). */
export function visibleGroups(form: ToolFormState): ToolFormGroup[] {
  return form.groups.filter((g) => g.inputs.some((i) => i.visible));
}

/** "Face · Boss-Extrude1" - a picked entity's chip. */
export function chipText(item: { type: string; label?: string | null }): string {
  return item.label ? `${item.type} · ${item.label}` : item.type;
}

/** "faces", "an edge", "faces or edges": what a selection input takes. */
export function takesText(filter: string[] | undefined, multiple = true): string {
  const kinds = [...new Set((filter ?? []).map((k) => k.trim().toLowerCase().replace(/_/g, " ")).filter(Boolean))];
  if (kinds.length === 0) return multiple ? "entities" : "an entity";
  const names = kinds.map((k) => (multiple ? (k.endsWith("ex") ? k.slice(0, -2) + "ices" : k + "s") : (/^[aeiou]/.test(k) ? "an " : "a ") + k));
  return names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} or ${names[names.length - 1]}`;
}

/** "PDFs, images, drawings" - what a files input takes. */
export function acceptText(accept: string[] | undefined): string {
  const names: Record<string, string> = { image: "images", pdf: "PDFs", model: "3D models", drawing: "drawings" };
  const list = (accept ?? []).map((a) => names[a.toLowerCase()] ?? a);
  return list.length ? list.join(", ") : "files";
}

export const TIER_LABELS: Record<string, string> = { fast: "Fast", balanced: "Balanced", high: "High", max: "Max" };

/** The profile dropdown's caption: "Shop A (modified)", "(Tool defaults)". */
export function profileCaption(p: ToolFormProfile): string {
  return p.name ? `${p.name}${p.modified ? " (modified)" : ""}` : "(Tool defaults)";
}

/**
 * The save buttons next to OK (docs/tool-profiles.md): no profile -> "Save as profile..."; the user's profile changed ->
 * "Update" and "Save as new..."; a locked one changed, or any one unchanged -> only "Save as new...".
 */
export function profileButtons(p: ToolFormProfile): { update: boolean; saveLabel: string } {
  if (!p.name) return { update: false, saveLabel: "Save as profile…" };
  return { update: p.modified && !p.locked, saveLabel: "Save as new…" };
}

/** Whether a dropped file looks like a tool profile (by name; the add-in checks the content). */
export function isProfileFile(name: string): boolean {
  return /\.json$/i.test(name.trim());
}

/** A filled-in value the user changed: what the table had, for the "Table: ..." hint. */
export function tableHint(input: ToolFormInput): string | null {
  if (!input.edited || input.table === undefined || input.table === null) return null;
  const v = typeof input.table === "number" ? formatNumber(input.table) : String(input.table);
  const unit = typeof input.table === "number" && input.unit ? (input.unit === "%" ? " %" : ` ${input.unit}`) : "";
  return `Table: ${v}${unit}`;
}

/**
 * "--profile "Shop A"" (or --profile=Shop, --profile 'Shop A') in a /run or /<tool> command: the profile, and the rest of
 * the arguments without it.
 */
export function splitProfileArg(args: string): { profile: string | null; rest: string } {
  const m = /(^|\s)--profile(?:=|\s+)(?:"([^"]*)"|'([^']*)'|“([^”]*)”|(\S+))/i.exec(args ?? "");
  if (!m) return { profile: null, rest: (args ?? "").trim() };
  const profile = (m[2] ?? m[3] ?? m[4] ?? m[5] ?? "").trim();
  const rest = (args.slice(0, m.index) + m[1] + args.slice(m.index + m[0].length)).replace(/\s+/g, " ").trim();
  return { profile: profile || null, rest };
}

/** The line a tool run's prompt carries about its profile: "Profile: Shop A (modified: press_pressure)". */
export function profileLine(profile: { name?: string | null; modified?: string[] | null } | null | undefined): string | null {
  if (!profile?.name) return null;
  const modified = (profile.modified ?? []).filter(Boolean);
  return `Profile: ${profile.name}${modified.length ? ` (modified: ${modified.join(", ")})` : ""}`;
}
