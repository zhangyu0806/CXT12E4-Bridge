"""Offline integration checks; installers run only with a temporary HOME.

WSL interop is stubbed. Bun and tmux checks use real executables when present;
tmux always uses a private socket and never loads the user's configuration.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[1]
TEMPLATES = ROOT / 'integrations/terminal-status'


class IntegrationPortabilityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cxt-int-')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        # A successful command must treat all of these as filename characters.
        self.home = self.base / "home with 'quote' $(touch INJECTED) `touch BACKTICK` $cash"
        self.home.mkdir()
        self.stub_bin = self.base / 'stubs'
        self.stub_bin.mkdir()
        self.log = self.base / 'calls.jsonl'
        self.env = {**os.environ, 'HOME': str(self.home),
                    'PATH': str(self.stub_bin) + os.pathsep + os.environ['PATH'],
                    'XDG_RUNTIME_DIR': str(self.base / 'runtime'),
                    'XDG_CONFIG_HOME': str(self.home / '.config'),
                    'XDG_CACHE_HOME': str(self.home / '.cache'),
                    'TMUX_PANE': '%7', 'CXT_TEST_LOG': str(self.log),
                    'PYTHONDONTWRITEBYTECODE': '1'}
        self.env.pop('TMUX', None)
        self.env.pop('BASH_ENV', None)
        self.env.pop('ENV', None)

    def run_command(self, args, *, check=True, **kwargs):
        result = subprocess.run(args, cwd=self.base, env=self.env,
                                text=True, capture_output=True, timeout=20, **kwargs)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse((self.base / 'INJECTED').exists())
        self.assertFalse((self.base / 'BACKTICK').exists())
        return result

    def executable(self, path, body):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('#!' + sys.executable + '\n' + body)
        path.chmod(0o755)

    def install_recording_reporter(self):
        self.executable(self.home / '.local/bin/cxt12e4-agent-status',
                        'import json, os, sys\n'
                        'with open(os.environ["CXT_TEST_LOG"], "a") as output:\n'
                        '    output.write(json.dumps(sys.argv[1:]) + "\\n")\n')

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def seed_claude(self):
        path = self.home / '.claude/settings.json'
        path.parent.mkdir(parents=True)
        data = {'theme': 'keep-theme', 'keep': 42, 'hooks': {
            'SessionStart': [{'matcher': 'existing', 'timeout': 7, 'hooks': [
                {'type': 'command', 'command': 'echo keep-user-hook'},
                {'type': 'command', 'command': '/old/home/.local/bin/cxt12e4-agent-status idle'}]}],
            'PreToolUse': [{'hooks': [
                {'type': 'command', 'command': '/old/home/.local/bin/cxt12e4-agent-status thinking-if-waiting'}]}]}}
        path.write_text(json.dumps(data))
        return path

    def verify_claude_upgrade(self, path, *, macos=False):
        settings = json.loads(path.read_text())
        self.assertEqual(settings['keep'], 42)
        self.assertEqual(settings['theme'], 'keep-theme')
        self.assertNotIn('/old/home', path.read_text())
        group = settings['hooks']['SessionStart'][0]
        self.assertEqual(group, {'matcher': 'existing', 'timeout': 7, 'hooks': [
            {'type': 'command', 'command': 'echo keep-user-hook'}]})
        counts = {}
        for event, groups in settings['hooks'].items():
            count = sum('cxt12e4-' in hook.get('command', '')
                        for group in groups for hook in group.get('hooks', []))
            if count:counts[event] = count
        expected = {'SessionStart': 1, 'UserPromptSubmit': 1, 'PermissionRequest': 1,
                    'Notification': 2, 'Stop': 1, 'StopFailure': 1, 'SessionEnd': 1}
        if macos:expected.update(PreToolUse=1, PostToolUse=1)
        self.assertEqual(counts, expected)
        return settings

    def test_shell_hooks_and_idle_helper_quote_home(self):
        self.install_recording_reporter()
        idle = self.home / '.local/bin/cxt12e4-claude-idle'
        shutil.copyfile(TEMPLATES / idle.name, idle)
        idle.chmod(0o755)
        expected = []
        for name in ('claude-settings.json', 'grok-hooks.json'):
            template = json.loads((TEMPLATES / name).read_text())
            for groups in template['hooks'].values():
                for group in groups:
                    for hook in group['hooks']:
                        command = hook['command']
                        self.run_command(['/bin/sh', '-c', command], input=json.dumps({
                            'hook_event_name': 'Notification', 'notification_type': 'idle_prompt'}))
                        expected.append(['idle-if-active' if 'cxt12e4-claude-idle' in command else command.rsplit(' ', 1)[1]])
        self.assertEqual(self.calls(), expected)
        before = self.calls()
        self.run_command([str(idle)], input='{"hookEventName":"Notification","notificationType":"idle_prompt"}')
        self.assertEqual(self.calls(), before)

    def test_macos_installer_upgrades_hooks_and_preserves_unrelated_settings(self):
        karabiner = self.home / '.config/karabiner/karabiner.json'
        karabiner.parent.mkdir(parents=True)
        karabiner.write_text(json.dumps({'profiles': [{'selected': True, 'name': 'test',
            'complex_modifications': {'rules': [{'description': 'keep-user-rule'}]}}]}))
        (self.home / '.config/opencode').mkdir()
        (self.home / '.grok').mkdir()
        claude = self.seed_claude()
        command = [sys.executable, str(ROOT / 'install/macos/install_integrations.py')]
        self.run_command(command)
        settings = self.verify_claude_upgrade(claude, macos=True)
        before = claude.read_bytes()
        self.run_command(command)
        self.assertEqual(claude.read_bytes(), before)
        self.assertIn({'description': 'keep-user-rule'}, json.loads(karabiner.read_text())['profiles'][0]['complex_modifications']['rules'])
        plugin = self.home / '.config/opencode/plugins/cxt12e4-terminal-lights.js'
        self.assertEqual(plugin.read_bytes(), (TEMPLATES / plugin.name).read_bytes())
        # Exercise the installed shell wrappers with a test helper, not the
        # Bridge or its input socket. The real helper was never started.
        helper = self.home / 'Library/Application Support/CXT12E4Bridge/bridge_helper.py'
        self.executable(helper, 'import json, os, sys\n'
                        'with open(os.environ["CXT_TEST_LOG"], "a") as output:\n'
                        '    output.write(json.dumps(sys.argv[1:]) + "\\n")\n')
        for event in ('SessionStart', 'PreToolUse', 'PostToolUse'):
            hook = settings['hooks'][event][-1]['hooks'][0]['command']
            self.run_command(['/bin/sh', '-c', hook])
        self.run_command([str(self.home / '.local/bin/cxt12e4-claude-idle')])
        self.assertEqual(self.calls(), [['agent', 'idle'], ['agent', 'thinking-if-waiting'],
                                       ['agent', 'thinking-if-waiting'], ['agent', 'idle-if-active']])
        grok = json.loads((self.home / '.grok/hooks/cxt12e4-macos.json').read_text())
        self.run_command(['/bin/sh', '-c', grok['hooks']['StopCancelled'][0]['hooks'][0]['command']])
        self.assertEqual(self.calls()[-1], ['agent', 'idle'])

    def stub_wsl(self):
        self.env['CXT_TEST_WINDOWS_APPDATA'] = r"D:\Users\Example User's $cash\AppData\Local"
        self.env['CXT_TEST_WSL_APPDATA'] = str(self.base / "Windows mount 'quote' $(touch INJECTED) `touch BACKTICK` $cash")
        self.executable(self.stub_bin / 'cmd.exe',
                        'import os, sys\n'
                        'assert sys.argv[1:] == ["/d", "/c", "echo", "%LOCALAPPDATA%"]\n'
                        'print(os.environ["CXT_TEST_WINDOWS_APPDATA"] + "\\r")\n')
        self.executable(self.stub_bin / 'wslpath',
                        'import os, sys\n'
                        'assert sys.argv[1:] == ["-u", os.environ["CXT_TEST_WINDOWS_APPDATA"]]\n'
                        'print(os.environ["CXT_TEST_WSL_APPDATA"])\n')
        self.executable(self.stub_bin / 'tmux',
                        'import sys\n'
                        'assert sys.argv[1:5] == ["display-message", "-p", "-t", "%7"]\n'
                        'print("Test session\\t$1\\t1700000000\\t12345")\n')
        real_date = shutil.which('date')
        self.executable(self.stub_bin / 'date',
                        'import subprocess, sys\n'
                        'if sys.argv[1:] == ["+%s%N"]: print("1700000000000000000")\n'
                        'elif sys.argv[1:] == ["+%s"]: print("1700000000")\n'
                        'else: raise SystemExit(subprocess.call(' + repr([real_date]) + ' + sys.argv[1:]))\n')
        if not shutil.which('flock'):
            self.skipTest('flock required for the real reporter check')

    def test_wsl_installer_renders_windows_directory_and_upgrades_hooks(self):
        self.stub_wsl()
        claude = self.seed_claude()
        command = ['bash', str(ROOT / 'install/wsl-home/Install-Cxt12E4Integrations.sh')]
        self.run_command(command)
        settings = self.verify_claude_upgrade(claude)
        before = claude.read_bytes()
        self.run_command(command)
        self.assertEqual(claude.read_bytes(), before)
        reporter = self.home / '.local/bin/cxt12e4-agent-status'
        self.assertNotIn('__CXT12E4_STATUS_ROOT_SHELL__', reporter.read_text())
        status = Path(self.env['CXT_TEST_WSL_APPDATA']) / 'Cxt1024Bridge/terminal-status/pane-7.state'
        self.run_command(['/bin/sh', '-c', settings['hooks']['SessionStart'][-1]['hooks'][0]['command']])
        self.assertEqual(status.read_text().split('\t')[2], 'idle')
        self.run_command([str(reporter), 'waiting'])
        grok = json.loads((self.home / '.grok/hooks/cxt12e4-terminal-lights.json').read_text())
        self.run_command(['/bin/sh', '-c', grok['hooks']['PreToolUse'][0]['hooks'][0]['command']])
        fields = status.read_text().strip().split('\t')
        self.assertEqual(fields, ['Test session', '%7', 'thinking', '1700000000', '$1', '1700000000', '12345', '1700000000000000000'])
        self.run_command([str(self.home / '.local/bin/cxt12e4-claude-idle')], input=json.dumps({
            'hook_event_name': 'Notification', 'notification_type': 'idle_prompt'}))
        self.assertEqual(status.read_text().split('\t')[2], 'idle')
        self.run_command([str(reporter), 'off'])
        self.assertFalse(status.exists())

    def test_wsl_rejects_bad_converted_directory_before_writing(self):
        self.stub_wsl()
        command = ['bash', str(ROOT / 'install/wsl-home/Install-Cxt12E4Integrations.sh')]
        for bad in ('', 'relative/path', '/one\n/two', '/one\r/two'):
            with self.subTest(path=repr(bad)):
                self.env['CXT_TEST_WSL_APPDATA'] = bad
                result = self.run_command(command, check=False)
                self.assertEqual(result.returncode, 3, result.stderr)
                self.assertFalse((self.home / '.local').exists())
                self.assertFalse((self.home / '.claude').exists())

    def test_unrendered_reporter_refuses_to_write(self):
        result = self.run_command(['bash', str(TEMPLATES / 'cxt12e4-agent-status')], check=False)
        self.assertEqual(result.returncode, 2)
        self.assertFalse((self.base / '__CXT12E4_STATUS_ROOT_SHELL__').exists())

    @unittest.skipUnless(shutil.which('bun'), 'Bun required to verify actual shell interpolation')
    def test_opencode_plugin_executes_quoted_reporter_with_real_bun(self):
        self.install_recording_reporter()
        self.env['CXT_TEST_PLUGIN'] = str(TEMPLATES / 'cxt12e4-terminal-lights.js')
        runner = self.base / 'plugin-test.mjs'
        runner.write_text('''import { $ } from "bun";
import { pathToFileURL } from "node:url";
const { Cxt12E4TerminalLightsPlugin } = await import(pathToFileURL(process.env.CXT_TEST_PLUGIN).href);
const plugin = await Cxt12E4TerminalLightsPlugin({ $ });
for (const [type, properties] of [
  ["session.status", { status: { type: "busy" } }],
  ["permission.asked", {}], ["permission.replied", {}],
  ["session.idle", {}], ["session.deleted", {}]
]) await plugin.event({ event: { type, properties } });
''')
        self.run_command([shutil.which('bun'), str(runner)])
        self.assertEqual(self.calls(), [[state] for state in ('idle', 'thinking', 'waiting', 'thinking', 'completed', 'idle')])

    @unittest.skipUnless(shutil.which('tmux'), 'tmux required for real configuration parsing')
    def test_tmux_editor_names_work_with_home_on_path(self):
        tmux = shutil.which('tmux')
        socket = self.base / 'tmux.sock'
        config = self.base / 'tmux.conf'
        # Parse the actual changed declarations without loading the sample's
        # optional third-party configuration or any installed user hooks.
        lines = (ROOT / 'integrations/tmux/local.conf').read_text().splitlines()
        config.write_text('\n'.join(line for line in lines
                                    if line.startswith(('set-environment -g EDITOR ', 'set-environment -g VISUAL '))) + '\n')
        self.env['PATH'] = str(self.home / '.local/bin') + os.pathsep + self.env['PATH']
        self.env['SHELL'] = '/bin/sh'
        self.executable(self.home / '.local/bin/cxt-prompt-cut-editor',
                        'import json, os, sys\n'
                        'with open(os.environ["CXT_TEST_LOG"], "a") as output:\n'
                        '    output.write(json.dumps(sys.argv[1:]) + "\\n")\n')
        command = [tmux, '-S', str(socket), '-f', str(config)]
        self.addCleanup(lambda: subprocess.run(command + ['kill-server'], env=self.env, capture_output=True))
        self.run_command(command + ['new-session', '-d', '-s', 'portability', '/bin/sleep', '60'])
        for variable in ('EDITOR', 'VISUAL'):
            result = self.run_command(command + ['show-environment', '-g', variable])
            self.assertEqual(result.stdout.strip(), variable + '=cxt-prompt-cut-editor')
        runner = self.base / 'editor-consumer.py'
        runner.write_text('import os, subprocess\n'
                          'for variable in ("EDITOR", "VISUAL"):\n'
                          '    subprocess.run(os.environ[variable].split(" ") + ["draft with spaces.txt"], check=True)\n')
        self.run_command(command + ['new-window', '-d', '-t', 'portability', sys.executable, str(runner)])
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if self.log.exists() and len(self.log.read_text().splitlines()) == 2:
                break
            time.sleep(.025)
        self.assertTrue(self.log.exists(), 'The new pane did not run its editor')
        self.assertEqual(self.calls(), [['draft with spaces.txt'], ['draft with spaces.txt']])


if __name__ == '__main__':
    unittest.main(verbosity=2)
