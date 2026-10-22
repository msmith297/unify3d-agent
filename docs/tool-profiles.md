# Tool profiles: saved settings for a tool's inputs

Written 2026-10-22. Built with plugin creator v2 and standing instructions (the next build).

## 0. What the user asked for

- Save a profile of a tool's inputs (they suggested TOML, "or whatever is best").
- At the top of every tool with inputs: load a profile, then edit fields on top of it.
- At the end: "Save profile", with a name.
- One saved profile can be the default.
- After loading a profile: at the end, "Update profile" or "Save as".
- Improve or add whatever makes sense.

## 1. Decisions

- **Format: JSON**, like `inputs.json`, the remembered last values (`last-inputs\`) and plugin manifests. That means
  no new parser and one format.

  Values are stored as text with their unit, so the file reads like a settings file and converts correctly when the
  document or the field's unit changes:

  ```json
  { "schema": 1, "tool": "design-compression-mold", "name": "Shop A - PU on the 50 t press",
    "created": "...", "updated": "...", "toolVersion": "<TOOL.md updated>",
    "values": { "mold_material": "Aluminium + steel inserts", "mold_grade": "al_6061_t651", "part_grade": "pu-tdi-ether-95a",
                "press_pressure_is": "Gauge reading", "press_pressure": "2000 psi", "press_rated_force": "50 US ton",
                "cycle_life": "10000", "max_lid_mass": { "on": false } } }
  ```

  - Data-driven choices store the row **id** (`al_6061_t651`), not the label, so label edits don't break profiles.
  - Optional fields store their switch (`{ "on": false }`, or `{ "on": true, "value": "23 kg" }`).
- **Where:**
  - your profiles: `%APPDATA%\SwAgent\tools\profiles\<tool>\<profile-slug>.json`;
  - per-PC settings: `profiles\<tool>\_settings.json` (the default profile, the last used one);
  - shipped profiles: a tool can ship read-only ones in its own `profiles\` folder (built-in tools and plugins: "PU shop
    starter", "Rubber shop starter"); they show with a lock.
- **What goes in:**
  - by default every input except document-specific ones (`files`, `selection`), so a profile works on any part;
  - a checkbox at Save, "Include the files and selections", for templates of one job;
  - partial profiles are allowed: a profile holds only the fields chosen at Save (all by default, or "Only the fields I
    changed").

## 2. In the tool form (panel) and the PropertyManager page

**Top: a "Profile" row.** It holds:

- a dropdown with:
  - `(Tool defaults)`;
  - the default profile, starred;
  - your profiles;
  - the shipped ones, locked;
  - `Load from file...`;
- a ⋯ menu: Set as default, Rename, Duplicate, Delete, Export..., Open folder.

Loading:

- Picking a profile applies its values over the tool defaults (stacking is a later idea, not v1).
- Fields the profile set show a small dot; fields edited after loading show a different marker.
- The dropdown reads "Shop A (modified)" once anything differs.

**What the form opens with:**

- the default profile, when one is set;
- otherwise today's behaviour (tool defaults plus remembered last values).

A banner line says "Loaded your default profile: Shop A".

**Bottom, next to OK/Cancel:**

| Situation | Buttons |
|---|---|
| no profile loaded | "Save as profile..." (a name box; "Make default" checkbox; "Include files/selections"; "Only changed fields") |
| a profile loaded and modified | "Update 'Shop A'" and "Save as new..." |
| a shipped (locked) profile loaded and modified | only "Save as new..." |

Saving never runs the tool; OK still does.

**Validation when loading:**

- The tool's inputs may have changed since the profile was saved:
  - fields that no longer exist are listed ("ignored: old_field");
  - a value outside a field's Min/Max, or a choice that no longer exists, keeps the default and is listed.
- The profile is never silently changed.

**The PropertyManager page** gets the same features in native form:

- a "Profile" group at the top: a dropdown, plus Save and Update buttons;
- the ⋯ actions are in the panel's Plugins/Tools screen.

## 3. The agent and other places

- **Tool runs:** `[Run tool: x]` payloads include `profile: "Shop A" (modified: press_pressure)`, so the agent knows
  where values came from. The mold tool records it in `mold.json` and on the setup sheet ("Profile: Shop A").
- **Agent tools:**
  - `list_tool_profiles(tool)`;
  - `save_tool_profile(tool, name, values | from_last_run, make_default?)`, for "save these as my shop defaults";
  - `run_tool(tool, profile?)` / `/run <tool> --profile "Shop A"`.

  The tool index line lists a tool's profile names.
- **Standing instructions:** "always use Shop A for molds" sets the default profile, not a memory note.
- **Forks:** `fork_tool` copies the source's profiles into the fork.
- **Sharing:** export a profile as a file, and import one by dropping the `.json` on the panel. Plugins can ship
  profiles in a tool's `profiles\` folder.
- **Storage:** honours `SWAGENT_DATA_DIR`; writes use `AtomicFile.Replace`.

## 4. Acceptance (headless where possible)

- Save, load, update and save-as round trips on the real mold `inputs.json`:
  - units convert (a profile saved in psi loads in an MMGS document as psi, with the field's unit choice);
  - grade ids survive label changes;
  - optional switches restore.
- The default profile loads when the form opens; the "(modified)" state and the dots are right.
- An outdated profile (a missing field, an out-of-range value) loads with the right warnings.
- Locked shipped profiles can't be updated.
- The payload carries the profile name and the modified fields.
