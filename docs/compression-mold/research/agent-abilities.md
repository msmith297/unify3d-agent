# What the agent can do in SolidWorks today, and what's missing for a compression-mold tool

Scope: one multi-body SolidWorks part holding every piece of a heat-press compression mold. The pieces are the cavity plate(s), the force/plunger, the base, the lid, steel inserts, and bolts or bolt holes.
Repo root (R): `C:/Users/msmit/Desktop/Software Projects/Unify3D Agent`.
API signatures below were checked by reflecting over `R/src/SwAgent/bin/Debug/net48/SolidWorks.Interop.sldworks.dll` / `swconst.dll` (the SW2026 interop the add-in loads). Raw dumps are in this folder (`api-dump.txt`, `api-dump2.txt`).

## 0. Verdict in one paragraph

Through `execute_csharp` the agent can already reach the whole COM API. Insert Part, Scale, Combine, Split, Move/Copy Body, the Mold Tools features (parting line, parting surface, tooling split), per-body mass properties, ray casting, `IModeler` interference and Save As STEP are all callable today, with no new C# in the add-in. What it lacks is **packaging and feedback**:
- There are no ready-made, tested helpers for the mold-specific measurements: per-body report, draft and undercut classification as data, air-trap points, and export of each body.
- It can't ask the user to **pick faces mid-run** and get `[S#]` refs back. `ask_user`/`ask_form` answers are text only.
- The overlay can't **fill curved faces or regions**. It draws lines and flat polygons, and temp bodies in one opaque colour.
- Page number boxes have **no pressure, temperature or mass units**, and dropdowns can't depend on each other or fill other fields.
- There's **no MCP client** for the future simulators.

Each gap has a small fix (section 6).

---

## 1. Agent tools registered today (`[AgentTool]`)

All live in `R/src/SwAgent`. "Write" = gated by approval (`ui/src/agent/runAgent.ts:18`, `WRITE_TOOLS = execute_csharp, undo, rebuild`).

| Tool | Where | Inputs | What it does | Use for the mold |
|---|---|---|---|---|
| `execute_csharp` | Scripting/ScriptHost.cs:71 | `title`, `kind` (change/check), `code` (req), `capture_after` | Persistent Roslyn C# REPL per chat on the SW UI thread. Full COM API. One undo step per cell (`StartRecordingUndoObject`/`FinishRecordingUndoObject2`, :242-264). Reports the tree diff (`FeatureDiff`). Modal prompts suppressed (`QuietDialogs`). | Everything: insert, scale, combine, split, plates, bolts, measuring, export |
| `search_sw_api` | Tools/ApiTools.cs:47 | `query` (req), `limit` | Reflection search of the installed interop | Finding exact mold API signatures (InsertMoldCoreCavitySolids etc.) |
| `get_sw_api_type` | Tools/ApiTools.cs:73 | `type_name` (req), `member_filter` | Every member of an interface/enum | Same |
| `get_model_context` | Tools/ModelTools.cs:46 | none | Title, path, units, configs, material, solid/surface body **counts**, whole-part `GetPartBox`, whole-part mass (`GetMassProperties2`), selection | Only whole-part numbers: **no per-body mass or box** |
| `get_feature_tree` | Tools/ModelTools.cs:142 | `max_features`, `include_sub_features` | Tree with types, suppression, errors | Checking build state |
| `get_selection` | Tools/ModelTools.cs:166 | none | Current selection with geometry facts | Reading what the user picked *now*, but gives no persistent refs |
| `list_open_documents` | Tools/ModelTools.cs:177 | none | Open docs | |
| `undo` | Tools/ModelTools.cs:191 | `steps` | `EditUndo2` + tree diff | |
| `rebuild` | Tools/ModelTools.cs:204 | `force` | Rebuild and list feature errors | |
| `show_overlay` | Tools/OverlayTools.cs:63 | `items[]` {`type` line/arrow/polygon/circle/marker/dimension/label, `points` or `ref` "S1", `text`, `color`, `dashed`, `fill`, `radius`, `normal`}, `units` mm/in/m, `append` | Annotations anchored to model coordinates, re-projected as the camera moves, with a depth test | Labels, parting-line outline, bolt circles, air-trap markers |
| `clear_overlay` | Tools/OverlayTools.cs:109 | none | Clears annotations and temp bodies | |
| `select_refs` | Tools/SelectionTools.cs:215 | `ids[]` (req), `append`, `mark` | Selects `[S#]` entities with a selection mark | Feeding user-picked faces into feature calls |
| `capture_views` | Tools/ViewTools.cs:65 | `views[]` (current/isometric/front/...), `max_size`, `zoom_to_fit`, `move_camera`, `highlight[]` (refs; default = current selection), `focus[]`, `hide[]` (components), `direction` | Real screenshot ('current') or off-screen renders from the live mesh. Highlighted faces are drawn **blue** (single colour) | Verifying geometry. The agent can select faces in code and render them highlighted |
| `describe_model_history` | Tools/HistoryTools.cs:36 | `include_sketch_geometry`, `filter` | Full build recipe | Turning a finished mold into a reusable macro |
| `snapshot_design` / `compare_design` | Tools/DesignSnapshot.cs:49 / :72 | `name`, `from_saved_file` / `baseline`, `rebuild` | Per-body volume/area/box (`IBody2.GetMassProperties(1.0)`, `GetBodyBox`, :279-286) as JSON, then a diff | **Closest existing per-body report** (volume and box, no mass or material) |
| `replace_inserted_part` | Tools/DesignSnapshot.cs:88 | `feature`, `path` (req) | Repoints an Insert Part (`IDerivedPartFeatureData.PathName`) | Customer revises the molded part |
| `analyze_design` | Tools/RebuildTools.cs:84 | `feature` | Inserted parts, skeleton, body origins, body-picking features (Combine, Split, Scale, MoveCopyBody... :78-82) | Spotting the multi-body renumbering trap |
| `checkpoint` | Tools/RebuildTools.cs:212 | `action` save/list/restore, `name` | Saves a copy (SaveAs3 Copy). Restore **only works on registered working copies** | Saving known-good stages of a long mold build |
| `rebuild_journal` | Tools/RebuildTools.cs:283 | `action` read/append, `text` | Markdown journal kept outside the file per design | A design log that survives context clearing |
| `save_working_copy` | Tools/RebuildTools.cs:318 | `name` | Save As a copy and register it | |
| `design_library` / `add_to_design_library` | Tools/LibraryTools.cs:57 / :112 | folder, query, root... | Lists and saves Design Library parts | Standard hardware (bolts, dowels, inserts) if the user keeps them there |
| `list_user_tools` / `read_user_tool` / `save_user_tool` / `edit_user_tool` | Tools/UserTools.cs:279/288/306/366 | | Tool authoring | |
| `run_tool_macro` / `test_tool` | Tools/ToolTester.cs:38 / :66 | `name`, `code` / `run_macro` | Runs or tests a tool's macro.cs and preview.cs | Testing the mold tool's own macro |

Client-side (UI) tools: `ask_user`, `ask_form` and `offer_restart` (section 4).

## 2. The `execute_csharp` environment (ScriptGlobals)

`R/src/SwAgent/Scripting/ScriptGlobals.cs`: one instance per chat, so variables survive between cells (ScriptHost.cs:81-96).
- `swApp` (ISldWorks), `Model` (IModelDoc2), `App`/`Doc` (xCAD), `Ct` (cancellation), `WorkDir` (:41, per-chat scratch folder `%LOCALAPPDATA%…/work/<thread>`, good for STEP exports).
- `Agent.CallToolAsync(name, input)` (:148) calls any registered tool from code (not `execute_csharp` itself).
- `Ref("S1")` (:47) resolves a stored persistent reference via `GetObjectByPersistReference3` (SelectionTools.cs:280). `SelectRefs(...)` (:50) and `SelectRefsWithMark(mark, append, ...)` (:53) select them. **Refs only come from the user's messages or tool-page selections.** The agent can't add its own ids to the chat's ref table. It keeps computed faces in REPL variables, or can call `Model.Extension.GetPersistReference3(face)` itself.
- `ShowWork()` (:60), `Focus(...)` (:68), `Refresh()`, `Print()` (:81).
- `Inputs` (:84): the PropertyManager page values in a tool macro. `Inputs.Number/Integer/Flag/Text/Selected<T>/Files` (ToolInputs.cs). Lengths are in metres.
- `Preview(IBody2, color, transparency)` (:93): temp bodies in a tool's preview.cs.
- `Fail(msg)` (:102) shows a status-bar message. `NeedsAgent(why)` (:108) hands a hybrid macro over to the agent.
- `Geo` (:114, Geometry.cs): `EdgePoints`, `LoopPoints`, `Frame(planarFace)` -> u/v/n `PlaneFrame` (`ToUV`, `ToModel`, `Height`), `LoopUV`, `Distance(point|loop, edge|edges|loop)`, `Inside(uv, polygon)`, `Box(uv)`. All edge or planar-face helpers. **None for bodies, mass, draft or ray tests.**
- `Overlay` (:120, OverlayApi.cs): `Line`, `Arrow`, `Polygon(points, color, fill, dashed)`, `Circle(center, normal, r)`, `Marker`, `Dimension`, `Label`, `Body(IBody2 temp, rgb)`, `Clear()`.
- `mm()`, `inch()`, `deg()` (:125-133).
- Imports: System, IO, Linq, Text, Collections.Generic, Threading.Tasks, InteropServices, sldworks, swconst (ScriptHost.cs:294-296). References include System.Drawing, WinForms, Newtonsoft (:270-286). **No math or optimisation library** (no MathNet). The structural plate and bolt calculations must be plain C# or JS.
- Output is capped at 30,000 characters (ScriptHost.cs:29).
- Exceptions keep the changes made before them. Cells aren't time-limited (the user's Stop sets `Ct`).
- Context editing clears old tool calls after about 40k input tokens and keeps the last 6 (runAgent.ts:149-156). Long mold builds will lose early cell outputs, so key numbers must go in a journal or manifest file (see gap G14).

## 3. Overlay: what it can and can't show (UI/SceneOverlay.cs, UI/PreviewDisplay.cs)

- **2D layer** (`SceneOverlay : Form`, :22): a click-through WinForms window over the graphics area. Items are `line | arrow | polygon | circle | marker | dimension | label` (:24-37). Points are in model metres, projected with `IModelView.Transform` on a 30 ms timer (:110).
  - Depth test: when the agent draws, it captures the model mesh (`LiveModelMesh.For`, :317-325). A quarter-resolution depth image (`ComputeHidden`, :352) fades the parts that are behind geometry.
  - Polygons fill with alpha 70, or 30 when mostly hidden (:500).
  - **No "face" or "region" item type.** A polygon is a single flat outline, so a fillet, draft wall or freeform face can't be filled except triangle by triangle.
  - `ref` anchoring only takes one point on a face (the closest point to its box centre, OverlayTools.cs:166-170), not the face's shape.
- **Temp bodies** (`SceneOverlay.ShowBody`, :73 -> `PreviewDisplay.Show`, PreviewDisplay.cs:17): `IBody2.Display3(part, BGR, swTempBodySelectOptionNone)`, parts only.
  - **Transparency is ignored**: the tuple element is discarded at PreviewDisplay.cs:21 (`var (body, color, _)`), so bodies are opaque in one colour.
  - Setting `MaterialPropertyValues2` on temp bodies crashes SW (comment :11-13).
- **Can it highlight faces holographically today?** Only indirectly. The agent can call `face.CreateSheetBody()` (IFace2, returns a temp Body2) and `Overlay.Body(sheet, rgb)`. That gives an opaque coloured sheet lying exactly on the real face, which will z-fight. Alternatively it can select faces and use `capture_views` (blue, offline image only), or `IFace2.Highlight(true)` (transient selection-style highlight, not colour-coded). There's no tested, colour-coded, translucent face or region highlight.
- Safety rule from the code: never call SW geometry from the overlay timer (comment at SceneOverlay.cs:296-301; `RayIntersections` on a timer once crashed SW). Any new analysis must run inside a tool call or cell.

## 4. Asking the user (ui/src/agent/runAgent.ts)

- `ask_user` (:72-86): `{question: string (req), options?: string[]}`. Shows a card and resolves with **a plain string** (`executeTool` :373-377).
- `ask_form` (:21-52): `{title (req), intro?, fields[] (req): {id, label, type: "choice"|"multi"|"text"|"number"|"yesno", options?, default?, help?, required?}}`. It returns JSON keyed by id (`FormSpec`/`FormField` in `ui/src/lib/types.ts:235-249`). **There's no selection, file, table or unit field type.**
- `offer_restart` (:55-69).
- **Can an answer be a SolidWorks selection with `[S#]` refs? No.**
  - `answerQuestion` (store.ts:1503) just resolves the text. `send()` routes a typed answer straight to `answerQuestion` without calling `buildContext` (store.ts:1344-1349), so the selection isn't captured.
  - A voice answer does mention what was selected while speaking, but only as descriptions, with **no refs** ("while answering I selected: Face of Boss-Extrude1", store.ts:807-810).
  - "Send now" steering messages (`queuedBlocks`, store.ts:418) carry text and attachments only, with no `sw_context`.
- Refs are only created when (a) a new prompt is sent (`buildContext`, store.ts:536-588, calling the add-in RPC `selection.snapshot`, AgentController.cs:260) or (b) a tool page selection box is staged (`stageToolPrompt`, store.ts:272-323). They're merged into the thread with `selRefs` (store.ts:915).
- So a **tool page selection box** (filter `face`, ToolPage.cs:771) works at the *start* of the run. That only helps when the molded part is already in the open document. When the user starts from a blank part and attaches the file, the faces don't exist until the agent inserts the part, so a mid-run pick is needed.

## 5. Attachments (how the molded part and drawing reach the agent)

- Chat attachments: `attachmentBlocks` (store.ts:432-445).
  - Images go in as `image` blocks (max 5 MB, `FileAttachments.MaxImageBytes`, ModelFiles/FileAttachments.cs:17).
  - PDFs go in as a native `document` block (max 30 MB, :18). Claude reads the text and page images, so it can read tolerances and notes on a PDF drawing.
  - 3D models go in as `<model_file name= path=>` with a summary plus one six-view JPEG (`modelBlocks`, store.ts:397-402).
- Tool page `files` inputs are staged the same way (store.ts:288-303). Images and PDFs are attached as real blocks, models as summary plus views, and anything else as a path only.
- 3D reading: `ModelFileContext.DescribeAsync` (ModelFiles/ModelFileContext.cs:43).
  - Meshes (`.stl .3mf .obj`) are parsed off-thread.
  - CAD (`.step .stp .iges .igs .x_t .x_b .sldprt .sldasm`, :26) is opened hidden by `SwModelLoader`: `OpenDoc6` ReadOnly|Silent for SW files, `LoadFile4(path,"r",GetImportFileData)` for neutral formats (SwModelLoader.cs:80-90). It reads the facts (units, feature count, solid/surface body counts, material, whole-model mass/volume/area, SwModelLoader.cs:128-160), tessellates, then **closes the file again**.
  - Limits: 300 MB for meshes and 400 MB for CAD; 40 s and 90 s time budgets.
  - The agent gets **views plus numbers plus the file path**, but **no geometry in the document**. To use the part it has to insert it itself (gap G1).
- `.slddrw`, `.dxf` and `.dwg` aren't model or PDF types, so they arrive as **path only** (`kind: "file"`). A SolidWorks drawing's tolerances can't be read unless the user exports a PDF (gap G13).

### Tool page input types (for the inputs.json the tool will need)
Supported types: `selection` (filters face/edge/vertex/feature/body/component/plane/sketch...), `number`/`integer`, `checkbox`, `choice` (combobox), `options` (radio), `text` and `files` (UI/ToolPage.cs:220-330). Spec fields: Id, Type, Label, Tip, Required, Default, Min, Max, Unit, Filter, Multiple, Choices, Accept, Icon, Group (Tools/ToolSpec.cs:17-61).
- `Unit` is only `length` (mm, default max 10000 mm), `angle`, or unitless (ToolPage.cs:243-251). **No MPa/psi, degC/degF, kg/lb or tonnes.**
- `OnComboboxSelectionChanged` (ToolPage.cs:587) only calls `Changed()`. **A family dropdown can't repopulate a subtype dropdown or load lookup values into number boxes.** There's no table or dependency field in the spec.

---

## 6. Gaps for "Design compression mold", with the smallest fix and the API it would use

"Today" means what an agent can already do in `execute_csharp` without help. "Fix" is the smallest addition. Proposed helpers go in ScriptGlobals as a `Mold` (or extended `Geo`) object so macro.cs, preview.cs and agent cells share them. Agent tools are only proposed where a JSON tool call or a UI change is needed.

### G1. Bring the molded part in as a body (blank part plus attached file)
- Today: possible but untested. Each run re-derives it.
- Fix: helper `Mold.InsertPart(path, config="") -> IBody2[]`.
  - Native `.sldprt`: `((IPartDoc)Model).InsertPart3(path, options, config)`, where options = `swInsertPartImportSolids (1) | swInsertPartImportSurfaces (2) | swInsertPartImportCoordinateSystem (256) | swInsertPartDontZoomAll (262144)`. This keeps a link, so `replace_inserted_part` handles revisions later.
  - STEP/IGES/Parasolid: first `swApp.LoadFile4(path, "r", swApp.GetImportFileData(path), ref err)` hidden (as SwModelLoader does). Then `Extension.SaveAs3(WorkDir\<name>.sldprt, ...)`, close it, then `InsertPart3`. The alternative is `IPartDoc.InsertImportedFeature(path, out errors)`, but that has no link and needs verifying.
  - Mesh `.stl` can't become a usable solid reliably, so ask for STEP.
  - Then orient the part so the press pull direction is +Z. Use `IFeatureManager.InsertMoveCopyBody2(tx,ty,tz, dist, rx,ry,rz, ax,ay,az, bCopy:false, 1)` with the body selected, or `IDerivedPartFeatureData.GetMoveFeature()`.
  - Name the body: `IBody2.Name = "Molded part"`. Look bodies up by name, never by index: `analyze_design` explains the renumbering trap (RebuildTools.cs:19-22, 77-82).

### G2. Scale for shrinkage (uniform or per axis)
- Today: possible via `IFeatureManager.InsertScale(short type, bool uniform, x, y, z)`. `type` is `swScaleType_e`: Centroid=0, Origin=1, CoordSys=2. Edit it later with `IScaleFeatureData` (`SetScale`, `IsUniform`, `Bodies`).
  - Trap 1: Scale works on the selected bodies, or on **all bodies** if none are selected. It must run while the molded part is the only body, or with its body explicitly selected.
  - Trap 2: Scale is listed as body-picking in analyze_design (:81).
- Fix: helper `Mold.ScaleForShrink(IBody2 body, double sx, double sy, double sz, origin=Origin)`. It selects the body, calls `InsertScale(1, sx==sy==sz, ...)` and checks the volume ratio afterwards (`IBody2.GetMassProperties(1)[3]`, which should equal `sx*sy*sz`).
  - Factor convention to use: ASTM D955 defines shrinkage S = (L_mold - L_part)/L_mold, so the scale is **1/(1 - S)**, not 1 + S. They differ by S^2, which is 0.01% at S = 1%.
  - Anisotropic shrink (filled grades, flow vs transverse) uses the non-uniform form.
  - **Temp-body preview:** a `MathTransform` only carries a uniform scale (`IMathUtility.ComposeTransform(..., double Scale)`). A non-uniform preview has to use the Scale feature itself, or a hand-built 4x4 for `IBody2.ApplyTransform`, which needs verifying because SW may reject non-orthogonal matrices.

### G3. Cavity: subtract the part from the plate(s), keeping the part
- Today: possible.
  - Copy the part body first: `InsertMoveCopyBody2(..., bCopy:true, NumCopies:1)` with the body selected.
  - Then `IFeatureManager.InsertCombineFeature(opType, mainBody, new object[]{copy})`. **Per the SW API help, opType for this method is `swBodyOperationType_e` (SWBODYCUT = 15902), not `swCombineBodiesOperationType_e` (Subtract = 1).** Verify on first use. `ICombineBodiesFeatureData.OperationType` uses the second enum.
  - Multi-cavity: copy or pattern the part bodies first (`InsertMoveCopyBody2` with NumCopies, or a body pattern), then subtract each copy from the plate.
  - Assembly-style `InsertCavity4` exists only on `IAssemblyDoc` and doesn't apply in one part.
- Fix: helper `Mold.Subtract(IBody2 target, IBody2 tool, bool keepTool=true) -> IBody2`, which does copy, combine, re-find bodies by name and assert that the volume dropped by the tool's volume. `Mold.ArrayCavities(body, n, pitchX, pitchY)` for N cavities.

### G4. Split bodies at a parting surface or plane (plunger/cavity, lid/base, inserts)
- Today: possible, three ways.
  - (a) Split feature: select the trim tool (plane or surface), call `IFeatureManager.PreSplitBody2()` (returns the resulting bodies), then `PostSplitBody2(bodiesToMark, consumeCut:false, origins, savePaths:null, overrideTemplate:"")`. `ISplitBodyFeatureData` edits it later.
  - (b) `IFeatureManager.InsertCutSurface(flip, keepPieceIndex, useFeatScope, autoSelect, bodies, out err)` keeps one side.
  - (c) The real Mold Tools chain, all in one part: `InsertMoldPartingLine(flip)` (`IPartingLineFeatureData` has `DraftAnalysis(angle)`, `GetFacesByType(swDraftAnalysisFaceType_e)`, `Status()` -> `swPartingLineFeatureStatus_e`), then `InsertMoldShutOffSurface()`, then `InsertMoldPartingSurface(radiate, ...)`, then a sketch of the block outline, then **`InsertMoldCoreCavitySolids(dist1, dist2, setback, angle)`**. That last one is the Tooling Split; it creates the core and cavity solids (`IToolingSplitFeatureData`).
- Fix: helper `Mold.SplitByPlane(IBody2 body, IRefPlane plane) -> (IBody2 above, IBody2 below)` and `Mold.SplitBySurface(IBody2 body, IBody2 sheet)`, wrapping Pre/PostSplitBody2 with by-name re-lookup.
  - For compression molds the parting is usually planar plus a flash land. Use the Mold Tools chain (c) only for non-planar partings, and leave it to the agent with `search_sw_api`.
  - Temp-body version for previews: `IBody2.Copy2(true).Operations2(SWBODYCUT=15902, halfSpaceBox, out err)`, where the half-space box comes from `IModeler.CreateBodyFromBox3` and is cut and intersected. That's cheap enough for preview.cs.

### G5. Draft analysis as data, not just colours
- Today:
  - `IModelDoc2.MoldDraftAnalysis(angle, options, colors, shows)` only paints the screen and returns nothing.
  - `IPartingLineFeatureData.DraftAnalysis(angle)` plus `GetFacesByType(0..3)` gives positive / negative / no-draft / straddle faces, but needs a parting-line feature.
- Fix: helper `Mold.Draft(IBody2 body, double[] pull, double minDraftRad) -> list of {face, minAngle, maxAngle, class}`.
  - For each `IFace2` in `body.GetFaces()`, take `GetTessTriangles(true)` and `GetTessNorms()`, with a 1-3 mm sample density. Angle = asin(n·pull). Classes: positive >= min, negative <= -min, "needs draft" when |a| < min, straddle when both signs appear.
  - Report the area of each class: `IFace2.GetArea()` per face, or the triangle-area sums for straddle faces.
  - Pure arithmetic, so it's fast enough for preview.cs.
  - Also expose it as an agent tool `mold_check` (G6/G11) so the agent gets one compact report.

### G6. Undercut detection
- Today: `IFeatureManager.MoldUndercutDetect2(colors..., coordInput, dx,dy,dz)` exists but is a UI command that colours faces. **It returns no data. Don't use it in code.**
- Fix: helper `Mold.Undercuts(IBody2 body, double[] pull, double step=0.002)`.
  - Sample points on each face, offset by 0.01 mm along the face normal.
  - Ray-cast with `Model.Extension.RayIntersections(new[]{body}, pts, dirs, options: swRayPtsOptsTOPOLS|swRayPtsOptsNORMALS (=3), hitRadius: 1e-7, offset: 0, highPrecision: true)`, then `Model.GetRayIntersectionsTopology()` and `GetRayIntersectionsPoints()`.
  - A face sample that faces +pull but hits the body when moving along +pull (or the mirror case for -pull) is **occluded, which means an undercut**.
  - Output: faces with occluded-area %, plus sample points for the overlay.
  - Call it only inside a tool call or cell, never from a timer (SceneOverlay.cs:296-301).
  - Same machinery gives "which side of the parting plane each face belongs to" (core vs cavity assignment).

### G7. Mass, volume and material per body (plate-weight limits, max base and lid weight)
- Today:
  - Whole part only in `get_model_context` (ModelTools.cs:110).
  - Volume and box per body only inside `snapshot_design` (DesignSnapshot.cs:279-286, `GetMassProperties(1.0)` with density 1, so mass = volume).
  - No per-body material.
- Fix: agent tool **`body_report`** (read-only; also as helper `Mold.Report()`). For every solid body (`IPartDoc.GetBodies2(swSolidBody, false)`) it lists:
  - name and material (`IBody2.GetMaterialPropertyName(cfg, out db)`; set it with `IBody2.SetMaterialProperty(cfg, db, name)`, e.g. "6061-T6 (SS)" or a tool steel such as P20/H13 from the user's DB)
  - volume and mass: `IMassProperty2` from `Extension.CreateMassProperty2()` with `SelectedItems = new object[]{body}` and `UseSystemUnits=true`, read `Mass`, `Volume`, `CenterOfMass`. Fallback: `IBody2.GetMassProperties(density)`, where [3] = volume (m³) and [5] = mass (kg).
  - tight AABB, from six `IBody2.GetExtremePoint(±x,±y,±z, out x,out y,out z)` calls. `GetBodyBox()` is documented as not guaranteed tight on curved bodies.
  - optional best-fit box from `IFeatureManager.InsertGlobalBoundingBox2`, but that adds a feature
  - **checks against the page limits**: max weight per machined plate, max bounding box per plate, max total base weight, max lid weight
  - Output as a table plus JSON (for the manifest, G14).

### G8. Interference and clearance between mold pieces
- Today: possible.
  - `IModeler.CheckInterference3(targetBodies, toolBodies, options = swBodyInterference_ReturnInterferingObject (4) [| IncludeCoincidentFaces (2)], out faces1, out faces2, out intersectBodies)`. It works on part bodies; read the volume of the returned bodies with `GetMassProperties`.
  - Minimum distance: `IModelDoc2.ClosestDistance(obj1, obj2, out p1, out p2)`, for things like the bolt hole to cavity wall, minimum steel between cavities, and insert to plate edge.
  - `IInterferenceDetectionMgr` is assembly-only.
- Fix: agent tool **`check_bodies`** taking `{pairs: "all" | [[nameA,nameB]...], min_clearance_mm}`. It returns interfering pairs with volume, and pairs closer than the minimum with their two closest points. It draws those points with `Overlay.Dimension`.

### G9. Export each body to STEP (for the shop and future simulators)
- Today: possible but fiddly.
- Fix: agent tool **`export_bodies`** taking `{bodies: names|"all", folder?, format: "step"|"x_t"|"sldprt", step_ap: 214|242}`. Per body:
  - `IBody2.Copy2(true)`, then `swApp.NewDocument(partTemplate, 0, 0, 0)` with the template from `swApp.GetUserPreferenceStringValue(swDefaultTemplatePart)`
  - `((IPartDoc)newDoc).CreateFeatureFromBody3(copy, false, swCreateFeatureBodyCheck (1))`, then set the material on that body
  - `swApp.SetUserPreferenceIntegerValue(swStepAP, 214)` (restore it afterwards), then `newDoc.Extension.SaveAs3(folder\<body>.step, swSaveAsCurrentVersion, swSaveAsOptions_Silent|Copy, null, null, ref e, ref w)`, then close the temp doc by its title
  - This adds no feature to the user's tree.
  - The alternative `IFeatureManager.CreateSaveBodyFeature(bodies, paths, assemName, consume:false, copyProps)` writes linked `.sldprt` files plus an assembly, but adds a feature.
  - Default folder: next to the part, `<part> - mold bodies/`. Write the manifest (G14) there too.

### G10. Ask the user to select faces mid-run and get `[S#]` refs back (critical surfaces, "no parting line here")
- Today: impossible (section 4).
- Fix, the smallest UI change: a new client tool **`ask_selection`** in runAgent.ts next to `ASK_USER_TOOL`, with schema `{prompt: string (req), types?: ("face"|"edge"|"body"|"vertex")[], min?: number, max?: number, preselect_note?: string}`.
  - The card says "Select the faces in SolidWorks, then press Done (or Skip)".
  - On Done: `rpc("selection.snapshot", {docKey})` (already exists: AgentController.cs:260, used at store.ts:550).
  - Number the items `S{n+1}...` from `Object.keys(thread.selRefs).length` (same rule as store.ts:555), merge them into `thread.selRefs` (as store.ts:915) and resolve the tool with text like `[S7] Face of Imported1 - freeform, area 812 mm², spans ...`, using the same line format as buildContext (store.ts:567).
  - **Pre-selection:** the agent first selects its suggested critical faces in code (`face.Select4(true, data)` or `SelectRefs`). The user adds or removes faces and presses Done, so the suggestions come back as stable refs too.
  - Also add `"selection"` as an `ask_form` field type (FormCard.tsx), using the same snapshot on submit.
  - Add `ask_selection` to `exclude_tools` in `CLEAR_OLD_TOOL_USES` (runAgent.ts:155) so the refs text isn't cleared.
  - If the faces were picked on the tool page instead (part already present), a `selection` input with `Filter: ["face"]` already works.

### G11. Highlight suggested critical surfaces, draft classes and air-trap regions on the model
- Today: lines, polygons, markers and labels only. Temp bodies are opaque, one colour each, and transparency is ignored. There's no face fill.
- Fix (small, in OverlayApi plus SceneOverlay):
  - `Overlay.Faces(IEnumerable<IFace2> faces, string color, string label=null)`: put a new item type `"tris"` in `SceneOverlay.Item`, filled from `IFace2.GetTessTriangles(true)` (float[] of 9 per triangle, metres) and drawn as filled triangles at alpha about 70. The depth test can't sample only vertices: `Samples(i)` needs the triangle centroids. Draw the outline with `Geo.LoopPoints` for each `face.GetLoops()`.
  - Also expose it in `show_overlay` as item type `face` with `ref: "S3"` (resolve via `SelectionTools.Resolve`, then the same triangles).
  - Cap the count around 20k triangles. GDI fill is fine while it only redraws on camera change (`Tick`, :205-208).
  - Legend convention for the mold: red = needs draft or undercut, green = drafted OK, yellow = critical surface (no parting line), cyan = proposed parting line (line item), magenta = air-trap marker.
  - **Air traps:** helper `Mold.AirTraps(IBody2 cavityOrPart, double[] fillDir, double step)` finds local high points of the cavity surface relative to the press closing direction (vertical), meaning closed upward pockets. Method: sample the downward-facing cavity faces, ray-cast upward (`RayIntersections`) and flag samples whose upward ray is blocked by the part's own material, then cluster them. Draw them with `Overlay.Marker` plus `Overlay.Label("air trap - vent?")` and `Overlay.Circle` for the region.
  - Honour the temp-body transparency, or drop the parameter. `PreviewDisplay.Show` discards it (PreviewDisplay.cs:21), so `Preview(..., transparency)` and `Overlay.Body` promise more than they deliver.
  - Translucency might be possible via `IBody2.Display3` option bits or by drawing the body's tessellation through the new `tris` item. The second is safer, given the crash note about material values on temp bodies.

### G12. Structural plate thickness and bolt count/size
- Today: no engineering library. Plain C# in macro.cs works (no MathNet).
- Fix: no SolidWorks API needed. Put pure functions `MoldCalc.Plate(...)` / `MoldCalc.Bolts(...)` in the add-in as a compiled helper class exposed on ScriptGlobals, so macro.cs, preview.cs and agent cells call the same validated code. Don't use a `.cs` file loaded with `#load`: `BuildOptions` (ScriptHost.cs:268-300) sets no SourceResolver or base directory, so `#load` won't resolve. Validate the functions offline against the simulated examples.
  - SolidWorks Simulation (`cosworks` interop in `api/redist`) could check single cases later, but it isn't referenced by the script options (ScriptHost.cs:270-286).

### G13. Reading the engineering drawing (tolerances, critical-surface notes)
- PDF and image: fine today (document or image blocks; Claude reads them).
- `.slddrw`: path only. Fix: in `FileAttachments.EntryAsync`, add `.slddrw`. Open it hidden with `OpenDoc6(path, swDocDRAWING, ReadOnly|Silent)`, `Extension.SaveAs3(temp.pdf)`, close it, and attach the PDF.
  - Optionally a `read_drawing_dims` agent tool that walks `IView.GetFirstDisplayDimension5()` and `IDimension.GetToleranceValues / Tolerance (IDimensionTolerance.Type, GetMinValue/GetMaxValue)` to return dimensions with tolerances as data.
- `.dxf`/`.dwg`: path only. Ask for a PDF.

### G14. Hooks for the future MCP machinability and demolding simulators
- Today: **no MCP anywhere** (grep for "mcp" in ui/src and src/SwAgent finds nothing). Tools are only `[AgentTool]` C# plus three UI tools.
- Fix, now: write a **design manifest** after every stage, at `<part folder>/<part> - mold bodies/mold.json`, or reuse `RebuildTools.DesignDir` (RebuildTools.cs:60). It holds:
  - the inputs (materials, pressure, temperature, cavities, limits)
  - pull/press direction, parting plane or surface, shrink factors
  - each body {name, role: cavity|force|base|lid|insert|part, material, mass, box, STEP path}
  - critical-face refs (persistent-ref base64) and draft and undercut results
  - bolt and plate calculation inputs and outputs
  - the checks performed, plus an empty `"simulations": []` array the simulators append to
- Agent tool `mold_manifest {action: read|write|merge}`, or generalise `rebuild_journal`.
- Later: add an MCP path.
  - Either pass `mcp_servers` to the Messages API from runAgent.ts (the server-side MCP connector; it needs remote, URL-reachable servers, and the betas array is already built at runAgent.ts:192)
  - or a local MCP client in the add-in that registers remote tools into `ToolRegistry`.
  - The TOOL.md procedure then gets a "Verify" phase: export bodies (G9), call the simulator with the manifest plus STEP paths, read violations back, and re-plan.

### G15. Page inputs the mold tool needs that the page can't express (FYI for the inputs.json design)
- Units for pressure, temperature, mass and force: use `Unit: "none"` and put the unit in the label ("Press pressure (MPa)"), or add `Unit: pressure|temperature|mass` to `ToolPage` (`swNumberBox_UnitlessDouble` plus a label, since `swNumberboxUnitType_e` has no pressure or temperature unit).
- Family -> subtype -> property table: needs `DependsOn`/`ChoicesBy` in `InputSpec` plus `OnComboboxSelectionChanged` repopulating with `IPropertyManagerPageCombobox.Clear()/AddItems()`, and writing `IPropertyManagerPageNumberbox.Value` for shrink, viscosity etc. Alternative: one `choice` with combined "PU - 90A cast" entries, with TOOL.md or macro.cs looking up the defaults and the agent confirming via `ask_form`.
- A file input with `Accept: ["model"]` for the part and `["pdf","image"]` for the drawing works today (pattern in tools/builtin/smart-rebuild/inputs.json).

---

## 7. Smallest useful set to build first (ordered)
1. `ask_selection` UI tool (G10): unblocks the critical-surface workflow from a blank part. About 60 lines of TS, no C#.
2. `Mold` helper object in ScriptGlobals with `InsertPart`, `ScaleForShrink`, `Subtract`, `SplitByPlane`, `Draft`, `Undercuts`, `AirTraps` (G1-G6, G11 analysis). This is plain C# around the API calls above, usable by macro.cs, preview.cs and the agent.
3. `Overlay.Faces` and a `face` overlay item (G11).
4. `body_report` (G7) and `check_bodies` (G8) agent tools: read-only, quick to test.
5. `export_bodies` (G9) and the `mold.json` manifest (G14).
6. Page units and dependent dropdowns (G15), or the workaround.
7. `.slddrw` to PDF (G13). MCP later.
