// Checks for the panel's pure modules (lib/toolRun.ts, lib/selection.ts, lib/toolGroups.ts, lib/toolFormLogic.ts, lib/toolFormMock.ts).
// Run from the ui folder:  node src/lib/checks/lib.check.mjs   (Node 22.18+ / 23.6+ runs the .ts imports as-is)
// The ui has no test runner yet; when vitest is added (ui/package.json), these move into *.test.ts files unchanged.
// Not part of the app: nothing imports this file, and tsc / vite ignore .mjs files.
import assert from "node:assert/strict";
import { selectionKind, isAllowed, refBase, kindsText, selectionStatus, selectionAnswer } from "../selection.ts";
import {
  clampSteps,
  docMatches,
  stickyState,
  lastReportedStatus,
  runReportsDone,
  runDocs,
  withRunDoc,
  STICKY_IDLE_MS,
} from "../toolRun.ts";
import { paletteSections, categoryNames } from "../toolGroups.ts";
import { formatNumber, parseNumber, stepNumber, visibleGroups, chipText, takesText, acceptText, tableHint } from "../toolFormLogic.ts";
import { mockOpenToolForm, mockToolForm } from "../toolFormMock.ts";

let n = 0;
const t = (name, fn) => {
  fn();
  n++;
  console.log("ok -", name);
};

// ---- selection (ask_selection) ----

t("selectionKind maps the add-in's type names", () => {
  assert.equal(selectionKind("Face"), "face");
  assert.equal(selectionKind("Edge"), "edge");
  assert.equal(selectionKind("Vertex"), "vertex");
  assert.equal(selectionKind("Solidbodies"), "body");
  assert.equal(selectionKind("Surfacebodies"), "body");
  assert.equal(selectionKind("Feature"), "feature");
  assert.equal(selectionKind("Plane"), "feature");
  assert.equal(selectionKind("Component"), "component");
  assert.equal(selectionKind("Dimension"), null);
  assert.equal(isAllowed("Dimension", {}), true);
  assert.equal(isAllowed("Dimension", { types: ["face"] }), false);
  assert.equal(isAllowed("Face", { types: ["edge", "face"] }), true);
});

t("refBase continues after the highest id", () => {
  assert.equal(refBase(undefined), 0);
  assert.equal(refBase({ S1: "a", S2: "b" }), 2);
  assert.equal(refBase({ S1: "a", S5: "b" }), 5); // S2-S4 were given to entities without refs
  assert.equal(refBase({ S1: "a", X: "b", S9x: "c" }), 3);
});

t("kindsText and the card's status", () => {
  assert.equal(kindsText(undefined), "entities");
  assert.equal(kindsText(["face"]), "faces");
  assert.equal(kindsText(["face"], false), "face");
  assert.equal(kindsText(["face", "edge", "vertex"]), "faces, edges or vertices");
  assert.deepEqual(selectionStatus(0, { types: ["face"], min: 1 }), { text: "0 faces selected - select at least 1", ok: false });
  assert.deepEqual(selectionStatus(1, { types: ["face"], min: 1 }), { text: "1 face selected", ok: true });
  assert.equal(selectionStatus(5, { max: 3 }).ok, true);
  assert.match(selectionStatus(5, { max: 3 }).text, /at most 3/);
  assert.equal(selectionStatus(0, {}).ok, true);
  // More selected than the chips show (they stop at 30): Done stays possible for a large min.
  assert.deepEqual(selectionStatus(30, { types: ["face"], min: 40 }, { more: true }), { text: "30+ faces selected", ok: true });
  assert.equal(selectionStatus(30, { types: ["face"], min: 40 }).ok, false);
});

const items = [
  { type: "Face", label: "Imported1", detail: "cylinder r 10 mm, area 1256 mm²", ref: "AAA" },
  { type: "Edge", label: "Imported1", detail: "line 40 mm", ref: "BBB" },
  { type: "Face", label: "Imported1", detail: "planar", ref: null },
  { type: "Face", label: "", detail: "", ref: "CCC" },
];

t("Done numbers allowed entities from base+1 and reports the others", () => {
  const a = selectionAnswer(items, { prompt: "p", types: ["face"], min: 1 }, 6, { note: "  the bore too  " });
  assert.deepEqual(a.refs, { S7: "AAA", S8: "CCC" });
  assert.equal(a.count, 2);
  const lines = a.text.split("\n");
  assert.match(lines[0], /^The user pressed Done with 3 faces selected - use the \[S#\] references/);
  assert.equal(lines[1], "[S7] Face of Imported1 - cylinder r 10 mm, area 1256 mm²");
  assert.equal(lines[2], "- Face of Imported1 - planar (no persistent id: not addressable with Ref)");
  assert.equal(lines[3], "[S8] Face");
  assert.equal(lines[4], "Not counted (not faces): 1 Edge.");
  assert.equal(lines[5], 'Their note: "the bore too"');
});

t("Done with too few / too many / nothing", () => {
  assert.match(selectionAnswer([], { prompt: "p", types: ["edge"] }, 0).text, /^The user pressed Done with no edges selected\.$/);
  assert.match(selectionAnswer(items.slice(0, 1), { prompt: "p", min: 2 }, 0).text, /fewer than the 2/);
  assert.match(selectionAnswer(items, { prompt: "p", max: 1 }, 0).text, /more than the 1/);
  const all = selectionAnswer(items, { prompt: "p" }, 0);
  assert.deepEqual(Object.keys(all.refs), ["S1", "S2", "S3"]);
  assert.doesNotMatch(all.text, /Not counted/);
  assert.doesNotMatch(all.text, /cut/);
});

t("a cut list and unknown types are reported, not silent", () => {
  const cut = selectionAnswer(items, { prompt: "p" }, 0, { total: 612 });
  assert.match(cut.text, /only the first 4 of 612 selected entities were read/);
  const unknown = selectionAnswer(items, { prompt: "p", unknownTypes: ["surface"] }, 0);
  assert.match(unknown.text, /Unknown types ignored: surface - valid are face, edge, vertex, body, feature, component\. Any entity counted\./);
  const partly = selectionAnswer(items, { prompt: "p", types: ["face"], unknownTypes: ["loop"] }, 0);
  assert.match(partly.text, /Unknown types ignored: loop/);
  assert.doesNotMatch(partly.text, /Any entity counted/);
  assert.match(selectionAnswer(items, { prompt: "p", unknownTypes: ["x"] }, 0, { skipped: true }).text, /Unknown types ignored: x/);
});

t("Skip and an unreadable selection", () => {
  const s = selectionAnswer(items, { prompt: "p" }, 3, { skipped: true, note: "use your guess" });
  assert.deepEqual(s.refs, {});
  assert.match(s.text, /pressed Skip/);
  assert.match(s.text, /Their note: "use your guess"/);
  assert.match(selectionAnswer(null, { prompt: "p" }, 0).text, /couldn't be read/);
});

// ---- long tool runs ----

t("clampSteps: 50..1000 like UserTools.MaxStepsOf", () => {
  assert.equal(clampSteps(undefined), 200);
  assert.equal(clampSteps(null, 600), 600);
  assert.equal(clampSteps(600), 600);
  assert.equal(clampSteps(5000), 1000);
  assert.equal(clampSteps(0), 50);
  assert.equal(clampSteps(10), 50);
  assert.equal(clampSteps(-4), 50);
  assert.equal(clampSteps(NaN), 200);
});

const tr = (over = {}) => ({
  tool: "m",
  label: "Compression Mold",
  model: "x",
  effort: "high",
  maxSteps: 600,
  startedAt: 1000,
  lastActive: 1000,
  doc: { key: "k1", title: "Mold.SLDPRT", path: "C:\\W\\Mold.SLDPRT" },
  ...over,
});

t("docMatches: key, then path, then title for unsaved; nothing known never matches", () => {
  const doc = tr().doc;
  assert.equal(docMatches(doc, { key: "k1", title: "Other" }), true);
  assert.equal(docMatches(doc, { key: "k2", title: "Mold.SLDPRT", path: "c:\\w\\mold.sldprt" }), true); // reopened under a new key
  assert.equal(docMatches(doc, { key: "k2", title: "Mold.SLDPRT", path: "C:\\W\\Other.SLDPRT" }), false);
  assert.equal(docMatches(doc, null), false); // the panel has no DocInfo for the active document
  assert.equal(docMatches(null, { key: "k1", title: "x" }), false);
  assert.equal(docMatches(null, null), false);
  assert.equal(docMatches({ key: "k1", title: "Part1", path: null }, { key: "k9", title: "Part1" }), true);
  assert.equal(docMatches({ key: "k1", title: "Part1", path: null }, { key: "k9", title: "Part2" }), false);
});

t("stickyState: active, paused on another document, expired after 2 h idle", () => {
  const active = { key: "k1", title: "Mold.SLDPRT" };
  assert.equal(stickyState(tr(), 1000 + STICKY_IDLE_MS - 1, active), "active");
  assert.equal(stickyState(tr(), 1000 + STICKY_IDLE_MS + 1, active), "expired");
  assert.equal(stickyState(tr(), 2000, { key: "k3", title: "Bracket.SLDPRT" }), "other_doc");
  assert.equal(stickyState(tr({ lastActive: 5_000_000 }), 5_000_000 + 1000, active), "active");
  assert.equal(stickyState(tr(), 2000, null), "other_doc");
  assert.equal(stickyState(tr({ doc: null }), 2000, active), "other_doc");
});

t("documents the run activated count as its own (Phase 1a part built from a drawing)", () => {
  const part = { key: "k5", title: "Bushing.SLDPRT", path: "C:\\W\\part\\Bushing.SLDPRT" };
  const run = withRunDoc(tr(), part);
  assert.equal(stickyState(run, 2000, part), "active");
  assert.equal(stickyState(run, 2000, { key: "k1", title: "Mold.SLDPRT" }), "active");
  assert.equal(stickyState(run, 2000, { key: "k7", title: "Other.SLDPRT", path: "C:\\Other.SLDPRT" }), "other_doc");
  assert.equal(runDocs(run).length, 2);
  assert.equal(withRunDoc(run, { ...part, key: "k6" }), run); // same file under a new key: not added twice
  assert.equal(withRunDoc(run, { key: "k1", title: "Mold.SLDPRT" }), run);
});

// Messages of one turn: the agent's tool calls, then their results.
let ids = 0;
const turn = (calls) => {
  const uses = calls.map(([name, input]) => ({ type: "tool_use", id: `t${++ids}`, name, input }));
  return [
    { role: "assistant", content: uses },
    {
      role: "user",
      content: calls.map(([, , text, isError], i) => ({ type: "tool_result", tool_use_id: uses[i].id, content: [{ type: "text", text }], is_error: !!isError })),
    },
  ];
};
const start = { role: "user", content: [{ type: "text", text: "[Run tool: design-compression-mold]" }] };
const merge = (status, text = `merged; status ${status}; valid\nrecord_status: ${status}`) => ["mold_record", { action: "merge", data: { status } }, text];

// The pretty-printed record of design 4.3: a top-level status plus nested stage, check and simulation statuses.
const moldJson = (status) =>
  JSON.stringify(
    {
      schema: "unify3d.mold/1",
      id: "5b0c",
      status,
      checks: [{ id: "STR-BEAR-PL1", status: "pass" }],
      simulations: [{ id: "SIM-1", hook: "H1", status: "not_run" }],
      stages: [{ id: "4.3", name: "Cavities", status: "done", checkpoint: "153012-stage-4-3" }],
      materials: { part: { rheology: { status: "unknown" } } },
    },
    null,
    2,
  );

t("done: only a write of the record that reports record_status: done", () => {
  assert.equal(runReportsDone([start, ...turn([merge("done")])], 0, []), true);
  assert.equal(runReportsDone([start, ...turn([merge("verifying")])], 0, []), false);
  // MoldRecord.Merge's own line, passed through unchanged, also counts; so does the merge's input when nothing is printed.
  assert.equal(runReportsDone([start, ...turn([merge("done", "merged; status done; 2 warn; valid")])], 0, []), true);
  assert.equal(runReportsDone([start, ...turn([merge("done", "ok")])], 0, []), true);
  assert.equal(runReportsDone([start, ...turn([["mold_record", { action: "merge", data: '{"status":"done"}' }, "ok"]])], 0, []), true);
  assert.equal(runReportsDone([start, ...turn([["mold_record", { action: "init" }, "Created record 5b0c\nrecord_status: planning"]])], 0, []), false);
  assert.equal(lastReportedStatus([start, ...turn([["mold_record", { action: "init" }, "Created record 5b0c\nrecord_status: planning"]])], 0, []), "planning");
});

t("done: reads never count - a re-run on a finished mold stays open", () => {
  const read = (text) => ["mold_record", { action: "read" }, text];
  assert.equal(runReportsDone([start, ...turn([read("Mold record 5b0c - status done, updated ...\nrecord_status: done")])], 0, []), false);
  assert.equal(runReportsDone([start, ...turn([read(moldJson("done"))])], 0, []), false);
  assert.equal(runReportsDone([start, ...turn([["mold_record", {}, "record_status: done"]])], 0, []), false); // no action: a read
  assert.equal(runReportsDone([start, ...turn([["mold_record", { action: "path" }, "record_status: done"]])], 0, []), false);
});

t("done: nested statuses in pretty JSON (design 4.3) never count", () => {
  // A merge whose result echoes the record: stages[].status "done" must not end a building run.
  const echo = ["mold_record", { action: "merge", data: { stages: [{ id: "4.3", status: "done" }] } }, moldJson("building")];
  assert.equal(lastReportedStatus([start, ...turn([echo])], 0, []), null);
  // ...and a real done is not hidden by a later nested status.
  const doneThenEcho = [start, ...turn([merge("done")]), ...turn([["mold_record", { action: "merge", data: { checks: [] } }, moldJson("done")]])];
  assert.equal(runReportsDone(doneThenEcho, 0, []), true);
  const tabbed = ["mold_record", { action: "merge", data: { notes: "x" } }, "record_status: building\n\trecord_status: done\n  record_status: done"];
  assert.equal(lastReportedStatus([start, ...turn([tabbed])], 0, []), "building");
  assert.equal(runReportsDone([start, ...turn([["mold_record", { action: "merge", data: {} }, "Status: ok\nstage status: done"]])], 0, []), false);
});

t("done: other tools, errors and earlier turns are ignored; the last write wins", () => {
  assert.equal(runReportsDone([start, ...turn([["execute_csharp", { action: "merge" }, "record_status: done"]])], 0, []), false);
  assert.equal(runReportsDone([start, ...turn([["rebuild_journal", { action: "write" }, "record_status: done"]])], 0, []), false);
  assert.equal(runReportsDone([start, ...turn([["rebuild_journal", { action: "write" }, "record_status: done"]])], 0, ["rebuild_journal"]), true);
  // The merged-line and input fallbacks are mold_record's only.
  assert.equal(runReportsDone([start, ...turn([["rebuild_journal", { action: "merge", data: { status: "done" } }, "merged; status done"]])], 0, ["rebuild_journal"]), false);
  assert.equal(runReportsDone([start, ...turn([[...merge("done"), true]])], 0, []), false); // error result
  const msgs = [start, ...turn([merge("done")])];
  assert.equal(runReportsDone(msgs, 2, []), false); // only messages from `from` on
  assert.equal(lastReportedStatus([start, ...turn([merge("done")]), ...turn([merge("building")])], 0, []), "building");
  assert.equal(lastReportedStatus([start, ...turn([merge("building"), merge("done")])], 0, []), "done");
});

// ---- Workflows menu ----

t("palette sections: categories first (built-ins first), then today's three groups", () => {
  const all = [
    { name: "smart-rebuild", builtIn: true, title: "", description: "", icon: "", updated: "", kind: "tool" },
    { name: "mold", builtIn: true, category: "Molding & Toolmaking", kind: "tool" },
    { name: "grooves", category: " molding  & toolmaking ", kind: "tool" },
    { name: "wf", category: "Molding & Toolmaking", kind: "workflow" },
    { name: "jig", category: "Fixtures", kind: "tool" },
    { name: "export", kind: "workflow" },
    { name: "holes", kind: "tool" },
    { name: "blank", category: "  ", kind: "tool" },
  ];
  const p = paletteSections(all);
  assert.deepEqual(
    p.categories.map((c) => [c.title, c.rows.map((r) => `${r.tool.name}:${r.run}`)]),
    [
      ["Molding & Toolmaking", ["mold:true", "grooves:false", "wf:true"]],
      ["Fixtures", ["jig:false"]],
    ],
  );
  assert.deepEqual(p.workflows.map((x) => x.name), ["export"]);
  assert.deepEqual(p.builtIn.map((x) => x.name), ["smart-rebuild"]);
  assert.deepEqual(p.tools.map((x) => x.name), ["holes", "blank"]);
  assert.deepEqual(categoryNames(all), ["Molding & Toolmaking", "Fixtures"]);
  const none = paletteSections([{ name: "a", kind: "workflow" }, { name: "b", builtIn: true, kind: "tool" }]);
  assert.equal(none.categories.length, 0);
});

// ---- tool forms in the panel (ToolFormCard) ----

t("number boxes show and read numbers as people write them", () => {
  assert.equal(formatNumber(2000), "2000");
  assert.equal(formatNumber(1.5), "1.5");
  assert.equal(formatNumber(137.895145), "137.895");
  assert.equal(formatNumber(0.000123456789), "0.000123457");
  assert.equal(formatNumber(610.0000000001), "610");
  assert.equal(formatNumber(-3.25), "-3.25");
  assert.equal(formatNumber(0), "0");
  assert.equal(formatNumber(null), "");
  assert.equal(parseNumber("1,5"), 1.5);
  assert.equal(parseNumber(" 2 000 "), 2000);
  assert.equal(parseNumber("1,234.5"), 1234.5);
  assert.equal(parseNumber(".5"), 0.5);
  assert.equal(parseNumber("-12"), -12);
  assert.equal(parseNumber("1e3"), 1000);
  assert.equal(parseNumber(""), null);
  assert.equal(parseNumber("12 mm"), null);
  assert.equal(parseNumber("abc"), null);
  assert.equal(stepNumber(0.1, 0.2, 1), 0.3);
  assert.equal(stepNumber(1, 1, -1, 1), 1); // stays at the minimum
  assert.equal(stepNumber(63, undefined, 1, 1, 64), 64);
  assert.equal(stepNumber(64, 1, 1, 1, 64), 64);
});

t("labels for chips, selection and file inputs, table hints", () => {
  assert.equal(chipText({ type: "Face", label: "Boss-Extrude1" }), "Face · Boss-Extrude1");
  assert.equal(chipText({ type: "Edge", label: null }), "Edge");
  assert.equal(takesText(["face"]), "faces");
  assert.equal(takesText(["vertex", "edge"]), "vertices or edges");
  assert.equal(takesText(["edge"], false), "an edge");
  assert.equal(takesText(["face", "sketch_segment"], false), "a face or a sketch segment");
  assert.equal(takesText([]), "entities");
  assert.equal(acceptText(["pdf", "image", "drawing"]), "PDFs, images, drawings");
  assert.equal(acceptText(undefined), "files");
  assert.equal(tableHint({ id: "s", type: "number", label: "", visible: true, enabled: true, required: false, unit: "%", table: 1.5, edited: true }), "Table: 1.5 %");
  assert.equal(tableHint({ id: "t", type: "number", label: "", visible: true, enabled: true, required: false, unit: "°C", table: 170, edited: false }), null);
  assert.equal(tableHint({ id: "c", type: "choice", label: "", visible: true, enabled: true, required: false, table: "Supplier datasheet", edited: true }), "Table: Supplier datasheet");
});

t("the mock form cascades, fills, switches units and hands over on OK", () => {
  const events = [];
  const emit = (name, data) => events.push([name, data]);
  mockOpenToolForm(emit);
  let form = events.at(-1)[1];
  assert.equal(events.at(-1)[0], "toolForm.show");
  const input = (f, id) => f.groups.flatMap((g) => g.inputs).find((i) => i.id === id);
  const send = (change) => (form = mockToolForm("toolForm.change", { formId: form.formId, ...change }, emit));
  assert.deepEqual(mockToolForm("toolForm.list", {}, emit).map((x) => x.formId), [form.formId]);
  assert.equal(input(form, "mold_grade").value, "6061-T651");
  assert.equal(input(form, "press_pressure").unit, "psi");
  assert.equal(Math.round(input(form, "press_pressure").value), 2000);

  send({ id: "mold_material", value: "Tool steel" });
  assert.equal(input(form, "mold_grade").value, "P20 pre-hard");
  assert.equal(input(form, "insert_grade").visible, false);
  send({ id: "part_family", value: "EPDM" });
  assert.equal(input(form, "part_grade").value, "EPDM sulfur 60A");
  assert.equal(input(form, "viscosity_cp").visible, false); // n/a for a rubber
  assert.equal(input(form, "press_temp").value, 170);
  send({ id: "press_temp", value: 175 });
  assert.equal(input(form, "press_temp").edited, true);

  send({ id: "press_pressure", unit: "bar" });
  assert.equal(Math.round(input(form, "press_pressure").value * 10) / 10, 137.9);
  send({ id: "press_pressure", value: 2000 });
  assert.equal(form.okEnabled, false);
  assert.equal(form.okField, "press_pressure");
  assert.match(input(form, "press_pressure").error, /Enter a value from/);
  assert.deepEqual(mockToolForm("toolForm.ok", { formId: form.formId }, emit).closed, false);
  send({ id: "press_pressure", value: 150 });
  assert.equal(form.okEnabled, true);

  send({ id: "daylight", value: 9999 }); // native boxes stay in range
  assert.equal(input(form, "daylight").value, 3000);
  form = mockToolForm("toolForm.pick", { formId: form.formId, id: "critical_faces" }, emit);
  assert.equal(input(form, "critical_faces").items.length, 2);
  send({ id: "critical_faces", remove: 0 });
  assert.equal(input(form, "critical_faces").items.length, 1);
  send({ tier: "high" });
  assert.equal(form.agent.tier, "high");
  assert.ok(visibleGroups(form).length >= 6);

  assert.deepEqual(mockToolForm("toolForm.ok", { formId: form.formId }, emit), { closed: true });
  assert.deepEqual(mockToolForm("toolForm.list", {}, emit), []);
  assert.throws(() => mockToolForm("toolForm.change", { formId: form.formId, id: "cavities", value: 2 }, emit), /closed/);
});

console.log(`${n} checks passed`);
