/**
 * The add-in's tool form (Tools/ToolFormBridge.cs) for the browser mock host: a trimmed copy of the built-in
 * design-compression-mold page (tools/builtin/design-compression-mold/inputs.json) with its data choices cascading, a
 * grade filling the process data, ShowWhen, unit choices, Optional inputs, ranges, picks and OK. Only for designing the
 * card outside SolidWorks; the real logic is ToolForm in C#.
 */
import type { ToolFormGroup, ToolFormInput, ToolFormState } from "./toolFormLogic";

type Emit = (name: string, data: unknown) => void;

interface Def {
  id: string;
  type: ToolFormInput["type"];
  label: string;
  group: string;
  tip?: string;
  choices?: string[];
  /** Quantity for numbers: length (mm), pressure (MPa), temperature (°C), percent, viscosity (cP), mass (kg), none. */
  unit?: string;
  unitChoices?: string[];
  min?: number;
  max?: number;
  optional?: boolean;
  on?: boolean;
  dims?: number;
  accept?: string[];
  multiple?: boolean;
  filter?: string[];
  expanded?: boolean;
  showWhen?: [string, string[]];
  text?: () => string;
}

const ALU = ["6061-T651", "7075-T651", "MIC-6 cast plate", "QC-10", "Alumold 500", "5083"];
const STEEL = ["P20 pre-hard", "4140 pre-hard", "H13 44-50 HRC", "420 SS pre-hard"];
const GRADE_NAMES: Record<string, string> = { "6061-T651": "6061-T6 / T651 rolled plate", "P20 pre-hard": "P20 / 1.2311 pre-hardened", "H13 44-50 HRC": "H13 / 1.2344 hardened 44-50 HRC" };

/** family -> grades with what they fill (null = n/a for the grade: hidden). */
const MATERIALS: Record<string, { grade: string; shrink: number; viscosity: number | null; mooney: number | null; temp: number; process: string }[]> = {
  "PU TDI-ether (MBOCA)": [
    { grade: "TDI-ether 80A", shrink: 1.6, viscosity: 1800, mooney: null, temp: 100, process: "liquid pour, hot cure" },
    { grade: "TDI-ether 90A", shrink: 1.5, viscosity: 1200, mooney: null, temp: 100, process: "liquid pour, hot cure" },
    { grade: "TDI-ether 95A", shrink: 1.4, viscosity: 900, mooney: null, temp: 100, process: "liquid pour, hot cure" },
  ],
  "Epoxy casting": [{ grade: "Epoxy casting, unfilled", shrink: 0.5, viscosity: 3000, mooney: null, temp: 60, process: "liquid pour" }],
  EPDM: [
    { grade: "EPDM sulfur 60A", shrink: 2.5, viscosity: null, mooney: 60, temp: 170, process: "compression of a preform" },
    { grade: "EPDM peroxide 70A", shrink: 2.8, viscosity: null, mooney: 60, temp: 177, process: "compression of a preform" },
  ],
};

/** Display unit -> MPa (pressure) for the unit choices. */
const PRESSURE: Record<string, number> = { psi: 0.006894757293168, bar: 0.1, MPa: 1, "kgf/cm²": 0.0980665 };

const DEFS: Def[] = [
  { id: "part_file", type: "files", label: "Molded part", group: "Molded part", accept: ["model"], multiple: false, tip: "The part to mold (SLDPRT, STEP, IGES, Parasolid)." },
  { id: "drawing", type: "files", label: "Engineering drawing", group: "Molded part", accept: ["pdf", "image", "drawing"], tip: "PDF, image or SolidWorks drawing of the part." },
  { id: "critical_faces", type: "selection", label: "Critical faces (no parting line)", group: "Molded part", filter: ["face"], tip: "Faces no parting line, flash, vent or sprue may touch." },
  { id: "mold_material", type: "options", label: "Mold material", group: "Mold", choices: ["Aluminium + steel inserts", "Tool steel"] },
  { id: "mold_grade", type: "choice", label: "Plate grade", group: "Mold", tip: "Grade of the machined plates." },
  { id: "insert_grade", type: "choice", label: "Insert grade", group: "Mold", showWhen: ["mold_material", ["Aluminium + steel inserts"]] },
  { id: "grade_info", type: "info", label: "", group: "Mold", text: () => `${GRADE_NAMES[str("mold_grade")] ?? str("mold_grade")}: full strength to 100 °C, derated to 150 °C, never above 175 °C` },
  { id: "part_family", type: "choice", label: "Part material", group: "Part material", choices: Object.keys(MATERIALS) },
  { id: "part_grade", type: "choice", label: "Grade", group: "Part material" },
  { id: "material_info", type: "info", label: "", group: "Part material", text: () => `${str("part_grade")}: ${grade().process}, data confidence medium` },
  { id: "shrink_pct", type: "number", unit: "percent", label: "Shrinkage", group: "Process data (from table)", expanded: false, min: 0, max: 10 },
  { id: "viscosity_cp", type: "number", unit: "viscosity", label: "Mixed viscosity", group: "Process data (from table)", min: 1, max: 1e7 },
  { id: "mooney", type: "number", unit: "none", label: "Mooney viscosity", group: "Process data (from table)", min: 1, max: 500 },
  {
    id: "press_pressure_is",
    type: "choice",
    label: "What you set on the press",
    group: "Press",
    choices: ["Gauge reading", "On the part", "Force reading (tons)", "Not sure - ask me"],
  },
  { id: "press_pressure", type: "number", unit: "pressure", unitChoices: ["psi", "bar", "MPa", "kgf/cm²"], label: "Gauge pressure", group: "Press", min: 0.01, max: 100, showWhen: ["press_pressure_is", ["Gauge reading", "Not sure - ask me"]] },
  { id: "part_pressure", type: "number", unit: "pressure", unitChoices: ["psi", "bar", "MPa", "kgf/cm²"], label: "Pressure on the part", group: "Press", min: 0.01, max: 150, showWhen: ["press_pressure_is", ["On the part"]] },
  { id: "press_temp", type: "number", unit: "temperature", label: "Press temperature", group: "Press", min: 0, max: 450, tip: "Platen set point. Filled from the material." },
  { id: "platen_size", type: "size", dims: 2, label: "Heated platen size", group: "Press", optional: true, on: false, min: 50, max: 5000 },
  { id: "daylight", type: "number", unit: "length", label: "Daylight", group: "Press", optional: true, on: false, min: 10, max: 3000 },
  { id: "cavities", type: "integer", label: "Number of cavities", group: "Cavities & limits", min: 1, max: 64 },
  { id: "max_plate_mass", type: "number", unit: "mass", label: "Max weight per machined plate", group: "Cavities & limits", optional: true, on: true, min: 1, max: 5000 },
  { id: "decisions", type: "options", label: "Big decisions", group: "Decisions & notes", expanded: false, choices: ["Ask on big ones", "Ask about all", "Decide everything"] },
  { id: "notes", type: "text", label: "Notes", group: "Decisions & notes", tip: "Anything the agent should know." },
];

const UNIT_SYMBOL: Record<string, string> = { length: "mm", temperature: "°C", percent: "%", viscosity: "cP", mass: "kg", none: "" };

/** The open mock form (one at a time, like the add-in). */
let form: {
  formId: string;
  values: Record<string, any>;
  table: Record<string, number | null>;
  units: Record<string, string>;
  on: Record<string, boolean>;
  items: Record<string, { type: string; label: string; detail: string }[]>;
  files: Record<string, string[]>;
  tier: string;
  message: string | null;
} | null = null;

const str = (id: string): string => String(form?.values[id] ?? "");
const grade = () => (MATERIALS[str("part_family")] ?? []).find((g) => g.grade === str("part_grade")) ?? Object.values(MATERIALS)[0][0];

function open(): ToolFormState {
  form = {
    formId: "mock-" + Math.random().toString(36).slice(2, 10),
    values: {
      mold_material: "Aluminium + steel inserts",
      mold_grade: "6061-T651",
      insert_grade: "P20 pre-hard",
      part_family: "PU TDI-ether (MBOCA)",
      part_grade: "TDI-ether 90A",
      press_pressure_is: "Not sure - ask me",
      // 2000 and 1000 psi, the round values ToolForm snaps these defaults to in psi
      press_pressure: 2000 * PRESSURE.psi,
      part_pressure: 1000 * PRESSURE.psi,
      platen_size: [610, 610],
      daylight: 300,
      cavities: 1,
      max_plate_mass: 23,
      decisions: "Ask on big ones",
      notes: "",
    },
    table: {},
    units: { press_pressure: "psi", part_pressure: "psi" },
    on: { platen_size: false, daylight: false, max_plate_mass: true },
    items: {},
    files: {},
    tier: "max",
    message: null,
  };
  fill();
  return state();
}

/** The grade's values go into the fields it fills (a pick overwrites an edit, as in ToolForm). */
function fill() {
  const g = grade();
  const set = (id: string, v: number | null) => {
    form!.table[id] = v;
    if (v !== null) form!.values[id] = v;
  };
  set("shrink_pct", g.shrink);
  set("viscosity_cp", g.viscosity);
  set("mooney", g.mooney);
  set("press_temp", g.temp);
}

function visible(d: Def): boolean {
  if (d.showWhen && !d.showWhen[1].includes(str(d.showWhen[0]))) return false;
  return !(d.id in form!.table && form!.table[d.id] === null);
}

function choicesOf(d: Def): string[] {
  if (d.id === "mold_grade") return str("mold_material") === "Tool steel" ? STEEL : ALU;
  if (d.id === "insert_grade") return ["P20 pre-hard", "H13 44-50 HRC", "S7 54-56 HRC"];
  if (d.id === "part_grade") return (MATERIALS[str("part_family")] ?? []).map((g) => g.grade);
  return d.choices ?? [];
}

/** A pressure input's value (stored in MPa) in its chosen unit, and back. */
const shown = (d: Def, mpa: number) => (d.unit === "pressure" ? Number((mpa / PRESSURE[form!.units[d.id]]).toPrecision(10)) : mpa);
const stored = (d: Def, v: number) => (d.unit === "pressure" ? v * PRESSURE[form!.units[d.id]] : v);

function errorOf(d: Def): string | null {
  if (d.type !== "number" || d.unit !== "pressure" || !visible(d)) return null;
  const v = form!.values[d.id] as number;
  if ((d.min !== undefined && v < d.min * 0.9995) || (d.max !== undefined && v > d.max * 1.0005))
    return `Enter a value from ${+shown(d, d.min ?? 0).toPrecision(4)} to ${+shown(d, d.max ?? 0).toPrecision(4)} ${form!.units[d.id]}.`;
  return null;
}

function input(d: Def): ToolFormInput {
  const f = form!;
  const o: ToolFormInput = { id: d.id, type: d.type, label: d.label, tip: d.tip ?? null, visible: visible(d), enabled: !d.optional || f.on[d.id], required: false, error: null };
  switch (d.type) {
    case "number":
    case "integer": {
      o.value = shown(d, f.values[d.id] ?? 0);
      o.unit = d.unit === "pressure" ? f.units[d.id] : d.type === "integer" ? "" : UNIT_SYMBOL[d.unit ?? "none"];
      if (d.unitChoices) o.unitChoices = d.unitChoices;
      if (d.min !== undefined) o.min = shown(d, d.min);
      if (d.max !== undefined) o.max = shown(d, d.max);
      o.step = d.unit === "pressure" ? (f.units[d.id] === "psi" ? 10 : 1) : 1;
      const t = f.table[d.id];
      if (t !== undefined && t !== null) {
        o.table = t;
        o.edited = Math.abs(t - (f.values[d.id] ?? 0)) > 1e-9;
      }
      if (t === null) o.notApplicable = true;
      break;
    }
    case "size":
      o.value = f.values[d.id];
      o.unit = "mm";
      o.dims = d.dims;
      o.min = d.min;
      o.max = d.max;
      break;
    case "choice":
    case "options":
      o.value = f.values[d.id] ?? null;
      o.choices = choicesOf(d);
      break;
    case "text":
      o.value = f.values[d.id] ?? "";
      break;
    case "info":
      o.text = d.text?.() ?? "";
      break;
    case "files":
      o.files = (f.files[d.id] ?? []).map((p) => ({ name: p.split(/[\\/]/).pop() ?? p, path: p }));
      o.accept = d.accept;
      o.multiple = d.multiple !== false;
      break;
    case "selection":
      o.items = f.items[d.id] ?? [];
      o.filter = d.filter;
      o.multiple = d.multiple !== false;
      break;
  }
  if (d.optional) {
    o.optional = true;
    o.on = f.on[d.id];
  }
  o.error = errorOf(d);
  return o;
}

function state(): ToolFormState {
  const f = form!;
  const groups: ToolFormGroup[] = [];
  for (const d of DEFS) {
    let g = groups.find((x) => x.id === d.group);
    if (!g) groups.push((g = { id: d.group, title: d.group, expanded: d.expanded !== false, inputs: [] }));
    g.inputs.push(input(d));
  }
  const blocking = DEFS.find((d) => errorOf(d));
  return {
    formId: f.formId,
    tool: "design-compression-mold",
    title: "Design compression mold for plastics and resins",
    description: "Designs a heat-press compression mold for a PU, resin, rubber or thermoset part, with every plate, insert and pad as a body in this part.",
    message: f.message,
    imperial: false,
    groups,
    okEnabled: !blocking,
    okReason: blocking ? `${blocking.label}: ${errorOf(blocking)}` : null,
    okField: blocking?.id ?? null,
    agent: { tier: f.tier, tiers: ["fast", "balanced", "high", "max"] },
    preview: null,
  };
}

function change(p: any) {
  const f = form!;
  f.message = null;
  if (p.tier) {
    f.tier = p.tier;
    return;
  }
  const d = DEFS.find((x) => x.id === p.id);
  if (!d) throw new Error(`The tool has no input '${p.id}'.`);
  if (typeof p.unit === "string") f.units[d.id] = p.unit;
  if (typeof p.on === "boolean") f.on[d.id] = p.on;
  if (p.clear) {
    delete f.items[d.id];
    delete f.files[d.id];
  }
  if (typeof p.remove === "number") {
    f.items[d.id]?.splice(p.remove, 1);
    f.files[d.id]?.splice(p.remove, 1);
  }
  if (!("value" in p)) return;
  const v = p.value;
  if (d.type === "number" || d.type === "integer") {
    let n = stored(d, Number(v));
    if (d.type === "integer") n = Math.round(n);
    // Native boxes keep their range; converted ones (pressure here) report it instead.
    if (d.unit !== "pressure") n = Math.min(d.max ?? Infinity, Math.max(d.min ?? -Infinity, n));
    f.values[d.id] = n;
  } else if (d.type === "size") {
    const arr = [...(f.values[d.id] as number[])];
    arr[p.index ?? 0] = Math.min(d.max ?? Infinity, Math.max(d.min ?? 0, Number(v)));
    f.values[d.id] = arr;
  } else if (d.type === "choice" || d.type === "options") {
    if (!choicesOf(d).includes(v)) return;
    f.values[d.id] = v;
    // Cascades: the plate grade follows the mold material, the grade the family, and the grade fills the process data.
    if (d.id === "mold_material" && !choicesOf(DEFS.find((x) => x.id === "mold_grade")!).includes(str("mold_grade"))) f.values.mold_grade = choicesOf(DEFS.find((x) => x.id === "mold_grade")!)[0];
    if (d.id === "part_family") f.values.part_grade = (MATERIALS[v] ?? [])[0]?.grade;
    if (d.id === "part_family" || d.id === "part_grade") fill();
  } else if (d.type === "text") f.values[d.id] = String(v ?? "");
}

function pick(id: string) {
  const f = form!;
  if (id === "critical_faces") {
    f.items[id] = [
      { type: "Face", label: "Imported1", detail: "planar, normal (0, 0, 1), area 1963 mm²" },
      { type: "Face", label: "Imported1", detail: "cylindrical, radius 25 mm" },
    ];
  } else if (id === "part_file") f.files[id] = ["C:\\Work\\Inputs\\puck.step"];
  else if (id === "drawing") f.files[id] = [...(f.files[id] ?? []), `C:\\Work\\Inputs\\puck drawing ${(f.files[id]?.length ?? 0) + 1}.pdf`];
}

/** The runTool payload ToolRunner would send after OK (ToolRunner.InputEntry's value text). */
function payload() {
  const s = state();
  const inputs = s.groups
    .flatMap((g) => g.inputs)
    .filter((i) => i.visible && i.type !== "info")
    .map((i) => {
      if (i.type === "selection") return { id: i.id, label: i.label, type: i.type, items: (i.items ?? []).map((x) => ({ ...x, ref: null })) };
      if (i.type === "files") return { id: i.id, label: i.label, type: i.type, files: [] };
      const value =
        i.type === "number" ? (i.optional && !i.on ? "not set" : `${i.value} ${i.unit ?? ""}`.trim()) : i.type === "size" ? `${(i.value as number[]).join(" x ")} mm` : i.value;
      return { id: i.id, label: i.label, type: i.type, value };
    });
  return {
    tool: s.tool,
    title: s.title,
    builtIn: true,
    intelligence: s.agent?.tier,
    maxSteps: 600,
    longRun: true,
    inputs,
    context: { model: "Mounting Bracket.SLDPRT (mock): part, 1 body, mm" },
  };
}

/** Opens the mock form (userTools.run of a tool with inputs while "toolInputs" isn't "page"). */
export function mockOpenToolForm(emit: Emit) {
  if (form) emit("toolForm.closed", { formId: form.formId, reason: "Another tool was started." });
  emit("toolForm.show", open());
}

/** toolForm.* RPCs of the mock host. */
export function mockToolForm(method: string, params: any, emit: Emit): unknown {
  if (method === "toolForm.list") return form ? [state()] : [];
  if (!form || params?.formId !== form.formId) throw new Error("This tool form is closed. Run the tool again.");
  switch (method) {
    case "toolForm.change":
      if (!params.preview) change(params);
      return state();
    case "toolForm.pick":
      pick(params.id);
      return state();
    case "toolForm.ok": {
      const s = state();
      if (!s.okEnabled) return { closed: false, form: s };
      const run = payload();
      form = null;
      setTimeout(() => emit("command", { name: "runTool", args: run }), 200);
      return { closed: true };
    }
    case "toolForm.cancel":
      form = null;
      return { closed: true };
  }
  return null;
}
