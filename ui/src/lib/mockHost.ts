/**
 * Stand-in for the SolidWorks add-in when the UI runs in a normal browser (npm run dev).
 * Lets the chat UI be designed and exercised without SolidWorks. Add ?demo to the URL to seed a sample chat.
 */
import type { ChatData, FileEntry, HostState, ToolDef, ToolRunResult, UserToolInfo, WatchEntry, WatchResult } from "./types";
import { emitLocal } from "./bridge";
import { mockOpenToolForm, mockToolForm } from "./toolFormMock";

const DOC = { key: "mock-doc", title: "Mounting Bracket.SLDPRT", path: "C:\\Work\\Mounting Bracket.SLDPRT", type: "part" as const };

const TOOLS: ToolDef[] = [
  tool("capture_views", "Screenshot of the SolidWorks graphics area.", { views: { type: "array", items: { type: "string" } } }),
  tool("execute_csharp", "Run C# against the live SolidWorks API.", { code: { type: "string" }, title: { type: "string" } }, ["code"]),
  tool("get_feature_tree", "FeatureManager design tree.", {}),
  tool("get_model_context", "Summary of the document.", {}),
  tool("get_selection", "Current selection.", {}),
  tool("search_sw_api", "Search the SolidWorks API.", { query: { type: "string" } }, ["query"]),
  // The mold tool's record (Track D): writes print `record_status: <status>`, which ends a long run's follow-ups at done.
  tool("mold_record", "For the design-compression-mold tool: the mold's design record.", { action: { type: "string" }, data: { type: "object" } }, ["action"]),
];

/** The mock mold record's status (mold_record). */
let mockRecordStatus = "planning";

function tool(name: string, description: string, properties: Record<string, any>, required?: string[]): ToolDef {
  return { name, description, input_schema: { type: "object", properties, ...(required ? { required } : {}) } };
}

const store = {
  get<T>(key: string, fallback: T): T {
    try {
      const v = localStorage.getItem("swagent-mock:" + key);
      return v ? (JSON.parse(v) as T) : fallback;
    } catch {
      return fallback;
    }
  },
  set(key: string, value: unknown) {
    localStorage.setItem("swagent-mock:" + key, JSON.stringify(value));
  },
};

export async function mockRpc(method: string, params: any): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 30));
  switch (method) {
    case "app.getState": {
      setTimeout(() => emitLocal("selection.changed", { docKey: DOC.key, items: [{ type: "Face", label: "Boss-Extrude1" }, { type: "Edge", label: "Fillet2" }] }), 300);
      const state: HostState = {
        version: "0.1.0-mock",
        solidworksVersion: "34.0.0 (mock)",
        apiKeySource: store.get<string>("apiKey", "") ? "stored" : "none",
        settings: store.get("settings", {}),
        scratchKey: "scratch",
        activeDoc: DOC,
        docs: [DOC],
        logFile: "(mock)",
        update: MOCK_UPDATE,
        swTheme: location.search.includes("light") ? { theme: 0, treeBg: "#FFFFFF", pmBg: "#F0F0F0" } : { theme: 2, treeBg: "#494949", pmBg: "#494949" },
      };
      return state;
    }
    case "settings.save":
      store.set("settings", params);
      return null;
    case "apiKey.get":
      return { key: store.get<string>("apiKey", "") || null };
    case "apiKey.set":
      store.set("apiKey", params?.key ?? "");
      return { source: params?.key ? "stored" : "none" };
    case "chat.load": {
      const key = params?.docKey ?? "scratch";
      let data = store.get<ChatData | null>("chat:" + key, null);
      if (!data && key === DOC.key && location.search.includes("demo")) data = demoChat();
      return { docKey: key, docId: data?.docId ?? "mock-" + key, recovered: false, data: data ?? emptyChat("mock-" + key) };
    }
    case "chat.save": {
      const data = params.data as ChatData;
      data.revision = (data.revision ?? 0) + 1;
      store.set("chat:" + params.docKey, data);
      return { revision: data.revision };
    }
    case "selection.snapshot":
      return {
        items: [
          { type: "Face", label: "Boss-Extrude1", detail: "planar, normal (0, 0, 1), area 9600 mm², spans (-60, -40, 10) to (60, 40, 10) mm", ref: "bW9jay1mYWNl" },
          { type: "Edge", label: "Fillet2", detail: "straight, direction (1, 0, 0), length 120 mm", ref: "bW9jay1lZGdl" },
        ],
        view: { text: "Viewing from +X+Y+Z (direction (0.577, 0.577, 0.577)); screen right = (0.707, 0, -0.707), screen up = (-0.408, 0.816, -0.408) (model coordinates)." },
      };
    case "markup.start":
      setTimeout(() => emitLocal("markup.strokes", { count: 2 }), 400);
      return { active: true };
    case "markup.finish":
      return { frames: [{ mediaType: "image/jpeg", data: mockImage().source.data, strokes: 2 }], inVoice: 0 };
    case "voice.status":
      return {
        enabled: true,
        model: "base.en",
        models: [
          { id: "tiny.en", label: "Fastest", mb: 74, note: "Quick on any PC; misses more technical words.", installed: false },
          { id: "base.en", label: "Balanced", mb: 141, note: "Recommended: fast, and good with CAD terms.", installed: true },
          { id: "small.en", label: "Most accurate", mb: 465, note: "Best accuracy; words appear more slowly on older CPUs.", installed: false },
        ],
        engine: "ready",
        engineError: null,
        download: null,
        downloadError: null,
        available: true,
      };
    case "voice.voices":
      return [{ name: "Microsoft Zira Desktop", culture: "en-US" }];
    case "voice.download":
    case "voice.cancel":
    case "voice.speak":
    case "voice.stopSpeaking":
    case "voice.cancelDownload":
    case "voice.deleteModel":
      return null;
    case "userTools.list":
      return mockUserTools();
    case "userTools.run": {
      // The add-in would show the tool's page, then hand the run to the panel (ToolRunner): do the hand-over.
      const t = mockUserTools().find((x) => x.name === params?.name);
      // A tool with inputs opens its form in the panel (ToolFormBridge) unless the user chose the PropertyManager page.
      if (t?.kind === "tool" && t.hasInputs && store.get<{ toolInputs?: string }>("settings", {}).toolInputs !== "page") {
        setTimeout(() => mockOpenToolForm(emitLocal), 150);
        return null;
      }
      if (t?.kind === "tool")
        setTimeout(
          () =>
            emitLocal("command", {
              name: "runTool",
              args: {
                tool: t.name,
                title: t.title,
                intelligence: t.intelligence ?? "balanced",
                maxSteps: t.maxSteps ?? undefined,
                longRun: t.longRun,
                inputs: [{ id: "notes", label: "Notes", type: "text", value: "(mock run from the browser)" }],
                context: { model: "Mounting Bracket.SLDPRT (mock): part, 1 body, mm" },
              },
            }),
          200,
        );
      return null;
    }
    case "userTools.setValue": {
      // The Workflows menu's category button (and the tool page's intelligence): kept for this browser session.
      const all = store.get<Record<string, Record<string, string>>>("toolSettings", {});
      // null resets a built-in's value to what it ships with (UserTools.SetValue).
      const mine: Record<string, string> = { ...(all[params.name] ?? {}), [params.key]: params.value };
      if (params.value === null) delete mine[params.key];
      all[params.name] = mine;
      store.set("toolSettings", all);
      setTimeout(() => emitLocal("userTools.changed", mockUserTools()), 50);
      return null;
    }
    case "toolForm.list":
    case "toolForm.change":
    case "toolForm.pick":
    case "toolForm.ok":
    case "toolForm.cancel":
      return mockToolForm(method, params, emitLocal);
    case "update.status":
    case "update.check":
      return MOCK_UPDATE;
    case "update.unsavedDocuments":
      return [{ title: "Mounting Bracket.SLDPRT", path: "C:\Parts\Mounting Bracket.SLDPRT", untitled: false }];
    case "update.restartAndInstall":
      return { ok: false, reason: "(mock) SolidWorks would now save, close, update and reopen." };
    case "userTools.setIcon":
      console.log("mock setIcon", params.name, String(params.png).length);
      (window as unknown as { __iconsSet?: string[] }).__iconsSet = [...((window as unknown as { __iconsSet?: string[] }).__iconsSet ?? []), params.name];
      return null;
    case "tools.list":
      return TOOLS;
    case "tools.run":
      return runTool(params.name, params.input ?? {});
    case "selection.get":
      return { docKey: DOC.key, items: [{ type: "FACES", label: "Boss-Extrude1" }] };
    case "files.pick":
      return mockEntries(["C:\\Work\\Inputs\\housing.step", "C:\\Work\\Inputs\\clip.stl", "C:\\Work\\Inputs\\drawing.pdf"]);
    case "files.entries":
      return mockEntries(params?.paths ?? []);
    case "files.describe":
      if (/\.slddrw$/i.test(params.path)) {
        const name = (params.path as string).split(/[\\/]/).pop() ?? params.path;
        return { name: `${name} (as PDF)`, path: params.path, kind: "pdf", mediaType: "application/pdf", data: "JVBERi0xLjQK", pdfPath: params.path.replace(/\.slddrw$/i, ".pdf") };
      }
      return describeModel(params.path);
    case "watch.start":
      return startWatch(params?.docKey);
    case "watch.status":
      return watch ? { active: true, docKey: DOC.key, doc: DOC.title, startedAt: new Date(watch.started).toISOString(), entries: watch.entries } : { active: false };
    case "watch.stop":
      return stopWatch();
    case "watch.cancel":
      if (watch) clearInterval(watch.timer);
      watch = null;
      return null;
    case "app.openExternal":
      window.open(params.url, "_blank", "noopener");
      return null;
    case "history.undoable":
      return { changes: params.toolUseIds?.length ?? 0 };
    case "history.undoSince":
      await new Promise((r) => setTimeout(r, 600));
      return { ok: true, steps: params.toolUseIds?.length ?? 0 };
    default:
      return null;
  }
}

// ---- tools: a categorised built-in, an uncategorised one, and the user's own in a category and without ----

const MOLD_SVG =
  "<svg viewBox=\"0 0 16 16\"><defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#D8DCE1\"/><stop offset=\"1\" stop-color=\"#9AA3AD\"/></linearGradient></defs><rect x=\"1.5\" y=\"8.5\" width=\"13\" height=\"5\" rx=\"0.5\" fill=\"url(#g)\" stroke=\"#59626C\"/><rect x=\"1.5\" y=\"2.5\" width=\"13\" height=\"5\" rx=\"0.5\" fill=\"url(#g)\" stroke=\"#59626C\"/><path d=\"M5 8.5h6v2.2a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1z\" fill=\"#E8912D\" stroke=\"#A35F14\" stroke-width=\"0.75\"/></svg>";

function mockUserTools(): UserToolInfo[] {
  const updated = new Date().toISOString();
  const tools: UserToolInfo[] = [
    {
      name: "design-compression-mold",
      builtIn: true,
      title: "Design compression mold for plastics and resins",
      ribbonTitle: "Compression Mold",
      category: "Molding & Toolmaking",
      categoryDefault: "Molding & Toolmaking",
      description: "Designs a heat-press compression mold for a PU, resin, rubber or thermoset part, with every plate, insert and pad as a body in this part.",
      icon: "part",
      updated,
      kind: "tool",
      intelligence: "max",
      longRun: true,
      maxSteps: 600,
      requires: ["mold_calc", "mold_part", "mold_bodies", "mold_record"],
      hasInputs: true,
    },
    { name: "smart-rebuild", builtIn: true, categoryDefault: null, title: "Smart Rebuild", description: "Rebuild the whole design around a change far back in its history.", icon: "rebuild", updated, kind: "tool", intelligence: "max", hasInputs: true },
    { name: "add-mounting-holes", title: "Add mounting holes", description: "Select a planar face; adds a 4-hole M6 clearance pattern 10 mm in from the face edges.", icon: "select", updated, kind: "tool", hasMacro: true },
    { name: "flash-grooves", title: "Flash grooves", description: "Cuts the shop's flash-groove pattern around the selected cavity face.", icon: "macro", updated, kind: "tool", hasMacro: true, category: "molding & toolmaking" },
    { name: "compression-mold", title: "Create compression mold", description: "From the active part: split-line, cavity/core plates with 1.5 deg draft, flash land, alignment pins and vents.", icon: "assembly", updated, kind: "workflow", iconSvg: MOLD_SVG, hasPng: false, category: "Molding & Toolmaking" },
    { name: "export-drawings", title: "Export drawings", description: "Saves every open drawing as PDF and DXF next to its file.", icon: "drawing", updated, kind: "workflow" },
  ];
  const settings = store.get<Record<string, Record<string, string>>>("toolSettings", {});
  return tools.map((t) => {
    const s = settings[t.name];
    if (!s) return t;
    return { ...t, ...("category" in s ? { category: s.category || null } : {}), ...(s.intelligence ? { intelligence: s.intelligence as UserToolInfo["intelligence"] } : {}) };
  });
}

// ---- files (3D model context) ----

const MODEL_EXT = /\.(stl|3mf|obj|step|stp|iges|igs|x_t|x_b|sldprt|sldasm)$/i;

function mockEntries(paths: string[]): FileEntry[] {
  return paths.map((path) => {
    const name = path.split(/[\\/]/).pop() ?? path;
    if (MODEL_EXT.test(name)) return { name, path, kind: "model", format: name.split(".").pop()!.toUpperCase() };
    if (/\.slddrw$/i.test(name)) return { name, path, kind: "drawing", format: "SolidWorks drawing" };
    if (/\.pdf$/i.test(name)) return { name, path, kind: "pdf", mediaType: "application/pdf", data: "JVBERi0xLjQK" };
    if (/\.(png|jpe?g)$/i.test(name)) return { name, path, kind: "image", mediaType: "image/jpeg", data: mockImage().source.data };
    return { name, path, kind: "file" };
  });
}

/** Stands in for ModelFileContext: a six-view sheet and a measurement summary, after a realistic delay. Name a file "broken.step" to see a failure. */
async function describeModel(path: string): Promise<FileEntry> {
  const name = path.split(/[\\/]/).pop() ?? path;
  const mesh = /\.(stl|3mf|obj)$/i.test(name);
  await new Promise((r) => setTimeout(r, mesh ? 900 : 2200));
  if (/broken/i.test(name)) {
    return {
      name,
      path,
      kind: "model",
      error: "SolidWorks couldn't import the file (swGenericError).",
      summary: `3D model: ${name} (STEP, 12 KB) - couldn't be read automatically: SolidWorks couldn't import the file (swGenericError). Path: ${path}`,
    };
  }
  const summary = [
    `3D model: ${name} (${mesh ? "STL mesh" : "STEP"}, 1.4 MB)`,
    mesh ? "" : "Read by: SolidWorks (opened hidden in the background, then closed - the user's documents and view were not touched)",
    `Units: ${mesh ? "mm (assumed - STL files carry no units)" : "mm (document unit: millimeter)"}`,
    "Size: 120 x 42 x 80 mm (X -60..60, Y 0..42, Z -40..40)",
    mesh ? "Volume 96,512 mm³, surface area 31,204 mm² (closed mesh)" : "Structure: part, 1 solid body, 14 features",
    mesh ? "" : "Mass properties (SolidWorks): volume 96,512 mm³, surface area 31,204 mm², mass 0.2606 kg, material 6061 Alloy",
    "Mesh: 18,342 triangles, 1 separate shell" + (mesh ? ", watertight" : ""),
    "Attached: one image with six views of the model - isometric, front, top, right, back iso, from below (SolidWorks axes, Y up; a triad in each tile shows the axes). Colours tell separate shells apart.",
  ]
    .filter(Boolean)
    .join("\n");
  return { name, path, kind: "model", format: mesh ? "STL mesh" : "STEP", summary, views: sixViews(), viewsMediaType: "image/jpeg", seconds: 1.8 };
}

function sixViews(): string {
  const c = document.createElement("canvas");
  c.width = 1200;
  c.height = 600;
  const g = c.getContext("2d")!;
  g.fillStyle = "#fff";
  g.fillRect(0, 0, 1200, 600);
  ["ISOMETRIC", "FRONT", "TOP", "RIGHT", "BACK ISO", "FROM BELOW"].forEach((label, i) => {
    const x = (i % 3) * 400;
    const y = Math.floor(i / 3) * 300;
    const grad = g.createLinearGradient(0, y, 0, y + 300);
    grad.addColorStop(0, "#e7edf4");
    grad.addColorStop(1, "#ffffff");
    g.fillStyle = grad;
    g.fillRect(x, y, 400, 300);
    g.strokeStyle = "#aaa";
    g.lineWidth = 1;
    g.strokeRect(x + 0.5, y + 0.5, 399, 299);
    g.fillStyle = "#b0bece";
    g.strokeStyle = "#262e38";
    g.lineWidth = 1.5;
    g.beginPath();
    if (i === 0 || i >= 4) {
      g.moveTo(x + 110, y + 170);
      g.lineTo(x + 220, y + 225);
      g.lineTo(x + 310, y + 160);
      g.lineTo(x + 200, y + 105);
    } else g.rect(x + 90, y + (i === 2 ? 70 : 120), 220, i === 2 ? 160 : 70);
    g.closePath();
    g.fill();
    g.stroke();
    g.fillStyle = "rgba(32,38,46,0.8)";
    g.fillRect(x + 6, y + 6, label.length * 8 + 10, 18);
    g.fillStyle = "#fff";
    g.font = "bold 11px Segoe UI, sans-serif";
    g.fillText(label, x + 10, y + 19);
  });
  return c.toDataURL("image/jpeg", 0.8).split(",")[1];
}

// ---- Watch me ----

const SCRIPT: Omit<WatchEntry, "id" | "t">[] = [
  { kind: "select", text: "Selected face of Boss-Extrude1 (planar, normal (0, 1, 0), area 9600 mm²)" },
  { kind: "add", text: "Added Sketch3 (Sketch): on Boss-Extrude1: 4 circles Ø6.6 mm - with face of Boss-Extrude1 (planar, normal (0, 1, 0), area 9600 mm²) selected" },
  { kind: "add", text: "Added Cut-Extrude2 (Cut-Extrude): end through all (from Sketch3)" },
  { kind: "select", text: "Selected 4 edges of Cut-Extrude2" },
  { kind: "add", text: "Added Chamfer1 (Chamfer): equal distance 0.5 mm on 4 edges - with 4 edges of Cut-Extrude2 selected" },
  { kind: "edit", text: "Edited Boss-Extrude1 (Boss-Extrude): end blind 10 mm -> blind 12 mm" },
];

let watch: { started: number; entries: WatchEntry[]; timer: ReturnType<typeof setInterval> } | null = null;

function startWatch(docKey: string) {
  if (docKey !== DOC.key) throw new Error("Open a part or assembly to record what you do in it.");
  if (watch) clearInterval(watch.timer);
  const w = { started: Date.now(), entries: [] as WatchEntry[], timer: undefined as unknown as ReturnType<typeof setInterval> };
  let next = 0;
  w.timer = setInterval(() => {
    if (next >= SCRIPT.length) return clearInterval(w.timer);
    const step = SCRIPT[next++];
    // Like ActivityLog: a selection followed by a feature is folded into it.
    if (w.entries.length && w.entries[w.entries.length - 1].kind === "select") w.entries.pop();
    w.entries.push({ ...step, id: next, t: (Date.now() - w.started) / 1000 });
    emitLocal("watch.log", { active: true, entries: [...w.entries] });
  }, 1800);
  watch = w;
  return { active: true, docKey: DOC.key, doc: DOC.title, startedAt: new Date().toISOString(), entries: [] };
}

async function stopWatch(): Promise<WatchResult> {
  if (!watch) throw new Error("Watch me isn't recording.");
  clearInterval(watch.timer);
  const w = watch;
  watch = null;
  await new Promise((r) => setTimeout(r, 500));
  const seconds = Math.round((Date.now() - w.started) / 1000);
  const lines = w.entries.map((e, i) => `${i + 1}. [0:${String(Math.round(e.t)).padStart(2, "0")}] ${e.text}`);
  const text =
    `Recorded with "Watch me" in ${DOC.title} (part) over ${seconds} s: ${w.entries.length} steps, feature tree 9 -> 12 features.\n` +
    `Steps (lengths in mm, angles in degrees; dimension names like D1@Sketch2 are the SolidWorks full names):\n${lines.join("\n")}\n\n` +
    "Final parameters of the features above:\n" +
    "- Sketch3 (Sketch) <- Boss-Extrude1: segments 4; circles Ø6.6 mm Ø6.6 mm Ø6.6 mm Ø6.6 mm; D1@Sketch3 6.6 mm; D2@Sketch3 10 mm\n" +
    "- Cut-Extrude2 (Cut-Extrude) <- Sketch3: end through all\n" +
    "- Chamfer1 (Chamfer) <- Cut-Extrude2: type equal distance; distance 0.5 mm; edges 4";
  const img = mockImage().source.data;
  return {
    docKey: DOC.key,
    doc: DOC.title,
    docType: "part",
    seconds,
    steps: w.entries.length,
    text,
    entries: w.entries,
    stats: { added: w.entries.filter((e) => e.kind === "add").length, edited: w.entries.filter((e) => e.kind === "edit").length, deleted: 0, renamed: 0, selections: 0 },
    before: img,
    after: img,
    film: w.entries.length ? sixViews() : null,
  };
}

function emptyChat(docId: string): ChatData {
  return { schema: 1, docId, revision: 0, activeThreadId: null, threads: [] };
}

async function runTool(name: string, input: any): Promise<ToolRunResult> {
  await new Promise((r) => setTimeout(r, 600));
  switch (name) {
    case "capture_views":
      return { isError: false, content: [{ type: "text", text: "Captured isometric (1280x720)." }, mockImage()] };
    case "execute_csharp":
      return { isError: false, content: [{ type: "text", text: "OK (compile 412 ms, run 38 ms)\n--- output ---\nFillet created: Fillet3 (2 edges, R2 mm)" }] };
    case "get_feature_tree":
      return { isError: false, content: [{ type: "text", text: "Origin [OriginProfileFeature]\nFront Plane [RefPlane]\nTop Plane [RefPlane]\nRight Plane [RefPlane]\nBoss-Extrude1 [Extrusion]\n  Sketch1 [ProfileFeature]\nCut-Extrude1 [Cut]\n  Sketch2 [ProfileFeature]\nFillet1 [Fillet]" }] };
    case "mold_record": {
      // The agreed output (MoldTools.StatusLine): every write ends with a `record_status:` line; reads don't count.
      if (input.action === "read") return { isError: false, content: [{ type: "text", text: `Mold record 5b0c - status ${mockRecordStatus}\nStages: 4.3 done` }] };
      if (typeof input.data?.status === "string") mockRecordStatus = input.data.status;
      return { isError: false, content: [{ type: "text", text: `merged; status ${mockRecordStatus}; valid\nrecord_status: ${mockRecordStatus}` }] };
    }
    default:
      return { isError: false, content: [{ type: "text", text: `(mock) ${name} ${JSON.stringify(input)}` }] };
  }
}

function mockImage() {
  const c = document.createElement("canvas");
  c.width = 640;
  c.height = 360;
  const g = c.getContext("2d")!;
  const grad = g.createLinearGradient(0, 0, 0, 360);
  grad.addColorStop(0, "#dfe6ee");
  grad.addColorStop(1, "#ffffff");
  g.fillStyle = grad;
  g.fillRect(0, 0, 640, 360);
  g.fillStyle = "#9aa7b6";
  g.beginPath();
  g.moveTo(200, 230);
  g.lineTo(380, 270);
  g.lineTo(470, 200);
  g.lineTo(290, 160);
  g.closePath();
  g.fill();
  g.fillStyle = "#7d8b9b";
  g.fillRect(200, 230, 180, 40);
  const data = c.toDataURL("image/jpeg", 0.8).split(",")[1];
  return { type: "image" as const, source: { type: "base64" as const, media_type: "image/jpeg" as const, data } };
}

const MOCK_UPDATE = {
  current: "0.1.0",
  latest: "0.2.0",
  status: location.search.includes("update") ? "available" : "uptodate",
  notes: "### What's new\n- Your tools get their own ribbon buttons and icons\n- Text size with Ctrl + / Ctrl -\n- Finished turns fold down to what changed",
  releaseUrl: null,
  error: null,
  progress: 0,
  lastChecked: null,
} as const;

function demoChat(): ChatData {
  const now = new Date().toISOString();
  return {
    schema: 1,
    docId: "mock-demo",
    revision: 1,
    activeThreadId: "t1",
    threads: [
      {
        id: "t1",
        title: "Add fillets to the bracket edges",
        createdAt: now,
        updatedAt: now,
        model: "claude-opus-5-5",
        effort: "high",
        turns: [{ at: now, msgIndex: 0, model: "claude-opus-5-5", effort: "high", usage: { input: 18342, output: 1210, cacheRead: 14002, cacheWrite: 4210 }, durationMs: 21400, stop: "end_turn" }],
        messages: [
          {
            role: "user",
            content: [
              { type: "text", text: "Add a 2 mm fillet to the selected edges, then show me the result." },
              { type: "text", text: "<sw_context>\nDocument: Mounting Bracket.SLDPRT (part)\nSelection: 2 x Edges\n</sw_context>" },
            ],
          },
          {
            role: "assistant",
            content: [
              { type: "thinking", thinking: "Checking which edges are selected first.", signature: "x" },
              { type: "tool_use", id: "tu1", name: "get_selection", input: {} },
            ],
          },
          { role: "user", content: [{ type: "tool_result", tool_use_id: "tu1", content: [{ type: "text", text: "[1] Edges (line, length 40 mm) of feature 'Boss-Extrude1'\n[2] Edges (line, length 40 mm) of feature 'Boss-Extrude1'" }] }] },
          {
            role: "assistant",
            content: [
              { type: "thinking", thinking: "Two straight 40 mm edges on Boss-Extrude1. Creating a constant-radius fillet.", signature: "x" },
              {
                type: "tool_use",
                id: "tu2",
                name: "execute_csharp",
                input: {
                  title: "Fillet selected edges R2",
                  code: 'var fm = Model.FeatureManager;\nvar feat = (Feature)fm.FeatureFillet3(\n    (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius,\n    mm(2), 0, 0, 0, 0, 0, null, null, null, null, null, null, null);\nPrint(feat?.Name ?? "fillet failed");\nfeat?.Name',
                },
              },
            ],
          },
          { role: "user", content: [{ type: "tool_result", tool_use_id: "tu2", content: [{ type: "text", text: "OK (compile 412 ms, run 38 ms)\n--- output ---\nFillet3\n--- return value ---\nFillet3" }] }] },
          {
            role: "assistant",
            content: [{ type: "tool_use", id: "tu3", name: "capture_views", input: { views: ["isometric"] } }],
          },
          { role: "user", content: [{ type: "tool_result", tool_use_id: "tu3", content: [{ type: "text", text: "Captured isometric (1280x720)." }] }] },
          {
            role: "assistant",
            content: [
              {
                type: "text",
                text: "Added **Fillet3** — a 2 mm constant-radius fillet on the two selected edges of `Boss-Extrude1`.\n\n| Feature | Edges | Radius |\n|---|---|---|\n| Fillet3 | 2 | 2 mm |\n\nThe rebuild is clean. Press **Ctrl+Z** once to undo it as a single step.",
              },
            ],
          },
        ],
      },
    ],
  };
}
