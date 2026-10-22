import importlib.util
from pathlib import Path
import shlex
import unittest


SPEC = importlib.util.spec_from_file_location("resurrect_restore", Path(__file__).with_name("resurrect-restore.py"))
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class RestoreStrategyTests(unittest.TestCase):
    def test_quotes_paths_and_defers_target_pane_to_target_shell(self):
        command = MODULE.restore_command("/home/a $ b/python", "/home/a ' b/lifecycle.py", "/bin/tmux",
                                         "claude", {"TMUX": "/tmp/a b,socket,123,0", "TMUX_PANE": "%999"})
        words = shlex.split(command)
        self.assertEqual(words[:3], ["exec", "/home/a $ b/python", "/home/a ' b/lifecycle.py"])
        self.assertEqual(words[words.index("--socket") + 1], "/tmp/a b,socket")
        self.assertTrue(command.endswith('--pane "$TMUX_PANE"'))
        self.assertNotIn("%999", command)

    def test_rejects_arguments_or_shell_syntax(self):
        for command in ("claude --resume", "claude; touch /tmp/no", "my-claude", "/bin/claude"):
            with self.assertRaises(ValueError):
                MODULE.restore_command("python", "script", "tmux", command, {"TMUX": "/tmp/socket,1,0"})

    def test_requires_explicit_restore_server(self):
        for value in ("", "socket,1,0", "/socket,abc,0", "/socket"):
            with self.assertRaises(ValueError):
                MODULE.restore_command("python", "script", "tmux", "grok", {"TMUX": value})


if __name__ == "__main__":
    unittest.main()
