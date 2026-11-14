"""Isolated launcher behavior; no real app, Dock or keyboard is modified."""
import importlib.util
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("dock_launcher", Path(__file__).with_name("dock-launcher.py"))
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class DockLauncherTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.config = self.root / "dock-slots.txt"
        self.config.write_text("1 ChatGPT\n2 Ghostty\n3 Notion\n4 Finder\n")
        self.tiles = []
        for name in ["Changed first", "B", "C", "D", "中文 App ' $() `text`", "F", "G", "H", "I", "J", "K"]:
            app = self.root / (name + ".app")
            app.mkdir()
            self.tiles.append({"tile-type": "file-tile", "tile-data": {
                "file-label": name, "file-data": {"_CFURLString": app.as_uri() + "/"}}})

    def resolve(self, slot):
        return launcher.resolve_slot(slot, self.config, lambda: self.tiles)

    def test_first_four_remain_fixed_even_when_dock_unavailable(self):
        def unavailable():
            self.fail("fixed overrides must not require Dock preferences")
        for slot, name in enumerate(["ChatGPT", "Ghostty", "Notion", "Finder"], 1):
            self.assertEqual(launcher.resolve_slot(slot, self.config, unavailable)["target"], name)

    def test_reorder_seen_on_next_resolution_and_no_fixed_slot_subtraction(self):
        before = self.resolve(5)
        self.tiles[4], self.tiles[6] = self.tiles[6], self.tiles[4]
        self.assertEqual(self.resolve(5)["name"], "G")
        self.assertEqual(self.resolve(7)["target"], before["target"])
        self.assertEqual(self.resolve(10)["name"], "J")
        self.assertEqual(self.resolve(11)["name"], "K")

    def test_file_url_spaces_unicode_and_shell_characters_are_literal(self):
        target = self.resolve(5)
        self.assertEqual(target["target"], str(self.root / "中文 App ' $() `text`.app"))
        with patch.object(launcher.subprocess, "run") as run:
            launcher.launch(target)
        self.assertEqual(run.call_args.args[0], ["/usr/bin/open", "-a", target["target"]])

    def test_missing_and_spacer_slots_do_not_launch_neighbor(self):
        self.tiles[4] = {"tile-type": "spacer-tile"}
        with self.assertRaisesRegex(launcher.LaunchError, "不是应用"):
            self.resolve(5)
        self.tiles = self.tiles[:5]
        with self.assertRaisesRegex(launcher.LaunchError, "没有第 6"):
            self.resolve(6)

    def test_stale_and_nonlocal_urls_fail_without_old_app_fallback(self):
        for url in ["https://example.com/App.app", "file://remotehost/App.app", "file:///missing.app"]:
            with self.subTest(url=url):
                self.tiles[4]["tile-data"]["file-data"]["_CFURLString"] = url
                with self.assertRaises(launcher.LaunchError):
                    self.resolve(5)

    def test_overrides_are_numbered_not_based_on_line_position(self):
        self.config.write_text("# custom list\n\n4 Finder\n1 ChatGPT\n5 Custom App\n")
        self.assertEqual(self.resolve(1)["name"], "ChatGPT")
        self.assertEqual(self.resolve(4)["name"], "Finder")
        self.assertEqual(self.resolve(5)["name"], "Custom App")
        self.assertEqual(self.resolve(6)["name"], "F")

    def test_reads_fresh_export_and_ignores_recent_apps(self):
        data = plistlib.dumps({"persistent-apps": self.tiles, "recent-apps": [{"wrong": True}]})
        with patch.object(launcher.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, data)) as run:
            self.assertEqual(launcher.read_dock(), self.tiles)
            launcher.read_dock()
        self.assertEqual(run.call_count, 2)
        self.assertEqual(run.call_args.args[0], ["/usr/bin/defaults", "export", "com.apple.dock", "-"])

    def test_finder_preserves_hammerspoon_helper(self):
        with patch.object(launcher.Path, "is_dir", return_value=True), patch.object(launcher.subprocess, "run") as run:
            launcher.launch(self.resolve(4))
        self.assertEqual(run.call_args.args[0], ["/usr/bin/open", "-g", "hammerspoon://show-finder"])

    def test_zero_is_ten_and_resolve_has_no_launch_side_effects(self):
        with patch.object(launcher, "resolve_slot", return_value={"slot": 10}) as resolve, patch.object(launcher, "launch") as launch, patch("builtins.print"):
            self.assertEqual(launcher.main(["--resolve", "0"]), 0)
        resolve.assert_called_once_with(10)
        launch.assert_not_called()

    def test_notification_does_not_interpolate_app_name(self):
        message = 'A "quoted" app $(touch NO)'
        with patch.object(launcher.subprocess, "run") as run:
            launcher.notify(message)
        self.assertEqual(run.call_args.args[0], ["/usr/bin/osascript", "-", message])
        self.assertNotIn(message, run.call_args.kwargs["input"])


if __name__ == "__main__":
    unittest.main()
