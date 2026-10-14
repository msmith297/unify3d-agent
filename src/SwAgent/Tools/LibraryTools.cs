using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// The SolidWorks Design Library: the folders listed under Tools > Options > File Locations > Design Library,
    /// shown in the Design Library task pane. Parts saved there can be dragged into any assembly, and user tools can
    /// refer to them by path ("use the 1/2-13 insert from Mold Hardware Components").
    /// </summary>
    public sealed class LibraryTools
    {
        private static readonly string[] LibraryExtensions = { ".sldprt", ".sldasm", ".sldlfp", ".sldblk", ".sldftp" };

        public sealed class ListInput
        {
            [ToolParam("Sub-folder to list, relative to the library root (e.g. \"mold hardware components\"). Default: the root.")]
            public string Folder { get; set; }

            [ToolParam("Only items whose name contains this text; searches all sub-folders.")]
            public string Query { get; set; }

            [ToolParam("Which library root: its number from the list (1, 2, ...) or an absolute folder path. Default: the first one that exists.")]
            public string Root { get; set; }
        }

        public sealed class AddInput
        {
            [ToolParam("Library sub-folder to save into, e.g. \"mold hardware components\" (created if missing). Use the user's wording; nested folders with '/'.", Required = true)]
            public string Folder { get; set; }

            [ToolParam("What to save: a reference id from the user's message (\"S1\" on the component or one of its faces) or a component name " +
                       "(\"insert-1\"). Default: the chat's document if it is a part, otherwise the one selected component.")]
            public string Source { get; set; }

            [ToolParam("File name without folder, e.g. \"Threaded insert 1-2-13 x 18.9.sldprt\". Default: the source's own file name. " +
                       "Prefer a descriptive name a person browsing the library would recognise.")]
            public string FileName { get; set; }

            [ToolParam("Which library root: its number from design_library (1, 2, ...) or an absolute folder path. Default: the first one that exists.")]
            public string Root { get; set; }

            [ToolParam("Replace a library file with the same name (default false).")]
            public bool? Overwrite { get; set; }

            [ToolParam("Also add Root to SolidWorks' Design Library folder list (Options > File Locations) if it isn't there. " +
                       "This changes the user's SolidWorks settings: only set it after the user agreed.")]
            public bool? RegisterRoot { get; set; }
        }

        [AgentTool("design_library",
            "The user's SolidWorks Design Library: the configured root folders (whether each exists) and the sub-folders and " +
            "parts/assemblies inside one of them, with full paths for inserting (IAssemblyDoc.AddComponent5). Use it before " +
            "adding to the library, and when a tool or request says to use a standard part from the library.")]
        public ToolResult List(ToolContext ctx, ListInput input)
        {
            var roots = Roots(ctx.Sw);
            var sb = new StringBuilder();
            sb.AppendLine("Design Library folders (SolidWorks Options > File Locations > Design Library):");
            if (roots.Count == 0) sb.AppendLine("  (none configured)");
            for (var i = 0; i < roots.Count; i++) sb.AppendLine($"  {i + 1}. {roots[i]}{(Directory.Exists(roots[i]) ? "" : "  [MISSING - folder not found / drive not connected]")}");

            string root;
            try
            {
                root = PickRoot(roots, input.Root, mustExist: true);
            }
            catch (ToolException ex)
            {
                return ToolResult.Ok(sb.AppendLine().AppendLine(ex.Message).ToString());
            }

            var dir = string.IsNullOrWhiteSpace(input.Folder) ? root : SafeSub(root, input.Folder);
            sb.AppendLine().AppendLine($"Contents of {dir}:");
            if (!Directory.Exists(dir)) return ToolResult.Ok(sb.AppendLine("  (folder does not exist yet)").ToString());

            var query = (input.Query ?? "").Trim();
            const int max = 300;
            var shown = 0;
            if (query.Length == 0)
            {
                foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown++ >= max) break;
                    sb.AppendLine($"  [folder] {Path.GetFileName(d)}/");
                }
                foreach (var f in LibraryFiles(dir, SearchOption.TopDirectoryOnly))
                {
                    if (shown++ >= max) break;
                    sb.AppendLine($"  {Path.GetFileName(f)}  ->  {f}");
                }
            }
            else
            {
                foreach (var f in LibraryFiles(dir, SearchOption.AllDirectories).Where(f => f.Substring(dir.Length).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (shown++ >= max) break;
                    sb.AppendLine($"  {f.Substring(dir.Length).TrimStart('\\')}  ->  {f}");
                }
            }
            if (shown == 0) sb.AppendLine(query.Length == 0 ? "  (empty)" : $"  (nothing matching '{query}')");
            if (shown > max) sb.AppendLine($"  ... more than {max} items; narrow with folder or query.");
            return ToolResult.Ok(sb.ToString());
        }

        [AgentTool("add_to_design_library",
            "Save a copy of a part or assembly into the user's SolidWorks Design Library so it can be reused (dragged into " +
            "assemblies from the Design Library pane, or inserted by tools). The open document is not renamed or changed. " +
            "Reports the saved path and any external references the copy still has.")]
        public ToolResult Add(ToolContext ctx, AddInput input)
        {
            var model = ctx.RequireModel();
            var (source, label) = ResolveSource(ctx, model, input.Source);
            var sourceType = (swDocumentTypes_e)source.GetType();
            if (sourceType == swDocumentTypes_e.swDocDRAWING) throw new ToolException("Drawings can't be added to the Design Library; select the part or assembly.");

            var roots = Roots(ctx.Sw);
            var root = PickRoot(roots, input.Root, mustExist: false);
            var dir = SafeSub(root, input.Folder);

            var ext = sourceType == swDocumentTypes_e.swDocASSEMBLY ? ".sldasm" : ".sldprt";
            var name = string.IsNullOrWhiteSpace(input.FileName)
                ? Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(source.GetPathName()) ? source.GetTitle() : source.GetPathName())
                : input.FileName.Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length == 0) throw new ToolException("The file name is empty after removing characters Windows doesn't allow.");
            var target = Path.Combine(dir, name + ext);

            if (File.Exists(target) && input.Overwrite != true)
            {
                throw new ToolException($"'{target}' already exists in the library. Choose another file name, or set overwrite after the user confirms replacing it.");
            }
            if (sourceType == swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new ToolException("Adding assemblies isn't supported yet (their component files would need copying too). Add the individual parts instead.");
            }

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new ToolException($"Can't create '{dir}': {ex.Message}. The library folder may be read-only or on a drive that isn't connected.");
            }

            int errors = 0, warnings = 0;
            var ok = source.Extension.SaveAs3(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Copy | swSaveAsOptions_e.swSaveAsOptions_Silent), null, null, ref errors, ref warnings);
            if (!ok || !File.Exists(target))
            {
                throw new ToolException($"SolidWorks couldn't save the copy to '{target}' (errors: {(swFileSaveError_e)errors}, warnings: {(swFileSaveWarning_e)warnings}).");
            }

            var registered = false;
            if (input.RegisterRoot == true && !roots.Any(r => SamePath(r, root)))
            {
                var list = string.Join(";", roots.Concat(new[] { root }));
                registered = ctx.Sw.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDesignLibrary, list);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Saved a copy of {label} to:");
            sb.AppendLine($"  {target}");
            sb.AppendLine($"  library folder: {RelativeTo(root, dir)}  (root: {root})");
            if (warnings != 0) sb.AppendLine($"  SolidWorks warnings: {(swFileSaveWarning_e)warnings}");
            if (registered) sb.AppendLine($"  Added {root} to SolidWorks' Design Library folders.");
            else if (!roots.Any(r => SamePath(r, root))) sb.AppendLine($"  Note: {root} is not one of SolidWorks' Design Library folders, so the part won't show in the Design Library pane until it is added.");

            var refs = ExternalRefs(source);
            if (refs.Count > 0)
            {
                sb.AppendLine("  The copy still has external references (in-context features or derived parts). It may show '->?' or fail to rebuild " +
                              "when inserted elsewhere; consider locking or breaking them in the library copy:");
                foreach (var r in refs.Take(10)) sb.AppendLine("    " + r);
            }
            Log.Info($"Added {label} to design library: {target}");
            return ToolResult.Ok(sb.ToString());
        }

        /// <summary>Design Library root folders as configured in SolidWorks (semicolon-separated).</summary>
        internal static List<string> Roots(ISldWorks sw)
        {
            var raw = sw?.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDesignLibrary) ?? "";
            return raw.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string PickRoot(List<string> roots, string choice, bool mustExist)
        {
            choice = (choice ?? "").Trim();
            if (choice.Length > 0)
            {
                if (int.TryParse(choice, out var n))
                {
                    if (n < 1 || n > roots.Count) throw new ToolException($"There is no Design Library folder number {n}; there are {roots.Count}.");
                    choice = roots[n - 1];
                }
                if (!Path.IsPathRooted(choice)) throw new ToolException("Root must be a folder number from design_library or an absolute path.");
                if (mustExist && !Directory.Exists(choice)) throw new ToolException($"'{choice}' doesn't exist (or its drive isn't connected).");
                return Path.GetFullPath(choice);
            }
            var existing = roots.FirstOrDefault(Directory.Exists);
            if (existing != null) return existing;
            throw new ToolException(roots.Count == 0
                ? "SolidWorks has no Design Library folders configured. Ask the user which folder to use (e.g. a shared network library), then pass it as root."
                : "None of the configured Design Library folders exist on this computer (a network drive may be disconnected). " +
                  "Ask the user which folder to use - one of the listed ones once its drive is connected, or another folder - then pass it as root.");
        }

        /// <summary>Resolve a folder inside the library root; rejects anything that escapes it.</summary>
        private static string SafeSub(string root, string folder)
        {
            folder = (folder ?? "").Trim().Replace('/', '\\').Trim('\\');
            if (folder.Length == 0) return root;
            if (Path.IsPathRooted(folder)) throw new ToolException("Folder must be relative to the library root, e.g. \"mold hardware components\".");
            var full = Path.GetFullPath(Path.Combine(root, folder));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ToolException("Folder escapes the library root.");
            return full;
        }

        private static (IModelDoc2 doc, string label) ResolveSource(ToolContext ctx, IModelDoc2 model, string source)
        {
            IComponent2 comp = null;
            source = (source ?? "").Trim();
            if (source.Length > 0)
            {
                var id = source.Trim('[', ']');
                if (ctx.Refs.ContainsKey(id))
                {
                    var obj = SelectionTools.Resolve(model, ctx.Refs, id);
                    comp = obj as IComponent2 ?? (obj as IEntity)?.GetComponent() as IComponent2 ?? (obj as IFeature)?.GetSpecificFeature2() as IComponent2;
                    if (comp == null && model.GetType() == (int)swDocumentTypes_e.swDocPART) return (model, model.GetTitle());
                    if (comp == null) throw new ToolException($"{id} isn't on a component.");
                }
                else if (model is IAssemblyDoc asm)
                {
                    comp = asm.GetComponentByName(source) as IComponent2
                           ?? ((object[])asm.GetComponents(false) ?? new object[0]).Cast<IComponent2>()
                               .FirstOrDefault(c => string.Equals(c.Name2, source, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(Path.GetFileNameWithoutExtension(c.GetPathName()), source, StringComparison.OrdinalIgnoreCase));
                    if (comp == null) throw new ToolException($"No component named '{source}' in {model.GetTitle()}.");
                }
                else throw new ToolException($"'{source}' is neither a reference id nor a component of an assembly.");
            }
            else if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var sm = (ISelectionMgr)model.SelectionManager;
                var picked = new List<IComponent2>();
                for (var i = 1; i <= sm.GetSelectedObjectCount2(-1); i++)
                {
                    if (sm.GetSelectedObjectsComponent4(i, -1) is IComponent2 c && !picked.Any(p => p.Name2 == c.Name2)) picked.Add(c);
                }
                if (picked.Count != 1) throw new ToolException("This is an assembly: pass source (a reference id or component name) to say which part to add.");
                comp = picked[0];
            }
            else return (model, model.GetTitle());

            if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                throw new ToolException($"{comp.Name2} is suppressed; unsuppress it first.");
            if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentLightweight || comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
            var doc = comp.GetModelDoc2() as IModelDoc2 ?? throw new ToolException($"Couldn't open the model of {comp.Name2}.");
            return (doc, $"{comp.Name2} ({(string.IsNullOrEmpty(comp.GetPathName()) ? "virtual component" : Path.GetFileName(comp.GetPathName()))})");
        }

        private static List<string> ExternalRefs(IModelDoc2 doc)
        {
            var result = new List<string>();
            try
            {
                if (doc.Extension.ListExternalFileReferencesCount() == 0) return result;

                doc.Extension.ListExternalFileReferences2(out var models, out _, out var feats, out _, out _, out _, out _, out _, out _);
                var m = models as object[] ?? new object[0];
                var f = feats as object[] ?? new object[0];
                for (var i = 0; i < m.Length; i++) result.Add($"{(i < f.Length ? f[i] : "?")} -> {m[i]}");
            }
            catch (Exception ex)
            {
                Log.Warn("Listing external references failed: " + ex.Message);
            }
            return result;
        }

        private static IEnumerable<string> LibraryFiles(string dir, SearchOption option)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.*", option).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly).ToList();
            }
            return files
                .Where(f => LibraryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("~$"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        private static string RelativeTo(string root, string dir) =>
            SamePath(root, dir) ? "(root)" : dir.Substring(root.TrimEnd('\\').Length + 1);

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
