using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Scripting
{
    /// <summary>
    /// What the user entered in a tool's PropertyManager page (or what was already selected), as seen by the tool's
    /// macro and preview code: Inputs.Number("offset"), Inputs.Selected&lt;IFace2&gt;("faces"), Inputs.Text("notes")...
    /// Numbers are SI: m, rad, Pa, degC, kg, N, Pa·s, s; percent stays 0-100 (like the rest of the SolidWorks API).
    /// </summary>
    public sealed class ToolInputs
    {
        public static readonly ToolInputs Empty = new ToolInputs();

        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<SelectedItem>> _selections = new Dictionary<string, List<SelectedItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InputMeta> _meta = new Dictionary<string, InputMeta>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>What the page knows about an input beyond its value (display text, table value, data row, visibility).</summary>
        internal sealed class InputMeta
        {
            /// <summary>The text the agent reads ("2000 psi (13.79 MPa)"); null keeps the raw value.</summary>
            public string Display;

            /// <summary>The value in SI (a number, an array for sizes, or null).</summary>
            public JToken Si;

            /// <summary>The value the data table gave (SI), when another input filled this one.</summary>
            public double? Table;

            public bool Edited;
            public bool Hidden;
            public bool NotApplicable;

            /// <summary>The unit the user saw, for converted numbers ("psi").</summary>
            public string Unit;

            /// <summary>The selected data row of a choice with ChoicesFrom; RowUnique when the entry names exactly one row.</summary>
            public JObject Row;
            public bool RowUnique;
        }

        internal void Set(string id, object value) => _values[id] = value;
        internal void SetSelection(string id, List<SelectedItem> items) => _selections[id] = items;
        internal void SetFiles(string id, List<string> paths) => _files[id] = paths;
        internal void SetMeta(string id, InputMeta meta) => _meta[id] = meta;
        internal InputMeta Meta(string id) => id != null && _meta.TryGetValue(id, out var m) ? m : null;

        public bool Has(string id) => _values.ContainsKey(id) || (_selections.TryGetValue(id, out var s) && s.Count > 0) || (_files.TryGetValue(id, out var f) && f.Count > 0);

        public object this[string id] => _values.TryGetValue(id, out var v) ? v : _selections.TryGetValue(id, out var s) ? s.Select(x => x.Entity).ToList() : null;

        /// <summary>A number box, in SI (meters / radians / Pa / degC / kg / N / Pa·s / s / plain number).</summary>
        public double Number(string id, double fallback = 0) => _values.TryGetValue(id, out var v) && v is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : fallback;

        public int Integer(string id, int fallback = 0) => (int)Math.Round(Number(id, fallback));

        /// <summary>An Optional number (SI): null when its checkbox is off, the data says n/a, or it is hidden.</summary>
        public double? Limit(string id) =>
            _values.TryGetValue(id, out var v) && v is IConvertible c && !(v is string) && !(v is bool) ? c.ToDouble(CultureInfo.InvariantCulture) : (double?)null;

        /// <summary>A size input: its 2 or 3 lengths in meters; null when it is off (Optional) or hidden.</summary>
        public double[] Size(string id) => _values.TryGetValue(id, out var v) && v is double[] d ? d.ToArray() : null;

        /// <summary>The data row selected in a choice with ChoicesFrom (null otherwise).</summary>
        public JObject Row(string id) => Meta(id)?.Row;

        /// <summary>The text the agent reads for the input ("2000 psi (13.79 MPa)"); the plain value when there is none.</summary>
        public string Display(string id) => Meta(id)?.Display ?? (_values.TryGetValue(id, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null);

        /// <summary>The value a data table filled in (SI), or null.</summary>
        public double? Table(string id) => Meta(id)?.Table;

        /// <summary>The user changed a value the data table filled in.</summary>
        public bool Edited(string id) => Meta(id)?.Edited == true;

        /// <summary>The input wasn't shown (ShowWhen not met, or n/a for the selected data row); its value is left out.</summary>
        public bool Hidden(string id) => Meta(id)?.Hidden == true;

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
