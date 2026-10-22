import importlib.util
import fcntl
import json
import os
from pathlib import Path
import pty
import shlex
import struct
import subprocess
import tempfile
import termios
import time
import unittest

spec = importlib.util.spec_from_file_location('setup_tmux', Path(__file__).with_name('setup_tmux.py'))
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class SetupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.deps = setup.dependencies(Path.home())

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cxt-mac-setup-')
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)/'home with $ space'
        self.home.mkdir()
        self.source = Path(self.temp.name)/'source/tmux'
        (self.source/'profiles').mkdir(parents=True)
        (self.source/'tmux.conf').write_text('source-file ' + setup.quote(self.home/'.config/tmux/local.conf') + '\n')
        (self.source/'shared.conf').write_text('# test shared layer\n')
        (self.source/'profiles/portable.conf').write_text('# test profile\n')
        self.local = self.home/'.config/tmux/local.conf'
        self.local.parent.mkdir(parents=True)
        self.local.write_text('set -g @keep-user-value "keep me"\n')

    def install(self):
        files, links = setup.plan(self.home, self.source, self.deps, '/usr/bin:/bin')
        return setup.apply_plan(self.home, files, links)

    def isolated_tmux(self):
        self.install()
        socket = str(Path(self.temp.name)/'socket')
        command = [self.deps['tmux'], '-S', socket, '-f', str(self.home/'.tmux.conf')]
        environment = {**os.environ, 'HOME': str(self.home), 'TERM': 'xterm-256color'}
        environment.pop('TMUX', None)
        environment.pop('TMUX_PANE', None)
        self.addCleanup(lambda: subprocess.run(command + ['kill-server'], env=environment, capture_output=True))

        def tm(*args):
            return subprocess.check_output(command + list(args), env=environment, text=True, stderr=subprocess.PIPE).strip()
        return command, environment, tm

    def bare_tmux_client(self, command, environment):
        master, slave = pty.openpty()
        os.set_blocking(master, False)
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))

        def controlling_terminal():
            os.setsid()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)

        try:
            client = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave,
                                      env=environment, preexec_fn=controlling_terminal)
        finally:
            os.close(slave)

        def cleanup():
            if client.poll() is None:
                client.kill()
            client.wait(timeout=5)
            os.close(master)
        self.addCleanup(cleanup)
        output = bytearray()

        def drain():
            try:
                while True:
                    block = os.read(master, 65536)
                    if not block:
                        break
                    output.extend(block)
            except (BlockingIOError, OSError):
                pass
            return output.decode(errors='replace')
        return client, drain

    def wait_for_client_exit(self, client, drain):
        for _ in range(100):
            drain()
            if client.poll() is not None:
                return drain()
            time.sleep(.05)
        self.fail('Bare tmux unexpectedly remained attached or created a session: ' + drain())

    def test_bare_tmux_attaches_existing_office_sessions_without_creating(self):
        command, environment, tm = self.isolated_tmux()
        for name in ('01-Explore', '02-CC', '03-Search', '04-Grok', '05-AGY', '06-Dimagent'):
            tm('new-session', '-d', '-s', name, '/bin/sleep', '300')
        before = tm('list-panes', '-a', '-F', '#{session_id} #{session_name} #{pane_id} #{pane_pid}')
        self.assertEqual(tm('show-options', '-sqv', 'default-client-command'), 'attach-session')
        # Starting the same unqualified command twice must reuse the inventory.
        for _ in range(2):
            client, drain = self.bare_tmux_client(command, environment)
            for _ in range(100):
                drain()
                attached = tm('list-clients', '-F', '#{client_name} #{session_name}')
                if attached or client.poll() is not None:
                    break
                time.sleep(.05)
            self.assertIsNone(client.poll(), drain())
            self.assertEqual(len(attached.splitlines()), 1)
            self.assertIn(attached.split()[1], before)
            self.assertEqual(tm('list-panes', '-a', '-F', '#{session_id} #{session_name} #{pane_id} #{pane_pid}'), before)
            tm('detach-client', '-t', attached.split()[0])
            self.wait_for_client_exit(client, drain)
            self.assertEqual(client.returncode, 0)

    def test_bare_tmux_cold_start_does_not_create_session(self):
        command, environment, tm = self.isolated_tmux()
        client, drain = self.bare_tmux_client(command, environment)
        output = self.wait_for_client_exit(client, drain)
        self.assertNotEqual(client.returncode, 0, output)
        self.assertIn('no sessions', output)
        result = subprocess.run(command + ['list-sessions'], env=environment, capture_output=True, text=True)
        self.assertEqual(result.stdout, '')

    def test_bare_tmux_empty_running_server_does_not_create_session(self):
        command, environment, tm = self.isolated_tmux()
        tm('new-session', '-d', '-s', 'temporary', '/bin/sleep', '300')
        tm('set-option', '-s', 'exit-empty', 'off')
        server_pid = tm('display-message', '-p', '#{pid}')
        tm('kill-session', '-t', '=temporary')
        client, drain = self.bare_tmux_client(command, environment)
        output = self.wait_for_client_exit(client, drain)
        self.assertNotEqual(client.returncode, 0, output)
        self.assertIn('no sessions', output)
        self.assertEqual(tm('list-sessions'), '')
        self.assertEqual(tm('display-message', '-p', '#{pid}'), server_pid)

    def test_explicit_new_session_still_works(self):
        command, environment, tm = self.isolated_tmux()
        tm('new-session', '-d', '-s', 'deliberate', '/bin/sleep', '300')
        self.assertEqual(tm('list-sessions', '-F', '#{session_name}'), 'deliberate')
        self.assertEqual(tm('show-options', '-sqv', 'default-client-command'), 'attach-session')

    def test_rebuild_preserves_custom_config_and_is_idempotent(self):
        changed, backup = self.install()
        self.assertTrue(changed)
        self.assertEqual((backup/'.config/tmux/local.conf').read_text(), 'set -g @keep-user-value "keep me"\n')
        self.assertIn('set -g @keep-user-value "keep me"', self.local.read_text())
        self.assertEqual(self.local.read_text().count(setup.BEGIN), 1)
        self.assertEqual((self.home/'.tmux.conf').resolve(), (self.source/'tmux.conf').resolve())
        self.assertEqual(self.install()[0], [])
        self.assertTrue((self.home/'.config/tmux/macos-bin/python3').stat().st_mode & 0o111)

    def test_refuses_unrelated_entry_before_writing(self):
        entry = self.home/'.tmux.conf'
        entry.write_text('# local.conf is not sourced here\nset -g prefix C-a\n')
        with self.assertRaisesRegex(RuntimeError, 'Existing tmux entry'):
            self.install()
        self.assertEqual(entry.read_text(), '# local.conf is not sourced here\nset -g prefix C-a\n')
        self.assertFalse((self.home/'.config/tmux/macos').exists())

    def test_refuses_different_winsettings_release(self):
        (self.home/'.config/tmux/winsettings').symlink_to(self.home)
        with self.assertRaisesRegex(RuntimeError, 'points elsewhere'):
            self.install()

    def test_rejects_broken_managed_block(self):
        self.local.write_text(setup.BEGIN + '\nunfinished\n')
        with self.assertRaises(ValueError):
            self.install()

    def test_generated_config_loads_and_alt_o_creates_window_in_same_directory(self):
        self.install()
        socket = str(Path(self.temp.name)/'socket')
        tmux = self.deps['tmux']
        def tm(*args):
            return subprocess.check_output([tmux, '-S', socket, *args], text=True, stderr=subprocess.PIPE).strip()
        self.addCleanup(lambda: subprocess.run([tmux, '-S', socket, 'kill-server'], capture_output=True))
        directory = self.home/'project dir'
        directory.mkdir()
        tm('-f', str(self.home/'.tmux.conf'), 'new-session', '-d', '-s', 'check', '-c', str(directory), 'sleep 300')
        self.assertEqual(tm('show-options', '-gqv', '@keep-user-value'), 'keep me')
        self.assertEqual(tm('show-options', '-gqv', '@resurrect-processes'), 'claude opencode grok')
        self.assertEqual(tm('show-options', '-gqv', '@resurrect-save-command-strategy'), 'cxt-macos')
        for agent in ('claude', 'opencode', 'grok'):
            self.assertEqual(tm('show-options', '-gqv', '@resurrect-strategy-' + agent), 'cxt-macos')
        bindings = tm('list-keys', '-T', 'root').splitlines()
        parts = next(shlex.split(line) for line in bindings if shlex.split(line)[3] == 'M-o')
        self.assertEqual(len(tm('list-windows', '-t', 'check').splitlines()), 1)
        tm(*parts[4:], 'sleep 300')
        self.assertEqual(len(tm('list-windows', '-t', 'check').splitlines()), 2)
        self.assertEqual(Path(tm('display-message', '-p', '#{pane_current_path}')).resolve(), directory.resolve())
        hooks = tm('show-hooks', '-gw')
        self.assertIn('pane-lifecycle.py', hooks)
        self.assertIn('recover', hooks)
        # Exercise the generated hook, including paths with spaces and '$'.
        fake = self.home/'claude'
        fake.write_text('#!/bin/sh\nexit 0\n')
        fake.chmod(0o755)
        tm('set-option', '-g', 'default-shell', '/bin/sh')
        tm('set-window-option', '-g', 'remain-on-exit', 'on')
        pane = tm('new-window', '-d', '-P', '-F', '#{pane_id}', '-t', 'check', '-c', str(directory), str(fake))
        for _ in range(100):
            state = tm('show-options', '-pqv', '-t', pane, '@cxt12e4-pane-original')
            if '"phase":"shell"' in state and tm('display-message', '-p', '-t', pane, '#{pane_dead}') == '0':
                break
            time.sleep(.05)
        else:
            self.fail('The generated pane-died hook did not recover the test Agent')
        # Restored Agents start through the plugin's shell, so the generated
        # strategy must record the Agent before exec even with quoted paths.
        current_path = tm('show-environment', '-g', 'PATH').split('=', 1)[1]
        tm('set-environment', '-g', 'PATH', str(self.home) + ':' + current_path)
        restored = tm('new-window', '-d', '-P', '-F', '#{pane_id}', '-t', 'check', '-c', str(directory), '/bin/sh', '-i')
        wrapper = self.home/'.tmux/plugins/tmux-resurrect/strategies/claude_cxt-macos.sh'
        self.assertTrue(wrapper.stat().st_mode & 0o111)
        environment = {**os.environ, 'TMUX': socket + ',' + tm('display-message', '-p', '#{pid}') + ',0'}
        command = subprocess.check_output([str(wrapper), 'claude', str(directory)], env=environment, text=True).strip()
        tm('send-keys', '-t', restored, '-l', command)
        tm('send-keys', '-t', restored, 'Enter')
        for _ in range(100):
            raw = tm('show-options', '-pqv', '-t', restored, '@cxt12e4-pane-original')
            if raw and json.loads(raw)['phase'] == 'shell' and tm('display-message', '-p', '-t', restored, '#{pane_dead}') == '0':
                self.assertEqual(json.loads(raw)['argv'], ['claude'])
                break
            time.sleep(.05)
        else:
            self.fail('The generated restore strategy did not record and recover the Agent')


if __name__ == '__main__':
    unittest.main()
