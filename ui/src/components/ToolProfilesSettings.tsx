import { useEffect, useState, type DragEvent } from "react";
import { Lock, Star } from "lucide-react";
import { rpc } from "../lib/bridge";
import { isProfileFile } from "../lib/toolFormLogic";

interface ToolWithProfiles {
  name: string;
  title: string;
  profiles: string[];
  default: string | null;
}

interface ProfileList {
  tool: string;
  default: string | null;
  lastUsed: string | null;
  profiles: { name: string; locked: boolean; isDefault: boolean; updated?: string | null; inputs: number }[];
  added?: string;
  note?: string | null;
}

/**
 * Options > Tools > Tool profiles (Tools/Profiles/ProfilesBridge.cs): every tool's saved settings, for people who use the
 * PropertyManager page (the panel's form has the same actions in its Profile row): which one the tool opens with,
 * rename, duplicate, delete, export and import. Older add-ins without profiles show nothing.
 */
export function ToolProfilesSettings() {
  const [tools, setTools] = useState<ToolWithProfiles[] | null>(null);
  const [tool, setTool] = useState("");
  const [list, setList] = useState<ProfileList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<{ name: string; to: string } | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  useEffect(() => {
    void rpc<ToolWithProfiles[] | null>("toolProfiles.tools")
      .then((all) => {
        const usable = all ?? [];
        setTools(usable);
        setTool((usable.find((t) => t.profiles.length > 0) ?? usable[0])?.name ?? "");
      })
      .catch(() => setTools([]));
  }, []);

  useEffect(() => {
    if (!tool) return;
    setList(null);
    setError(null);
    setNote(null);
    void rpc<ProfileList>("toolProfiles.list", { tool })
      .then(setList)
      .catch((e: Error) => setError(e.message));
  }, [tool]);

  if (!tools || tools.length === 0) return null;

  /** An action that answers with the new list (null: cancelled in a dialog). */
  const act = async (method: string, params: Record<string, unknown>, done?: string) => {
    setError(null);
    setNote(null);
    try {
      const r = await rpc<ProfileList | null>(method, { tool, ...params });
      if (r) {
        setList(r);
        setNote(r.added ? `Added ${r.added}.${r.note ? ` ${r.note}` : ""}` : done ?? null);
      }
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  };

  const onDrop = (e: DragEvent) => {
    const file = [...e.dataTransfer.files].find((f) => isProfileFile(f.name));
    if (!file) return;
    e.preventDefault();
    void file.text().then((content) => act("toolProfiles.import", { content }));
  };

  return (
    <div className="mt-2 flex flex-col gap-1 border-t border-line pt-1.5" onDragOver={(e) => e.dataTransfer.types.includes("Files") && e.preventDefault()} onDrop={onDrop}>
      <div className="text-[11.5px] text-text-2">Tool profiles: saved settings of a tool's inputs</div>
      <select value={tool} onChange={(e) => setTool(e.target.value)} className="sw-input h-[22px] min-w-0 px-1 text-[12px]" aria-label="Tool">
        {tools.map((t) => (
          <option key={t.name} value={t.name}>
            {t.title}
            {t.profiles.length ? ` (${t.profiles.length})` : ""}
          </option>
        ))}
      </select>
      {list && list.profiles.length === 0 && <div className="text-[11px] text-text-3">No profiles yet. Save one from the tool's inputs, or import a file.</div>}
      {list?.profiles.map((p) => (
        <div key={p.name} className="flex flex-col gap-1 rounded-[2px] px-1 py-0.5 hover:bg-card-2">
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              className={p.isDefault ? "text-accent" : "text-text-3 hover:text-text"}
              title={p.isDefault ? "The tool opens with this profile. Click to stop." : "Open the tool with this profile"}
              onClick={() => void act("toolProfiles.setDefault", { name: p.isDefault ? null : p.name }, p.isDefault ? "The tool opens without a profile now." : `The tool opens with ${p.name} now.`)}
              aria-pressed={p.isDefault}
            >
              <Star size={12} fill={p.isDefault ? "currentColor" : "none"} />
            </button>
            <span className="min-w-0 flex-1 truncate text-[12px] text-text" title={`${p.inputs} inputs`}>
              {p.name}
              {p.name === list.lastUsed && <span className="text-text-3"> - last used</span>}
            </span>
            {p.locked && (
              <span className="text-text-3" title="Comes with the tool: duplicate it to change it">
                <Lock size={11} />
              </span>
            )}
            {!p.locked && (
              <button type="button" className="text-[11px] text-text-3 hover:text-text" onClick={() => setRenaming({ name: p.name, to: p.name })}>
                Rename
              </button>
            )}
            <button type="button" className="text-[11px] text-text-3 hover:text-text" onClick={() => void act("toolProfiles.duplicate", { name: p.name }, "Saved a copy.")}>
              Duplicate
            </button>
            <button
              type="button"
              className="text-[11px] text-text-3 hover:text-text"
              onClick={() =>
                void rpc<{ path: string } | null>("toolProfiles.export", { tool, name: p.name })
                  .then((r) => r && setNote(`Exported to ${r.path}.`))
                  .catch((e: Error) => setError(e.message))
              }
            >
              Export
            </button>
            {!p.locked && (
              <button type="button" className="text-[11px] text-text-3 hover:text-err" onClick={() => setDeleting(p.name)}>
                Delete
              </button>
            )}
          </div>
          {renaming?.name === p.name && (
            <form
              className="flex items-center gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                void act("toolProfiles.rename", { name: p.name, newName: renaming.to }, "Renamed.").then(() => setRenaming(null));
              }}
            >
              <input autoFocus value={renaming.to} onChange={(e) => setRenaming({ ...renaming, to: e.target.value })} className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]" aria-label="New name" />
              <button type="submit" className="sw-btn" disabled={!renaming.to.trim()}>
                Rename
              </button>
              <button type="button" className="sw-btn" onClick={() => setRenaming(null)}>
                Cancel
              </button>
            </form>
          )}
          {deleting === p.name && (
            <div className="flex items-center gap-1 text-[11.5px] text-text-2">
              <span className="min-w-0 flex-1">Delete {p.name}?</span>
              <button type="button" className="sw-btn" onClick={() => void act("toolProfiles.delete", { name: p.name }, `Deleted ${p.name}.`).then(() => setDeleting(null))}>
                Delete
              </button>
              <button type="button" className="sw-btn" onClick={() => setDeleting(null)}>
                Keep it
              </button>
            </div>
          )}
        </div>
      ))}
      <div className="flex flex-wrap items-center gap-1">
        <button type="button" className="sw-btn" onClick={() => void act("toolProfiles.import", {})} title="Add a profile file someone shared (or drop it here)">
          Import…
        </button>
        <button type="button" className="sw-btn" onClick={() => void rpc("toolProfiles.openFolder", { tool }).catch((e: Error) => setError(e.message))}>
          Open folder
        </button>
      </div>
      {error ? <div className="text-[11px] text-err">{error}</div> : note && <div className="text-[11px] text-text-3">{note}</div>}
    </div>
  );
}
