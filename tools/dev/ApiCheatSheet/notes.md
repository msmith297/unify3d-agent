# Gotchas (curated)

<!-- Maintainers: everything below except this comment and the title goes into the agent's cheat sheet verbatim (see
README.md). Keep it short - the whole sheet has a token budget (members.json "budgetTokens"). Each line is something the
agent got wrong in real chats (Oct 2026: 401 code cells, 83 errors). Marks marked "verified" come from the measured helpers
in src/SwAgent/Mold/Geometry (MoldApi, Holes); keep them in step with those helpers. -->
## Gotchas
- Which interface: FeatureByName and GetBodies2 are on IPartDoc (((IPartDoc)Model).FeatureByName(...)), not IModelDoc2 (CS1061). Use the IFeatureManager versions of FeatureCut4, FeatureExtrusion3, FeatureRevolve2, FeatureFillet3, InsertCombineFeature (the IModelDoc2/IPartDoc ones are old, other parameters, no feature returned), the ISketchManager sketch calls, and IModelDocExtension.SaveAs3.
- Parameters typed SelectData, Body2, Feature, MathTransform take that type, not the I-interface (CS1503): keep what the API returned (SelectData sd = selMgr.CreateSelectData(); sd.Mark = 1;) or cast ((Body2)body).
- Arrays come back as object: object[] for COM objects (((object[])x).Cast<IBody2>()), double[] for numbers ((double[])box). A null means none: check before casting. CurveParamData is an object: (double[])cp.StartPoint. Pass points as new double[] { x, y, z }.
- Units are meters and radians in every call, whatever the document units.
- Marks (verified by the Mold helpers): Move/Copy body Mark 1 (Mark 0 fails); Combine target Mark 1, tools Mark 2, bodies passed as null; split plane Mark 1, body Mark 2 (then Mark 0 for both). Clear the selection before and after.
- InsertMoveCopyBody2: TransDist 0 means TransX/Y/Z is the move; rotation is RotAngleX/Y/Z (radians) about RotPoint. The rotation sign is not certain on this version: check the turned body (GetExtremePoint or the box) and undo and turn the other way if needed - Mold.Orient does exactly this. Rotate and move in separate features.
- FeatureCut4/FeatureExtrusion3 scope: UseFeatScope true + UseAutoSelect true affects every body the feature reaches; to touch one body set UseAutoSelect false with the body selected (the mark it reads is unverified: check other bodies' volumes after, as Mold's holes do). NormalCut is sheet metal only (false). Cut: Flip flips the side to cut, Dir reverses the direction. Merge false makes a new body.
- PostSplitBody2 can return null while still adding the split: compare GetLastFeatureAdded() before and after, never retry blindly (a second split is left in the tree).
- Revolve: the sketch's one centerline is the axis. Angles in radians.
- Fillets: one feature per body (group edges by IEdge.GetBody()); after any feature check it is not null and GetErrorCode2 is 0.
- Bodies by name: IBody2.Name, or SelectByID2(name, "SOLIDBODY", 0, 0, 0, ...). Auto names renumber when bodies are added upstream.
