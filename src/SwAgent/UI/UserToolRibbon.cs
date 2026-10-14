using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.UI
{
    /// <summary>
    /// The "Unify3D Tools" ribbon tab: one button per user tool (with the icon the agent designed for it) plus
    /// "New Tool". Rebuilt whenever tools are saved, edited, deleted or get a new icon.
    ///
    /// xCAD command groups are fixed at start-up, so this one is made with the plain SolidWorks command API.
    /// Buttons call back into the add-in object by name: OnUserToolClick("3") / OnUserToolEnable("3").
    /// </summary>
    internal sealed class UserToolRibbon : IDisposable
    {
        public const int GroupId = 7700;
        public const string TabName = "Unify3D Tools";
        private const string NewToolArg = "new";
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };
        private static readonly int[] DocTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };

        private readonly ICommandManager _cmdMgr;
        private readonly Timer _debounce = new Timer { Interval = 400 };
        private List<string> _names = new List<string>();
        private bool _created;

        public UserToolRibbon(ICommandManager cmdMgr)
        {
            _cmdMgr = cmdMgr;
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Rebuild();
            };
        }

        /// <summary>Tool name for a button index (null for "New Tool").</summary>
        public string ToolAt(string arg)
        {
            if (arg == NewToolArg) return null;
            return int.TryParse(arg, out var i) && i >= 0 && i < _names.Count ? _names[i] : null;
        }

        public static bool IsNewTool(string arg) => arg == NewToolArg;

        /// <summary>Rebuild soon (several changes in a row become one rebuild).</summary>
        public void Invalidate()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        public void Rebuild()
        {
            try
            {
                var tools = UserTools.ListTools();
                var names = tools.Select(t => (string)t["name"]).ToList();
                var icons = new List<byte[]> { Icons.NewTool };
                icons.AddRange(names.Select(n => LoadIcon(n) ?? Icons.ToolDefault));

                Remove();
                var dir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "ribbon")).FullName;
                foreach (var old in Directory.GetFiles(dir, "*.png")) TryDelete(old);
                var stamp = DateTime.Now.Ticks.ToString("x");
                var strips = Sizes.Select(s => SaveStrip(icons, s, Path.Combine(dir, $"tools-{stamp}-{s}.png"))).ToArray();
                var main = Sizes.Select(s => SaveStrip(new List<byte[]> { Icons.ToolDefault }, s, Path.Combine(dir, $"group-{stamp}-{s}.png"))).ToArray();

                var errors = 0;
                var group = _cmdMgr.CreateCommandGroup2(GroupId, TabName, "Your saved Unify3D tools", "Your saved Unify3D tools", -1, true, ref errors);
                group.IconList = strips;
                group.MainIconList = main;
                var options = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
                var newIndex = group.AddCommandItem2("New Tool", -1, "Create a new tool with the agent", "New Tool", 0,
                    $"OnUserToolClick({NewToolArg})", $"OnUserToolEnable({NewToolArg})", GroupId + 1, options);
                var indexes = new List<int>();
                for (var i = 0; i < tools.Count; i++)
                {
                    var title = (string)tools[i]["title"];
                    var description = (string)tools[i]["description"];
                    indexes.Add(group.AddCommandItem2(title, -1, description, title, i + 1, $"OnUserToolClick({i})", $"OnUserToolEnable({i})", GroupId + 10 + i, options));
                }
                group.HasToolbar = true;
                group.HasMenu = true;
                if (!group.Activate()) Log.Warn("Unify3D Tools command group did not activate");
                _names = names;
                _created = true;

                var ids = new List<int> { group.get_CommandID(newIndex) };
                ids.AddRange(indexes.Select(ix => group.get_CommandID(ix)));
                var styles = ids.Select(_ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
                foreach (var type in DocTypes)
                {
                    var tab = _cmdMgr.GetCommandTab(type, TabName) ?? _cmdMgr.AddCommandTab(type, TabName);
                    if (tab == null) continue;
                    if (tab.CommandTabBoxes() is object[] boxes) foreach (CommandTabBox b in boxes) tab.RemoveCommandTabBox(b);
                    tab.AddCommandTabBox().AddCommands(ids.ToArray(), styles);
                }
                Log.Info($"Unify3D Tools ribbon: {tools.Count} tool(s)");
            }
            catch (Exception ex)
            {
                Log.Error("Building the Unify3D Tools ribbon failed", ex);
            }
        }

        /// <summary>Take the group and its tabs out of SolidWorks (before a rebuild and on shutdown).</summary>
        public void Remove()
        {
            try
            {
                foreach (var type in DocTypes)
                {
                    var tab = _cmdMgr.GetCommandTab(type, TabName);
                    if (tab != null) _cmdMgr.RemoveCommandTab(tab);
                }
                if (_created) _cmdMgr.RemoveCommandGroup2(GroupId, true);
                _created = false;
            }
            catch (Exception ex)
            {
                Log.Warn("Removing the Unify3D Tools ribbon failed: " + ex.Message);
            }
        }

        private static byte[] LoadIcon(string name)
        {
            try
            {
                var png = Path.Combine(UserTools.Root, name, "icon.png");
                return File.Exists(png) ? File.ReadAllBytes(png) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>One horizontal strip of square icons at the given size (the format SolidWorks' IconList expects).</summary>
        private static string SaveStrip(List<byte[]> icons, int size, string path)
        {
            using (var strip = new Bitmap(size * icons.Count, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(strip))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (var i = 0; i < icons.Count; i++)
                    {
                        try
                        {
                            using (var ms = new MemoryStream(icons[i]))
                            using (var img = Image.FromStream(ms))
                                g.DrawImage(img, new Rectangle(i * size, 0, size, size));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Bad tool icon #{i}: {ex.Message}");
                        }
                    }
                }
                strip.Save(path, ImageFormat.Png);
            }
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // In use by SolidWorks until the group is rebuilt; cleaned up next time.
            }
        }

        public void Dispose()
        {
            _debounce.Dispose();
            Remove();
        }
    }
}
