using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwAgent.Scripting
{
    /// <summary>
    /// What the user entered in a tool's PropertyManager page (or what was already selected), as seen by the tool's
    /// macro and preview code: Inputs.Number("offset"), Inputs.Selected&lt;IFace2&gt;("faces"), Inputs.Text("notes")...
    /// Lengths are in meters and angles in radians, like the rest of the SolidWorks API.
    /// </summary>
    public sealed class ToolInputs
    {
        public static readonly ToolInputs Empty = new ToolInputs();

        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<SelectedItem>> _selections = new Dictionary<string, List<SelectedItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One entity picked in a selection box, with what the agent needs to refer to it later.</summary>
        public sealed class SelectedItem
        {
            public object Entity { get; set; }
            public string Type { get; set; }
            public string Label { get; set; }
            public string Detail { get; set; }

            /// <summary>SolidWorks persistent reference (base64), so the agent can resolve it as [S1].</summary>
            public string Ref { get; set; }
        }

        internal void Set(string id, object value) => _values[id] = value;
        internal void SetSelection(string id, List<SelectedItem> items) => _selections[id] = items;
        internal void SetFiles(string id, List<string> paths) => _files[id] = paths;

        public bool Has(string id) => _values.ContainsKey(id) || (_selections.TryGetValue(id, out var s) && s.Count > 0) || (_files.TryGetValue(id, out var f) && f.Count > 0);

        public object this[string id] => _values.TryGetValue(id, out var v) ? v : _selections.TryGetValue(id, out var s) ? s.Select(x => x.Entity).ToList() : null;

        /// <summary>A number box (meters / radians / plain number).</summary>
        public double Number(string id, double fallback = 0) => _values.TryGetValue(id, out var v) && v is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : fallback;

        public int Integer(string id, int fallback = 0) => (int)Math.Round(Number(id, fallback));

        /// <summary>A checkbox.</summary>
        public bool Flag(string id, bool fallback = false) => _values.TryGetValue(id, out var v) && v is bool b ? b : fallback;

        /// <summary>A text box, or the chosen entry of a dropdown / option group.</summary>
        public string Text(string id, string fallback = "") => _values.TryGetValue(id, out var v) && v != null ? v.ToString() : fallback;

        /// <summary>The entities in a selection box (IFace2, IEdge, IVertex, IFeature, IBody2, IComponent2...).</summary>
        public IReadOnlyList<T> Selected<T>(string id) where T : class =>
            _selections.TryGetValue(id, out var s) ? s.Select(x => x.Entity as T).Where(x => x != null).ToList() : new List<T>();

        public IReadOnlyList<object> Selected(string id) => Selected<object>(id);

        internal IReadOnlyList<SelectedItem> SelectedItems(string id) =>
            _selections.TryGetValue(id, out var s) ? s : (IReadOnlyList<SelectedItem>)new SelectedItem[0];

        /// <summary>Full paths of the files added to a file input.</summary>
        public IReadOnlyList<string> Files(string id) => _files.TryGetValue(id, out var f) ? f : new List<string>();

        internal IEnumerable<string> FileIds => _files.Keys;
        internal IEnumerable<string> SelectionIds => _selections.Keys;
        internal IEnumerable<KeyValuePair<string, object>> Values => _values;
    }
}
