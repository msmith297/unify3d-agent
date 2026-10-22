using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SwAgent.Mold.Geometry;
using SwAgent.Scripting.Helpers;

namespace SwAgent.Scripting.Extensions
{
    /// <summary>
    /// Measurements on bodies and features (<c>body.Box()</c>, <c>body.Volume()</c>, <c>feature.Bodies()</c>), and the
    /// interop's interface/class mismatches made to compile: the API takes the class-named types (SelectData,
    /// MathTransform, ModelDoc2, SketchRelation), cells hold the I-named ones - they are the same COM object.
    /// </summary>
    public static class EntityExtensions
    {
        // ---------- measuring ----------

        /// <summary>Tight box in model meters [x0, y0, z0, x1, y1, z1] from six extreme points (GetBodyBox isn't tight on curved bodies).</summary>
        [Helper("Tight model box [x0, y0, z0, x1, y1, z1] in meters.", "IBody2.GetBodyBox", "IBody2.GetExtremePoint")]
        public static double[] Box(this IBody2 body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body), "Box: the body is null.");
            var r = new double[6];
            for (var k = 0; k < 3; k++)
            {
                foreach (var sign in new[] { -1, 1 })
                {
                    var d = new double[3];
                    d[k] = sign;
                    if (!body.GetExtremePoint(d[0], d[1], d[2], out var x, out var y, out var z))
                        return body.GetBodyBox().AsDoubles(); // the loose box rather than nothing
                    r[sign < 0 ? k : k + 3] = new[] { x, y, z }[k];
                }
            }
            return r;
        }

        /// <summary>Volume in m^3 (mass properties at density 1); NaN when SolidWorks can't measure it.</summary>
        [Helper("Volume in m^3.", "IBody2.GetMassProperties")]
        public static double Volume(this IBody2 body) => SwGeo.Volume(body ?? throw new ArgumentNullException(nameof(body), "Volume: the body is null."));

        /// <summary>Surface area in m^2; NaN when SolidWorks can't measure it.</summary>
        [Helper("Surface area in m^2.")]
        public static double Area(this IBody2 body)
        {
            var mp = (body ?? throw new ArgumentNullException(nameof(body), "Area: the body is null.")).GetMassProperties(1.0).AsDoubles();
            return mp.Length > 4 ? mp[4] : double.NaN;
        }

        /// <summary>Centre of mass (uniform density) in model meters [x, y, z], or null when it can't be measured.</summary>
        [Helper("Centre of mass [x, y, z] in meters.")]
        public static double[] Centroid(this IBody2 body) => SwGeo.Centroid(body ?? throw new ArgumentNullException(nameof(body), "Centroid: the body is null."));

        /// <summary>
        /// The bodies a feature made or changed: a body folder's bodies, else the bodies its faces lie on. (A feature that
        /// only consumed bodies, e.g. a Delete Body, has none.)
        /// </summary>
        [Helper("The bodies a feature made or changed (a body folder: its bodies).", "IBodyFolder.GetBodies")]
        public static IList<IBody2> Bodies(this IFeature feature)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature), "Bodies: the feature is null.");
            if (feature.GetTypeName2() == "SubAtomFolder" && feature.GetSpecificFeature2() is IBodyFolder folder)
                return folder.GetBodies().AsObjects<IBody2>().ToList();
            return SwGeo.BodiesOf(feature);
        }

        /// <summary>The feature's display dimensions (IDisplayDimension objects), from GetFirstDisplayDimension/GetNextDisplayDimension.</summary>
        [Helper("The feature's display dimensions as an array.", "IFeature.GetFirstDisplayDimension", "IFeature.GetNextDisplayDimension")]
        public static object[] GetDisplayDimensions(this IFeature feature)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature), "GetDisplayDimensions: the feature is null.");
            var result = new List<object>();
            for (var d = feature.GetFirstDisplayDimension(); d != null && result.Count < 10000; d = feature.GetNextDisplayDimension(d)) result.Add(d);
            return result.ToArray();
        }

        // ---------- interface vs class arguments ----------

        /// <summary><c>body.Select2(append, selectData)</c> with an ISelectData.</summary>
        public static bool Select2(this IBody2 body, bool append, ISelectData data) => body.Select2(append, (SelectData)data);

        /// <summary><c>entity.Select4(append, selectData)</c> with an ISelectData.</summary>
        public static bool Select4(this IEntity entity, bool append, ISelectData data) => entity.Select4(append, (SelectData)data);

        /// <summary><c>segment.Select4(append, selectData)</c> with an ISelectData.</summary>
        public static bool Select4(this ISketchSegment segment, bool append, ISelectData data) => segment.Select4(append, (SelectData)data);

        /// <summary><c>point.Select4(append, selectData)</c> with an ISelectData.</summary>
        public static bool Select4(this ISketchPoint point, bool append, ISelectData data) => point.Select4(append, (SelectData)data);

        /// <summary><c>component.Select4(append, selectData, showPopup)</c> with an ISelectData.</summary>
        public static bool Select4(this IComponent2 component, bool append, ISelectData data, bool showPopup) => component.Select4(append, (SelectData)data, showPopup);

        /// <summary><c>face.Select4(append, data)</c>: faces select through IEntity.</summary>
        public static bool Select4(this IFace2 face, bool append, SelectData data) => ((IEntity)face).Select4(append, data);

        /// <summary><c>face.Select4(append, data)</c> with an ISelectData.</summary>
        public static bool Select4(this IFace2 face, bool append, ISelectData data) => ((IEntity)face).Select4(append, (SelectData)data);

        /// <summary><c>edge.Select4(append, data)</c>: edges select through IEntity.</summary>
        public static bool Select4(this IEdge edge, bool append, SelectData data) => ((IEntity)edge).Select4(append, data);

        /// <summary><c>edge.Select4(append, data)</c> with an ISelectData.</summary>
        public static bool Select4(this IEdge edge, bool append, ISelectData data) => ((IEntity)edge).Select4(append, (SelectData)data);

        /// <summary><c>vertex.Select4(append, data)</c>: vertices select through IEntity.</summary>
        public static bool Select4(this IVertex vertex, bool append, SelectData data) => ((IEntity)vertex).Select4(append, data);

        /// <summary><c>vertex.Select4(append, data)</c> with an ISelectData.</summary>
        public static bool Select4(this IVertex vertex, bool append, ISelectData data) => ((IEntity)vertex).Select4(append, (SelectData)data);

        /// <summary><c>body.ApplyTransform(transform)</c> with an IMathTransform (for temporary bodies; a feature body moves back on rebuild).</summary>
        public static bool ApplyTransform(this IBody2 body, IMathTransform transform) => body.ApplyTransform((MathTransform)transform);

        /// <summary><c>sketch.RelationManager.DeleteRelation(relation)</c> with an ISketchRelation.</summary>
        public static bool DeleteRelation(this ISketchRelationManager manager, ISketchRelation relation) => manager.DeleteRelation((SketchRelation)relation);

        private static readonly Lazy<Type[]> AccessSelectionsTypes = new Lazy<Type[]>(() =>
            typeof(ISldWorks).Assembly.GetTypes()
                .Where(t => t.IsInterface && t.Name.StartsWith("I", StringComparison.Ordinal) && AccessMethod(t) != null)
                .ToArray());

        private static MethodInfo AccessMethod(Type t) => t.GetMethods().FirstOrDefault(m => m.Name == "AccessSelections" && m.GetParameters().Length == 2);

        /// <summary>
        /// <c>featureData.AccessSelections(Model, null)</c> on any feature-definition object (they take ModelDoc2 and
        /// Component2, cells hold IModelDoc2). Call ReleaseSelectionAccess() when done.
        /// </summary>
        public static bool AccessSelections(this object featureData, IModelDoc2 model, IComponent2 component)
        {
            if (featureData == null) throw new ArgumentNullException(nameof(featureData), "AccessSelections: the feature data is null (GetDefinition() returned nothing).");
            foreach (var t in AccessSelectionsTypes.Value)
            {
                if (!t.IsInstanceOfType(featureData)) continue;
                var m = AccessMethod(t);
                try
                {
                    return (bool)m.Invoke(featureData, new object[] { model, component });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }
            throw new InvalidCastException($"AccessSelections: a {VariantExtensions.TypeName(featureData)} has no AccessSelections (is it the feature's GetDefinition()?).");
        }
    }
}
