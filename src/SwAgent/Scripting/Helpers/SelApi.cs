using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Mold.Geometry;
using SwAgent.Scripting.Extensions;

namespace SwAgent.Scripting.Helpers
{
    /// <summary>
    /// Sel.* in code cells [SolidWorks]: selecting anything with a selection mark without knowing which interface
    /// selects it (IBody2.Select2 + SelectData, IFeature.Select2 + mark, IEntity.Select4 ...), checked by reading the
    /// selection back.
    /// </summary>
    public sealed class SelApi
    {
        private readonly ScriptGlobals _g;

        internal SelApi(ScriptGlobals g)
        {
            _g = g;
        }

        private IModelDoc2 Model => _g.Model ?? throw new SwHelperException("No document is open for this chat.");

        [Helper("Select an entity or a list of them (IBody2, IFeature, IFace2, IEdge, IVertex, IComponent2, sketch segment or point, ISketch, a feature name) with a selection mark; checked: each one is in the selection with that mark. Returns the selection count.",
            "IBody2.Select2", "IEntity.Select4", "IFeature.Select2", "IComponent2.Select4", "ISketchSegment.Select4", "ISketchPoint.Select4", "ISelectionMgr.CreateSelectData", "ISelectionMgr.SetSelectedObjectMark")]
        public int Select(object entity, int mark = 0, bool append = false)
        {
            var model = Model;
            var items = Items(entity);
            if (items.Count == 0) throw new SwHelperException("Sel.Select: nothing to select (null or an empty list).");
            if (!append) model.ClearSelection2(true);
            var mgr = model.SelMgr();
            foreach (var item in items)
            {
                var before = mgr.GetSelectedObjectCount2(-1);
                var ok = SelectOne(model, mgr, item, mark);
                var after = mgr.GetSelectedObjectCount2(-1);
                var index = after > before ? after : IndexOf(mgr, item);
                if (!ok && index <= 0) throw new SwHelperException($"Sel.Select: SolidWorks didn't select the {Describe(item)} (it may be hidden, suppressed or consumed by a later feature - get it again).");
                if (index <= 0) throw new SwHelperException($"Sel.Select: the {Describe(item)} isn't in the selection after selecting it.");
                if (mgr.GetSelectedObjectMark(index) != mark)
                {
                    mgr.SetSelectedObjectMark(index, mark, (int)swSelectionMarkAction_e.swSelectionMarkSet);
                    if (mgr.GetSelectedObjectMark(index) != mark)
                        throw new SwHelperException($"Sel.Select: the {Describe(item)} is selected but its mark reads {mgr.GetSelectedObjectMark(index)}, not {mark}.");
                }
            }
            return mgr.GetSelectedObjectCount2(-1);
        }

        [Helper("A SelectData (the class Select2/Select4 take) with this mark.", "ISelectionMgr.CreateSelectData")]
        public SelectData Data(int mark = 0) => Model.CreateSelectData(mark);

        [Helper("Clear the selection.")]
        public void Clear() => Model.ClearSelection2(true);

        [Helper("How many objects are selected (with this mark; -1 any).")]
        public int Count(int mark = -1) => Model.SelMgr().GetSelectedObjectCount2(mark);

        [Helper("The selected object at index (from 1) among those with this mark (-1 any), or null.")]
        public object Get(int index = 1, int mark = -1) => Model.SelMgr().GetSelectedObject6(index, mark);

        [Helper("Every selected object (with this mark; -1 any), in selection order.")]
        public IList<object> All(int mark = -1)
        {
            var mgr = Model.SelMgr();
            var n = mgr.GetSelectedObjectCount2(mark);
            var result = new List<object>();
            for (var i = 1; i <= n; i++) result.Add(mgr.GetSelectedObject6(i, mark));
            return result;
        }

        private List<object> Items(object entity)
        {
            if (entity == null) return new List<object>();
            if (entity is string || Marshal_IsCom(entity) || !(entity is IEnumerable list)) return new List<object> { entity };
            return list.Cast<object>().Where(x => x != null).ToList();
        }

        private bool SelectOne(IModelDoc2 model, ISelectionMgr mgr, object item, int mark)
        {
            var data = mgr.CreateSelectData(mark);
            switch (item)
            {
                case string name:
                    return (model.FeatureByName(name) ?? throw new SwHelperException($"Sel.Select: there is no feature named '{name}'.")).Select2(true, mark);
                case IBody2 b:
                    return b.Select2(true, data);
                case IFeature f:
                    return f.Select2(true, mark);
                case IComponent2 c:
                    return c.Select4(true, data, false);
                case ISketchSegment s:
                    return s.Select4(true, data);
                case ISketchPoint p:
                    return p.Select4(true, data);
                case IEntity e:
                    return e.Select4(true, data);
                case ISketch sk:
                    return ((IFeature)sk).Select2(true, mark);
            }
            throw new SwHelperException($"Sel.Select: don't know how to select a {VariantExtensions.TypeName(item)}.");
        }

        /// <summary>Where an already-selected object sits in the selection (1-based), or 0.</summary>
        private int IndexOf(ISelectionMgr mgr, object item)
        {
            var n = mgr.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= n; i++)
            {
                var o = mgr.GetSelectedObject6(i, -1);
                if (ReferenceEquals(o, item)) return i;
                try
                {
                    if (_g.swApp != null && _g.swApp.IsSame(o, item) == (int)swObjectEquality.swObjectSame) return i;
                }
                catch
                {
                    // not comparable
                }
            }
            return 0;
        }

        private static string Describe(object item) =>
            item is IBody2 b ? $"body '{SwGeo.SafeName(b)}'" : item is IFeature f ? $"feature '{f.Name}'" : item is string s ? $"feature '{s}'" : VariantExtensions.TypeName(item);

        private static bool Marshal_IsCom(object o) => System.Runtime.InteropServices.Marshal.IsComObject(o);
    }
}
