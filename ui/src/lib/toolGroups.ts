import type { UserToolInfo } from "./types";

/** A row of the Workflows menu: `run` shows a Run button (built-ins and workflows); user tools get their settings. */
export interface PaletteRow {
  tool: UserToolInfo;
  run: boolean;
}

export interface PaletteSection {
  title: string;
  rows: PaletteRow[];
}

/**
 * The Workflows menu's sections: one per category (built-ins first, then the user's - matched trimmed and
 * case-insensitively, shown with the first spelling seen), then the uncategorised ones exactly as before categories:
 * the user's workflows, the built-in tools, the user's ribbon tools. Mirrors RibbonLayout.Boxes in the add-in.
 */
export function paletteSections(all: UserToolInfo[]): { categories: PaletteSection[]; workflows: UserToolInfo[]; builtIn: UserToolInfo[]; tools: UserToolInfo[] } {
  const byKey = new Map<string, PaletteSection>();
  const categories: PaletteSection[] = [];
  const add = (t: UserToolInfo, run: boolean) => {
    const title = (t.category ?? "").replace(/\s+/g, " ").trim();
    const key = title.toLowerCase();
    let section = byKey.get(key);
    if (!section) {
      section = { title, rows: [] };
      byKey.set(key, section);
      categories.push(section);
    }
    section.rows.push({ tool: t, run });
  };
  const has = (t: UserToolInfo) => !!t.category?.trim();
  for (const t of all) if (t.builtIn && has(t)) add(t, true);
  for (const t of all) if (!t.builtIn && has(t)) add(t, t.kind !== "tool");
  return {
    categories,
    workflows: all.filter((t) => !t.builtIn && !has(t) && t.kind !== "tool"),
    builtIn: all.filter((t) => t.builtIn && !has(t)),
    tools: all.filter((t) => !t.builtIn && !has(t) && t.kind === "tool"),
  };
}

/** The categories in use (first spelling of each), for suggestions when the user moves a tool. */
export function categoryNames(all: UserToolInfo[]): string[] {
  return paletteSections(all).categories.map((c) => c.title);
}
