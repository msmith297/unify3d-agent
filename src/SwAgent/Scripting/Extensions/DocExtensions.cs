using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Mold.Geometry;
using SwAgent.Scripting.Helpers;

namespace SwAgent.Scripting.Extensions
{
    /// <summary>
    /// The calls agents write on Model that the interop puts elsewhere: <c>Model.FeatureByName("Sketch1")</c> (it is on
    /// IPartDoc / IAssemblyDoc), <c>Model.CreateSelectData(2)</c>, <c>Model.SelectionManager.GetSelectedObject6(1, -1)</c>
    /// (SelectionManager is typed object), <c>Model.Bodies()</c>.
    /// </summary>
    public static class DocExtensions
    {
        /// <summary>A feature by its tree name in a part, assembly or drawing (IPartDoc/IAssemblyDoc/IDrawingDoc.FeatureByName), or null.</summary>
        [Helper("A feature by its tree name, or null (FeatureByName lives on IPartDoc/IAssemblyDoc/IDrawingDoc, not IModelDoc2).", "IPartDoc.FeatureByName", "IAssemblyDoc.FeatureByName", "IDrawingDoc.FeatureByName")]
        public static IFeature FeatureByName(this IModelDoc2 model, string name)
        {
            if (model == null) throw new ArgumentNullException(nameof(model), "FeatureByName: no document (Model is null).");
            switch (model.GetType())
            {
                case (int)swDocumentTypes_e.swDocPART:
                    return ((IPartDoc)model).FeatureByName(name) as IFeature;
                case (int)swDocumentTypes_e.swDocASSEMBLY:
                    return ((IAssemblyDoc)model).FeatureByName(name) as IFeature;
                case (int)swDocumentTypes_e.swDocDRAWING:
                    return ((IDrawingDoc)model).FeatureByName(name) as IFeature;
            }
            return null;
        }

        /// <summary>The selection manager, typed (Model.SelectionManager is object).</summary>
        [Helper("The selection manager, typed (Model.SelectionManager is object).", "IModelDoc2.SelectionManager")]
        public static ISelectionMgr SelMgr(this IModelDoc2 model) =>
            (model ?? throw new ArgumentNullException(nameof(model), "SelMgr: no document (Model is null).")).SelectionManager as ISelectionMgr ??
            throw new InvalidOperationException("SelMgr: this document has no selection manager.");

        /// <summary>A SelectData (the class Select2/Select4 take, not ISelectData) with this selection mark.</summary>
        [Helper("A SelectData (the class Select2/Select4 take) with a selection mark.", "ISelectionMgr.CreateSelectData")]
        public static SelectData CreateSelectData(this IModelDoc2 model, int mark = 0) => model.SelMgr().CreateSelectData(mark);

        /// <summary>A SelectData with this selection mark: <c>selMgr.CreateSelectData(2)</c>.</summary>
        public static SelectData CreateSelectData(this ISelectionMgr selectionManager, int mark)
        {
            var data = (selectionManager ?? throw new ArgumentNullException(nameof(selectionManager))).CreateSelectData() ??
                       throw new InvalidOperationException("CreateSelectData returned nothing.");
            data.Mark = mark;
            return data;
        }

        // Model.SelectionManager is typed object: these make the obvious calls on it compile and run.

        /// <summary><c>Model.SelectionManager.CreateSelectData()</c>: SelectData with a mark.</summary>
        public static SelectData CreateSelectData(this object selectionManager, int mark = 0) => AsSelMgr(selectionManager, "CreateSelectData").CreateSelectData(mark);

        /// <summary><c>Model.SelectionManager.GetSelectedObject6(index, mark)</c> (index from 1, mark -1 = any).</summary>
        public static object GetSelectedObject6(this object selectionManager, int index, int mark) => AsSelMgr(selectionManager, "GetSelectedObject6").GetSelectedObject6(index, mark);

        /// <summary><c>Model.SelectionManager.GetSelectedObjectCount2(mark)</c> (mark -1 = any).</summary>
        public static int GetSelectedObjectCount2(this object selectionManager, int mark) => AsSelMgr(selectionManager, "GetSelectedObjectCount2").GetSelectedObjectCount2(mark);

        /// <summary><c>Model.SelectionManager.GetSelectedObjectType3(index, mark)</c>: an swSelectType_e value.</summary>
        public static int GetSelectedObjectType3(this object selectionManager, int index, int mark) => AsSelMgr(selectionManager, "GetSelectedObjectType3").GetSelectedObjectType3(index, mark);

        /// <summary><c>Model.SelectionManager.GetSelectedObjectMark(index)</c>.</summary>
        public static int GetSelectedObjectMark(this object selectionManager, int index) => AsSelMgr(selectionManager, "GetSelectedObjectMark").GetSelectedObjectMark(index);

        private static ISelectionMgr AsSelMgr(object o, string call) =>
            o as ISelectionMgr ?? throw new InvalidCastException(
                $"{call}: this is a {VariantExtensions.TypeName(o)}, not a selection manager. Use Model.SelMgr().{call}(...) or ((ISelectionMgr)Model.SelectionManager).{call}(...).");

        /// <summary>The part's bodies (solids by default; swBodyType_e.swSheetBody for surfaces). Empty for other documents.</summary>
        [Helper("The part's bodies as a list (solids by default, swBodyType_e.swSheetBody for surfaces).", "IPartDoc.GetBodies2")]
        public static IList<IBody2> Bodies(this IModelDoc2 model, swBodyType_e type = swBodyType_e.swSolidBody, bool visibleOnly = false)
        {
            if (!(model is IPartDoc part) || model.GetType() != (int)swDocumentTypes_e.swDocPART) return new List<IBody2>();
            return part.GetBodies2((int)type, visibleOnly).AsObjects<IBody2>().ToList();
        }

        /// <summary>A body of the part by its name (solids, then surfaces), or null.</summary>
        [Helper("A body by name, or null.")]
        public static IBody2 Body(this IModelDoc2 model, string name) =>
            model.Bodies().Concat(model.Bodies(swBodyType_e.swSheetBody)).FirstOrDefault(b => string.Equals(SwGeo.SafeName(b), name, StringComparison.Ordinal));

        /// <summary>Every feature in tree order, sub-features included (sketches under features, body folders...).</summary>
        [Helper("Every feature in tree order, sub-features included.", "IModelDoc2.FirstFeature", "IFeature.GetNextFeature", "IFeature.GetFirstSubFeature", "IFeature.GetNextSubFeature")]
        public static IList<IFeature> Features(this IModelDoc2 model) => SwGeo.AllFeatures(model ?? throw new ArgumentNullException(nameof(model))).ToList();

        /// <summary>The feature added last (IModelDocExtension.GetLastFeatureAdded), or null.</summary>
        [Helper("The feature added last, or null.", "IModelDocExtension.GetLastFeatureAdded")]
        public static IFeature LastFeature(this IModelDoc2 model) => model?.Extension?.GetLastFeatureAdded() as IFeature;
    }
}
