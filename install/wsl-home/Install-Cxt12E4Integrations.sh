#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
stamp="$(date +%Y%m%d-%H%M%S)-$$"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    printf 'Required command is missing: %s\n' "$1" >&2
    exit 2
  fi
}

for command_name in bash install python3 wslpath cmd.exe flock tmux; do
  require_command "$command_name"
done

windows_local_appdata="$(cmd.exe /d /c echo '%LOCALAPPDATA%' 2>/dev/null | tr -d '\r' | tail -n 1)"
if [[ ! "$windows_local_appdata" =~ ^[A-Za-z]:\\ ]]; then
  printf 'Unable to resolve Windows LOCALAPPDATA through WSL interop: %s\n' "$windows_local_appdata" >&2
  exit 3
fi
windows_local_appdata_posix="$(wslpath -u "$windows_local_appdata")"
if [[ "$windows_local_appdata_posix" != /* || "$windows_local_appdata_posix" == *$'\n'* || "$windows_local_appdata_posix" == *$'\r'* ]]; then
  printf 'wslpath did not return one absolute Windows LOCALAPPDATA directory.\n' >&2
  exit 3
fi
status_root="${windows_local_appdata_posix%/}/Cxt1024Bridge/terminal-status"

bin_dir="$HOME/.local/bin"
opencode_plugin_dir="$HOME/.config/opencode/plugins"
claude_settings="$HOME/.claude/settings.json"
grok_hook_dir="$HOME/.grok/hooks"

mkdir -p -- "$bin_dir" "$opencode_plugin_dir" "$(dirname -- "$claude_settings")" "$grok_hook_dir"

backup_if_present() {
  local target="$1"
  if [[ -e "$target" ]]; then
    cp -a -- "$target" "$target.bak-$stamp"
  fi
}

render_portable() {
  local source="$1"
  local destination="$2"
  python3 - "$source" "$destination" "$status_root" <<'PY'
import pathlib
import shlex
import sys

source, destination, status_root = sys.argv[1:]
data = pathlib.Path(source).read_text(encoding="utf-8")
data = data.replace("__CXT12E4_STATUS_ROOT_SHELL__", shlex.quote(status_root))
pathlib.Path(destination).write_text(data, encoding="utf-8")
PY
}

install_rendered() {
  local source="$1"
  local target="$2"
  local mode="$3"
  local temporary
  temporary="$(mktemp)"
  render_portable "$source" "$temporary"
  backup_if_present "$target"
  install -m "$mode" -- "$temporary" "$target"
  rm -f -- "$temporary"
}

install_rendered \
  "$repository_root/integrations/terminal-controls/cxt-l2-agent-action" \
  "$bin_dir/cxt-l2-agent-action" 0755
install_rendered \
  "$repository_root/integrations/terminal-status/cxt12e4-agent-status" \
  "$bin_dir/cxt12e4-agent-status" 0755
install_rendered \
  "$repository_root/integrations/terminal-status/cxt12e4-claude-idle" \
  "$bin_dir/cxt12e4-claude-idle" 0755
install_rendered \
  "$repository_root/integrations/terminal-status/cxt12e4-terminal-lights.js" \
  "$opencode_plugin_dir/cxt12e4-terminal-lights.js" 0644
install_rendered \
  "$repository_root/integrations/terminal-status/grok-hooks.json" \
  "$grok_hook_dir/cxt12e4-terminal-lights.json" 0644

claude_template="$(mktemp)"
claude_merged="$(mktemp)"
render_portable \
  "$repository_root/integrations/terminal-status/claude-settings.json" \
  "$claude_template"

python3 - "$claude_settings" "$claude_template" "$claude_merged" <<'PY'
import json
import pathlib
import sys

settings_path, template_path, output_path = map(pathlib.Path, sys.argv[1:])
if settings_path.exists():
    settings = json.loads(settings_path.read_text(encoding="utf-8"))
    if not isinstance(settings, dict):
        raise SystemExit("Claude settings root must be a JSON object")
else:
    settings = {}

template = json.loads(template_path.read_text(encoding="utf-8"))
hooks = settings.setdefault("hooks", {})
if not isinstance(hooks, dict):
    raise SystemExit("Claude settings 'hooks' must be a JSON object")

def is_cxt_command(item):
    return isinstance(item, dict) and "cxt12e4-" in str(item.get("command", ""))

for event_name, event_groups in list(hooks.items()):
    if not isinstance(event_groups, list):
        continue
    cleaned_groups = []
    for group in event_groups:
        if not isinstance(group, dict):
            cleaned_groups.append(group)
            continue
        group_copy = dict(group)
        commands = group_copy.get("hooks")
        if isinstance(commands, list):
            group_copy["hooks"] = [item for item in commands if not is_cxt_command(item)]
            if not group_copy["hooks"]:
                continue
        cleaned_groups.append(group_copy)
    hooks[event_name] = cleaned_groups

for event_name, event_groups in template.get("hooks", {}).items():
    hooks.setdefault(event_name, [])
    hooks[event_name].extend(event_groups)

output_path.write_text(
    json.dumps(settings, ensure_ascii=False, indent=2) + "\n",
    encoding="utf-8",
)
PY

backup_if_present "$claude_settings"
install -m 0644 -- "$claude_merged" "$claude_settings"
rm -f -- "$claude_template" "$claude_merged"

bash -n "$bin_dir/cxt-l2-agent-action"
bash -n "$bin_dir/cxt12e4-agent-status"
bash -n "$bin_dir/cxt12e4-claude-idle"
python3 -m json.tool "$claude_settings" >/dev/null
python3 -m json.tool "$grok_hook_dir/cxt12e4-terminal-lights.json" >/dev/null
if command -v node >/dev/null 2>&1; then
  node --check "$opencode_plugin_dir/cxt12e4-terminal-lights.js" >/dev/null
fi

printf 'CXT12E4 WSL integrations installed.\n'
printf 'Windows status directory: %s\n' "$status_root"
printf 'Backups use suffix: .bak-%s\n' "$stamp"
printf 'Restart OpenCode/Claude Code/Grok, or reload their hooks, before checking status lights.\n'
