import type { SelectionAsk, SelectionKind } from "./types";

/**
 * ask_selection: the agent asks the user to pick entities in SolidWorks mid-run. On Done the panel snapshots the
 * selection, keeps the requested kinds, numbers them as [S#] references (continuing the chat's numbering) and
 * answers with one line per entity in the context block's format. Pure, so it is easy to check.
 */

/** One entity as the add-in's selection.snapshot returns it (SelectionTools.Snapshot). */
export interface SnapshotItem {
  type: string;
  label: string;
  detail: string;
  ref: string | null;
}

const FEATURE_TYPES = new Set(["feature", "plane", "axis", "sketch", "datumpoints", "coordsys", "refcurves", "refsurfaces", "helix"]);

/** The kind of a selected entity from the add-in's type name ("Face", "Edge", "Solidbodies", "Plane", ...). */
export function selectionKind(type: string): SelectionKind | null {
  const t = (type ?? "").trim().toLowerCase();
  if (t === "face" || t === "faces") return "face";
  if (t === "edge" || t === "edges") return "edge";
  if (t === "vertex" || t === "vertices") return "vertex";
  if (t === "solidbodies" || t === "surfacebodies" || t === "body" || t === "bodies") return "body";
  if (t === "component" || t === "components") return "component";
  if (FEATURE_TYPES.has(t)) return "feature";
  return null;
}

/** Whether an entity counts for this request (no types: anything does). */
export function isAllowed(type: string, ask: Pick<SelectionAsk, "types">): boolean {
  if (!ask.types?.length) return true;
  const kind = selectionKind(type);
  return kind !== null && ask.types.includes(kind);
}

/**
 * The next [S#] number in a chat: past the highest one stored (and at least the count), so a new reference never
 * reuses an id an earlier message already gave to another entity.
 */
export function refBase(selRefs: Record<string, string> | undefined): number {
  const keys = Object.keys(selRefs ?? {});
  let max = keys.length;
  for (const k of keys) {
    const n = /^S(\d+)$/.exec(k);
    if (n) max = Math.max(max, Number(n[1]));
  }
  return max;
}

const KIND_NAMES: Record<SelectionKind, [string, string]> = {
  face: ["face", "faces"],
  edge: ["edge", "edges"],
  vertex: ["vertex", "vertices"],
  body: ["body", "bodies"],
  feature: ["feature", "features"],
  component: ["component", "components"],
};

/** "faces or edges" for the card and the answer. */
export function kindsText(types: SelectionKind[] | undefined, plural = true): string {
  if (!types?.length) return plural ? "entities" : "entity";
  const names = types.map((k) => KIND_NAMES[k]?.[plural ? 1 : 0] ?? k);
  return names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} or ${names[names.length - 1]}`;
}

/** What the card says about the count so far ("3 faces selected", "select at least 2"), and whether Done is allowed. */
export function selectionStatus(count: number, ask: Pick<SelectionAsk, "types" | "min" | "max">): { text: string; ok: boolean } {
  const min = Math.max(0, ask.min ?? 0);
  const what = count === 1 ? kindsText(ask.types, false) : kindsText(ask.types);
  const text = `${count} ${what} selected`;
  if (count < min) return { text: `${text} - select at least ${min}`, ok: false };
  if (ask.max !== undefined && ask.max >= 0 && count > ask.max) return { text: `${text} - at most ${ask.max} were asked for`, ok: true };
  return { text, ok: true };
}

const line = (it: SnapshotItem) => `${it.type}${it.label ? ` of ${it.label}` : ""}${it.detail ? ` - ${it.detail}` : ""}`;

/**
 * The tool result for Done (or Skip): the entities of the requested kinds as [S#] lines numbered from base + 1, the
 * refs to merge into the chat, and the user's note (typed or spoken while the card was open).
 */
export function selectionAnswer(
  items: SnapshotItem[] | null,
  ask: SelectionAsk,
  base: number,
  opts: { skipped?: boolean; note?: string } = {},
): { text: string; refs: Record<string, string>; count: number } {
  const note = opts.note?.trim() ? `\nTheir note: "${opts.note.trim()}"` : "";
  if (opts.skipped) {
    return { text: `The user skipped this (pressed Skip) without picking anything. Carry on with your own choice and say which you used.${note}`, refs: {}, count: 0 };
  }
  if (items === null) {
    return { text: `The user pressed Done, but the selection couldn't be read (no active document?). Ask again or carry on without it.${note}`, refs: {}, count: 0 };
  }
  const refs: Record<string, string> = {};
  const kept = items.filter((it) => isAllowed(it.type, ask));
  const other = items.filter((it) => !isAllowed(it.type, ask));
  const lines: string[] = [];
  let n = base;
  let addressable = 0;
  for (const it of kept) {
    if (it.ref) {
      const id = `S${++n}`;
      refs[id] = it.ref;
      addressable++;
      lines.push(`[${id}] ${line(it)}`);
    } else {
      // No id for it: an id without a stored reference could later be given to another entity.
      lines.push(`- ${line(it)} (no persistent id: not addressable with Ref)`);
    }
  }
  const out: string[] = [];
  if (kept.length === 0) {
    out.push(`The user pressed Done with no ${kindsText(ask.types)} selected.`);
  } else {
    out.push(`The user pressed Done with ${kept.length} ${kept.length === 1 ? kindsText(ask.types, false) : kindsText(ask.types)} selected${addressable ? ' - use the [S#] references with Ref("S#") and select_refs' : ""}:`);
    out.push(...lines);
  }
  if (ask.min !== undefined && kept.length < ask.min) out.push(`That is fewer than the ${ask.min} you asked for.`);
  if (ask.max !== undefined && ask.max >= 0 && kept.length > ask.max) out.push(`That is more than the ${ask.max} you asked for.`);
  if (other.length) {
    const counts = new Map<string, number>();
    for (const it of other) counts.set(it.type, (counts.get(it.type) ?? 0) + 1);
    out.push(`Not counted (not ${kindsText(ask.types)}): ${[...counts].map(([t, c]) => `${c} ${t}`).join(", ")}.`);
  }
  return { text: out.join("\n") + note, refs, count: addressable };
}
