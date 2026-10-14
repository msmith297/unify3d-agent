/**
 * Stand-in for the SolidWorks add-in when the UI runs in a normal browser (npm run dev).
 * Lets the chat UI be designed and exercised without SolidWorks. Add ?demo to the URL to seed a sample chat.
 */
import type { ChatData, HostState, ToolDef, ToolRunResult } from "./types";
import { emitLocal } from "./bridge";

const DOC = { key: "mock-doc", title: "Mounting Bracket.SLDPRT", path: "C:\\Work\\Mounting Bracket.SLDPRT", type: "part" as const };

const TOOLS: ToolDef[] = [
  tool("capture_views", "Screenshot of the SolidWorks graphics area.", { views: { type: "array", items: { type: "string" } } }),
  tool("execute_csharp", "Run C# against the live SolidWorks API.", { code: { type: "string" }, title: { type: "string" } }, ["code"]),
  tool("get_feature_tree", "FeatureManager design tree.", {}),
  tool("get_model_context", "Summary of the document.", {}),
  tool("get_selection", "Current selection.", {}),
  tool("search_sw_api", "Search the SolidWorks API.", { query: { type: "string" } }, ["query"]),
];

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
      return { mediaType: "image/jpeg", data: mockImage().source.data, strokes: 2 };
    case "userTools.list":
      return [
        { name: "add-mounting-holes", title: "Add mounting holes", description: "Select a planar face; adds a 4-hole M6 clearance pattern 10 mm in from the face edges.", icon: "select", updated: new Date().toISOString() },
        { name: "compression-mold", title: "Create compression mold", description: "From the active part: split-line, cavity/core plates with 1.5 deg draft, flash land, alignment pins and vents.", icon: "assembly", updated: new Date().toISOString(), iconSvg: "<svg viewBox=\"0 0 16 16\"><defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#D8DCE1\"/><stop offset=\"1\" stop-color=\"#9AA3AD\"/></linearGradient></defs><rect x=\"1.5\" y=\"8.5\" width=\"13\" height=\"5\" rx=\"0.5\" fill=\"url(#g)\" stroke=\"#59626C\"/><rect x=\"1.5\" y=\"2.5\" width=\"13\" height=\"5\" rx=\"0.5\" fill=\"url(#g)\" stroke=\"#59626C\"/><path d=\"M5 8.5h6v2.2a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1z\" fill=\"#E8912D\" stroke=\"#A35F14\" stroke-width=\"0.75\"/></svg>", hasPng: false },
      ];
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
    case "app.openExternal":
      window.open(params.url, "_blank", "noopener");
      return null;
    default:
      return null;
  }
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
