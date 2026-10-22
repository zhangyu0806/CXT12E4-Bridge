using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cxt1024Bridge
{
    // No desktop, HID, XInput, foreground activation, or injected keyboard/mouse input.
    internal static class WindowsProfileTests
    {
        private static int _checks;
        internal static int Run()
        {
            _checks = 0;
            foreach (string device in new[] { "keyboard", "xbox" })
            {
                List<ProfileControl> controls = WindowsLayerProfiles.Controls(device);
                Check(controls.Count == (device == "keyboard" ? 24 : 26), device + " control inventory");
                var unique = new HashSet<string>(); foreach (ProfileControl control in controls) Check(unique.Add(control.Id), "unique physical control");
                for (int layer = 0; layer < 8; layer++)
                {
                    var defaults = WindowsLayerProfiles.Defaults(device, layer);
                    Check(defaults.Count == controls.Count, "complete default map");
                    foreach (ProfileControl control in controls)
                    {
                        List<ProfileChoice> choices = WindowsLayerProfiles.Choices(device, control.Id, layer);
                        Check(choices.Exists(c => c.Id == defaults[control.Id]), "valid " + device + " L" + layer + " " + control.Id);
                        var actionIds = new HashSet<string>(); bool duplicate = false;
                        foreach (ProfileChoice choice in choices) if (!actionIds.Add(choice.Id)) duplicate = true;
                        Check(!duplicate, "unambiguous choice identifiers");
                    }
                }
            }
            Check(WindowsLayerProfiles.LayerName(0).StartsWith("L8"), "internal zero retains display layer eight");
            string[] primaryActions = { "button.9", "codex.composer", "button.5", "encoder.2.1", "button.2", "button.1", "files.open", "button.4" };
            for (int layer = 0; layer < 8; layer++)
            {
                var defaults = WindowsLayerProfiles.Defaults("xbox", layer);
                Check(defaults["x"] == "mouse.left" && defaults["y"] == "mouse.right", "X/Y mouse ownership is consistent in every layer");
                Check(defaults["a"] == "key.enter" && defaults["b"] == "key.escape", "A/B confirm and cancel are consistent in every layer");
                Check(defaults["rt.l3"] == primaryActions[layer], "former layer-specific X action remains available on RT L3");
                Check(defaults["l3"] == "bridge.help", "L3 still opens settings");
                Check(defaults["rt.a"] == "edit.copy" && defaults["rt.b"] == "edit.paste", "RT copy/paste preserved");
                if (layer != 2) Check(defaults["rt.x"] == "edit.undo" && defaults["rt.y"] == "edit.redo", "RT undo/redo preserved outside terminal");
            }
            Check(WindowsLayerProfiles.Defaults("xbox", 6)["rt.l3"] == "files.open", "Windows file-manager open action");
            Check(WindowsLayerProfiles.Defaults("xbox", 2)["rt.x"] == "button.10", "terminal RT X enters copy mode instead of Ctrl Z");
            Check(WindowsLayerProfiles.Defaults("xbox", 2)["rt.y"] == "button.9", "terminal RT Y stashes input instead of Ctrl Y");
            Check(!WindowsLayerProfiles.ValidAction("xbox", "x", 2, "edit.undo"), "terminal cannot import suspend-producing undo binding");
            Check(!WindowsLayerProfiles.ValidAction("keyboard", "button.1", 2, "edit.redo"), "terminal redo unavailable for keyboard too");
            Check(!WindowsLayerProfiles.ValidAction("keyboard", "button.1", 1, "mouse.left"), "keyboard taps cannot hold mouse buttons");
            Check(!WindowsLayerProfiles.ValidAction("xbox", "a", 1, "stick.mouse"), "button cannot receive axis action");
            Check(!WindowsLayerProfiles.ValidAction("xbox", "leftstick", 1, "button.1"), "axis cannot receive discrete action");
            Check(!WindowsLayerProfiles.ValidAction("xbox", "rightstick", 1, "stick.blender"), "Blender stick is layer scoped");
            Check(!WindowsLayerProfiles.ValidAction("xbox", "a", 6, "finder.open"), "Mac Finder action rejected on Windows");
            Bad(d => d.Version = 2, "unknown version"); Bad(d => d.Platform = "macos", "wrong platform");
            Bad(d => d.DeadZone = double.NaN, "NaN deadzone"); Bad(d => d.CursorSpeed = double.PositiveInfinity, "infinite speed");
            Bad(d => d.ScrollSpeed = 49, "below minimum scroll speed"); Bad(d => d.PrecisionScale = .71, "precision too high");
            Bad(d => d.Bindings = null, "null maps"); Bad(d => d.PausedProcesses = null, "null pause list");
            Bad(d => d.PausedProcesses = new List<string> { "C:\\Games\\game.exe" }, "process path rejected");
            Bad(d => d.PausedProcesses = new List<string> { "*" }, "wildcard process rejected");
            Bad(d => d.PausedProcesses = new List<string> { "game\n.exe" }, "process newline rejected");
            Bad(d => d.PausedProcesses = new List<string> { " " }, "empty process rejected");
            Bad(d => d.Bindings["unknown"] = new Dictionary<string, Dictionary<string, string>>(), "unknown device rejected");
            Bad(d => d.Bindings["xbox"] = null, "null device maps rejected");
            Bad(d => Map(d, "xbox", "01", "a", "none"), "noncanonical layer rejected");
            Bad(d => Map(d, "xbox", "8", "a", "none"), "display layer not accepted as internal layer");
            Bad(d => Map(d, "xbox", "0", "menu", "none"), "fixed Menu cannot be overridden");
            Bad(d => Map(d, "keyboard", "0", "hold.1", "none"), "global hold cannot be overridden");
            Bad(d => Map(d, "xbox", "0", "a", "shell:calc.exe"), "arbitrary command rejected");
            Bad(d => Map(d, "xbox", "0", "a", null), "null action rejected");
            Bad(d => Map(d, "xbox", "0", "a", "tmux.pane.left"), "tmux actions layer scoped");
            Reject(delegate { WindowsProfileStore.Parse("{}"); }, "missing format marker");
            Reject(delegate { WindowsProfileStore.Parse("null"); }, "null JSON");
            Reject(delegate { WindowsProfileStore.Parse("{\"Version\":1,\"Platform\":\"windows\",\"Bindings\":{},\"Executable\":\"calc.exe\"}"); }, "unknown JSON field");
            var original = new WindowsProfileDocument();
            WindowsProfileStore.Put(original, "xbox", "a", 1, "button.2");
            var clone = original.Clone(); clone.Bindings["xbox"]["1"]["a"] = "none"; clone.PausedProcesses.Clear();
            Check(original.Bindings["xbox"]["1"]["a"] == "button.2" && original.PausedProcesses.Count > 0, "clone deeply isolates maps and pause list");
            var decoded = WindowsProfileStore.Parse(WindowsProfileStore.Serialize(original));
            Check(WindowsProfileStore.Resolve(decoded, "xbox", "a", 1) == "button.2", "JSON roundtrip preserves override");
            string directory = Path.Combine(Path.GetTempPath(), "cxt-windows-profiles-" + Guid.NewGuid().ToString("N"));
            try { TestFiles(directory); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            Console.WriteLine("PASS " + _checks + " Windows profile checks"); return _checks;
        }
        private static void TestFiles(string directory)
        {
            string path = Path.Combine(directory, "profiles.windows.json");
            var store = new WindowsProfileStore(path); int changes = 0; store.Changed += delegate { changes++; };
            Check(store.Document.XboxEnabled && store.LoadError == null, "new configuration defaults");
            Check(!File.Exists(path), "reading defaults does not write config");
            store.Set("xbox", "x", 3, "button.1");
            Check(changes == 1 && store.Action("xbox", "x", 3) == "button.1", "save publishes one change");
            Check(store.Action("keyboard", "button.1", 3) == "button.1", "keyboard map isolated");
            Check(store.Action("xbox", "x", 1) == "mouse.left" && store.Action("xbox", "rt.l3", 1) == "codex.composer", "other layer remains default");
            string saved = File.ReadAllText(path);
            var snapshot = store.Document; snapshot.XboxEnabled = false; snapshot.Bindings["xbox"]["3"]["x"] = "none";
            Check(store.Document.XboxEnabled && store.Action("xbox", "x", 3) == "button.1", "document getter never exposes mutable store");
            var reloaded = new WindowsProfileStore(path);
            Check(reloaded.Action("xbox", "x", 3) == "button.1", "persisted setting loads");
            reloaded.Reset("xbox", 3);
            Check(reloaded.Action("xbox", "x", 3) == "mouse.left" && reloaded.Action("xbox", "rt.l3", 3) == "encoder.2.1", "selected layer reset");
            Check(File.ReadAllText(Path.Combine(directory, "profiles.windows.previous.json")) == saved, "atomic replacement preserves previous version");
            string before = File.ReadAllText(path); int beforeChanges = changes;
            Reject(delegate { store.Set("xbox", "a", 1, "shell.exec"); }, "invalid update rejected");
            Check(File.ReadAllText(path) == before && changes == beforeChanges, "invalid update leaves disk and notification unchanged");
            string badFile = Path.Combine(directory, "bad.json"); File.WriteAllText(badFile, "{\"Version\":999}");
            Reject(delegate { store.Import(badFile); }, "invalid import rejected");
            Check(File.ReadAllText(path) == before, "invalid import leaves original file");
            string export = Path.Combine(directory, "export.json"); reloaded.Export(export);
            var imported = new WindowsProfileStore(Path.Combine(directory, "imported", "profiles.windows.json")); imported.Import(export);
            Check(imported.Action("xbox", "x", 3) == "mouse.left" && imported.Action("xbox", "rt.l3", 3) == "encoder.2.1" && imported.LoadError == null, "export/import roundtrip");
            File.WriteAllText(path, "broken json", new UTF8Encoding(false));
            var damaged = new WindowsProfileStore(path);
            Check(damaged.LoadError != null && !damaged.Document.XboxEnabled, "damaged file pauses controller");
            Check(damaged.Action("keyboard", "button.1", 1) == "button.1", "damaged file retains default keyboard");
            Check(File.ReadAllText(path) == "broken json", "damaged original preserved");
            damaged.Set("xbox", "a", 1, "none");
            Check(File.ReadAllText(Path.Combine(directory, "profiles.windows.previous.json")) == "broken json", "explicit recovery retains corrupt bytes as backup");
            Check(damaged.LoadError == null && !damaged.Document.XboxEnabled, "recovery does not silently reenable controller");
            string occupied = Path.Combine(directory, "occupied"); Directory.CreateDirectory(occupied);
            var failing = new WindowsProfileStore(occupied); bool notified = false; failing.Changed += delegate { notified = true; };
            Reject(delegate { failing.Set("xbox", "a", 1, "none"); }, "failed atomic write reported");
            Check(!notified && failing.Action("xbox", "a", 1) == "key.enter", "failed write preserves in-memory state");
            Check(Directory.GetFiles(directory, ".profiles-*.tmp").Length == 0, "failed write removes temp file");
        }
        private static void Bad(Action<WindowsProfileDocument> change, string name)
        { Reject(delegate { var d = new WindowsProfileDocument(); change(d); d.Validate(); }, name); }
        private static void Map(WindowsProfileDocument d, string device, string layer, string control, string action)
        { d.Bindings[device] = new Dictionary<string, Dictionary<string, string>> { { layer, new Dictionary<string, string> { { control, action } } } }; }
        private static void Reject(Action action, string name)
        { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, name); }
        private static void Check(bool value, string message)
        { _checks++; if (!value) throw new InvalidOperationException("Windows profiles: " + message); }
    }
}
