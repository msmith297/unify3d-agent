# Gotchas (curated)

<!-- Maintainers: everything below except this comment and the title goes into the agent's cheat sheet verbatim (see
README.md). Keep it short - the whole sheet has a token budget (members.json "budgetTokens"). Each line is something the
agent got wrong in real chats (Oct 2026: 401 code cells, 83 errors). Marks marked "verified" come from the measured helpers
in src/SwAgent/Mold/Geometry (MoldApi, Holes); keep them in step with those helpers. -->
## Gotchas
- Which interface: FeatureByName and GetBodies2 are on IPartDoc (((IPartDoc)Model).FeatureByName(...)), not IModelDoc2 (CS1061); the built-in extensions Model.FeatureByName(name) and Model.Bodies() do it for you. Use the IFeatureManager versions of FeatureCut4, FeatureExtrusion3, FeatureRevolve2, FeatureFillet3, InsertCombineFeature (the IModelDoc2/IPartDoc ones are old, other parameters, no feature returned), the ISketchManager sketch calls, and IModelDocExtension.SaveAs3.
- Parameters typed SelectData, Body2, Feature, MathTransform take that type, not the I-interface (CS1503): keep what the API returned (SelectData sd = selMgr.CreateSelectData(); sd.Mark = 1;) or cast ((Body2)body).
- Arrays come back as object: object[] for COM objects (((object[])x).Cast<IBody2>()), double[] for numbers ((double[])box). A null means none: check before casting. CurveParamData is an object: (double[])cp.StartPoint. x.AsDoubles() / x.AsObjects<IBody2>() convert any of these (null gives []). Pass points as new double[] { x, y, z }.
- Units are meters and radians in every call, whatever the document units.
- Marks (verified by the Mold helpers): Move/Copy body Mark 1 (Mark 0 fails); Combine target Mark 1, tools Mark 2, bodies passed as null; split plane Mark 1, body Mark 2 (then Mark 0 for both). Clear the selection before and after.
- InsertMoveCopyBody2: which RotAngle slot turns about which axis, and the sign, is not certain on this version (the chats saw RotAngleX turn about Y): use Bodies.Rotate / Move / Copy, which find it by measurement.
- Feature scope: UseFeatScope true + UseAutoSelect true cuts every body the feature reaches; Features.Cut(sketch, depth, scope) limits it to the bodies given and checks the others are untouched. Merge false makes a new body.
- PostSplitBody2 can return null while still adding the split: compare GetLastFeatureAdded() before and after, never retry blindly (a second split is left in the tree).
- Revolve: the sketch's one centerline is the axis. Angles in radians.
- After any feature call check the feature is not null and GetErrorCode2 is 0.
- Components (assemblies): no helper yet. To turn or move one, build the transform with IMathUtility.CreateTransform (rotation about the pivot plus translation), combine it with comp.Transform2 (IMathTransform.Multiply) and set Transform2 or call SetTransformAndSolve2; a fixed component (IsFixed) or its mates can hold it. The multiplication order is unverified here: check comp.GetBox(false, false) before and after, and undo if it went wrong.
- Bodies by name: IBody2.Name, or SelectByID2(name, "SOLIDBODY", 0, 0, 0, ...). Auto names renumber when bodies are added upstream.
