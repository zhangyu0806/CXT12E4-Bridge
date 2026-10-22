# CXT12E4 Bridge for macOS

Native menu-bar companion for Xbox controllers and the existing v11 CXT12E4 firmware (USB `5754:c401`, version `0100`). It does not flash firmware or write EEPROM. Windows sources and deployment remain separate.

## Install

Requirements: macOS 13+, Xcode Command Line Tools (Swift) and Python 3, enabled Karabiner-Elements with `send_user_command` support (validated with 16.3.0), and the custom keyboard firmware. The optional tmux configuration adapter targets an existing personal winsettings setup, which is not public and is not included in this repository; see [rebuilding an existing Mac tmux setup](#rebuild-the-mac-tmux-setup). Open Karabiner at least once to create its user configuration. Clone the default `main` branch of [CXT12E4-Bridge](https://github.com/zhangyu0806/CXT12E4-Bridge), then run from the repository root:

```sh
./install/macos/install.sh
```

Installation builds and runs the native decoder tests and Python tests, installs `/Applications/CXT12E4 Bridge.app`, merges one device-scoped Karabiner rule, installs available Claude Code/OpenCode integrations, and registers the current-user LaunchAgent `com.zhangyu.cxt12e4.bridge`. It runs `setup_tmux.py --if-configured --apply --reload` before stopping the existing Bridge. If no winsettings checkout is found, that configuration step is skipped and the Bridge itself can still be installed and used. This does not provide the full terminal shortcuts shown in the personal reference page. If a checkout exists but required tools or pinned plugins are missing, installation stops with an explicit error while the existing Bridge remains running. Existing changed Bridge settings and the previous app are backed up under `~/Library/Application Support/CXT12E4Bridge/backups/`.

Enable **CXT12E4 Bridge** in System Settings → Privacy & Security → Accessibility. The app also requests Input Monitoring for diagnostics. If prompted to quit and reopen after a permission change, do so. Karabiner's existing driver and permissions handle exclusive keyboard capture; the Bridge runs as the logged-in user. Its raw HID interface only handles LEDs.

`send_user_command` sends ordered JSON datagrams directly to a private Unix socket; there is no shell process per detent, global F-key hook, or network listener. The rules match only VID/PID `5754:c401`, including the board's private Right Control marker. Other keyboards retain their own mappings. Keep Karabiner enabled.

## Manuals

The menu item **使用说明** opens the [Mac online manual](https://cxt12e4.11166666.xyz/mac/index.html). **快捷键总表** opens the [new site home](https://cxt12e4.11166666.xyz/). **离线说明** opens the bundled guide and local HTML manual without a network connection. The online manual can be newer than the installed app.

The shortcut reference records the maintainer's T90, Karabiner, Hammerspoon and tmux setup. Its display layouts, Caps remapping and ordinary-keyboard app shortcuts are examples, not defaults deployed by this installer. The K12 app chord reads the local slot file or Dock order as described below.

## Familiar controls

```
1   2   3   4
5   6   7   8
9  10  11  12
```

- Short taps execute on release. Hold **1, 2, 3, 5, 6, 7** for 650 ms to open Codex task slots 1–6.
- Hold **9, 10, 11, 12, 8, 4** for 650 ms to switch tmux slots **1–6**. Names prefixed **01–06** pin those slots; other session names (including tmux defaults `0`, `1`, etc.) automatically occupy free slots. Live automatic assignments persist across Bridge restarts and do not shift when another session closes. Duplicate explicit numbers reserve an empty slot instead of guessing.
- Hold **12**, tap another key, then release both: launch the corresponding app from the existing `~/.config/karabiner/dock-slots.txt`. Slots not listed use the Dock's persistent-app order. Slot 11 may be unconfigured.
- Four knobs retain the Windows physical numbering. Rotations use the PCB correction `[4,3,1,2]`; knob presses use `[1,2,3,4]`. K4 rotates as accelerated vertical scrolling on every layer.
- The menu-bar badge matches the Windows layer indicator: a solid square in the same eight layer colors with a centered bold digit 1–8. L1/L4 use white digits; the other layers use black. Its tooltip shows the layer name, automatic/manual mode and connection status. Menu checkmarks identify the current layer and whether automatic switching is enabled.
- Keyboard chords and private encoder markers are mutually exclusive. Overlap or an ambiguous report cancels the input sequence until every key and marker is released.
- Changing foreground app/window cancels pending presses. Manual menu-bar layer choices persist across windows in the same app and reset when switching to another app.
- Codex task LEDs: white idle/viewed, blue running, green new completion, amber waiting, red for actual errors or an unknown abort reason. An explicit user interruption returns to white idle. The six slots use the same recent-thread ordering as the Windows SQLite fallback; they are not a mirror of all cloud ChatGPT conversations or custom sidebar groups.
- tmux LEDs use live pane/session/server identity, including creation time. Historical completed reports start acknowledged; new completions latch green until a successful keyboard hold. A communication failure retains the last verified inventory during this process lifetime.
- Layer indicators, 500 ms heartbeat, firmware's three-second timeout and 40-minute idle lighting suspension retain the Windows protocol.

## Layers and Mac substitutions

| Layer | Foreground app | Mac behavior |
|---|---|---|
| L1 | Codex / ChatGPT desktop | Codex task slots, fork, quick chat, sidebar, Changes panel, built-in browser and standalone chat. K2 rotates Codex chats; K3 word movement/composer; K4 scroll/latest. Exact GPT-6 Sol, GPT-6.1 Sol and GPT-6 Astra controls are supported. ChatGPT receives only its specifically identified sidebar/new-chat controls; Codex-specific keys are withheld. Task slots still open local Codex tasks. |
| L2 | Ghostty, Terminal, iTerm2, WezTerm, Warp | Native tmux split, select, zoom, equalize, rotate, choose sessions/windows/panes from a cross-session tree, stash prompt, copy mode and previous pane. Knobs resize panes and change windows. |
| L3 | Notion | Mac shortcuts; keys 10/11 use Command+Option+4/8 for to-do/code blocks. Horizontal table scroll and accessible Full width menu. |
| L4 | Obsidian | Mac shortcuts, command palette for daily note, URL, template/outline and backlinks. |
| L5 | Blender | Default Blender keymap and numpad view controls, with Mac undo/redo. |
| L6 | Finder | Copy; move pasted files; new folder; rename; POSIX path; names; path; search; move to Trash; compare two selected files in Cursor; preview and copy-update two selected folders; Compress menu. K1 directory history/up, K2 selection/tab, K3 tabs, K4 Quick Look. |
| L7 | WeChat / Telegram / QQ | Editing, conversation navigation and emoji. Send, clear and newline require a focused editable message composer; search fields and conversation lists are excluded. Client-specific unread is only assigned for Telegram. |
| L8 | Browsers and unmatched apps | PixPin screenshot/pin, undo/redo, cut/copy/paste/plain text, new/reopen tab, find, Raycast clipboard history. Knobs back/forward/address, tabs/close, zoom/reset, scroll/reload. |

Finder uses Mac copy/paste workflows instead of Total Commander's two-pane commands. K10 compares two selected files using Cursor. K11 previews copying the first selected directory into the second, asks for an explicit in-app confirmation, preserves extra destination files and backs up overwritten files. It does not delete destination-only files or silently mirror directories. Unavailable controls fail with a menu-bar message instead of sending guessed keystrokes. Obsidian command labels, Notion Full width, model controls and messaging shortcuts depend on the installed app version and language. The native accessible Power menu exposes the current model and effort in a live status announcement; the Bridge focuses that exact control and verifies each arrow step. K1 requests Astra Ultra/Fast and K2 requests Astra High/Standard, but reasoning effort and execution permission are separate: if Ultra presents a full-access dialog, the Bridge stops and leaves confirmation to the user. Existing permission allows the preset flow to continue. GPT-6.1 Sol is recognized exactly and preserved when moving into Astra and back; unknown model names fail closed instead of being guessed.

PixPin retains the existing `Ctrl+Alt+C` / `Ctrl+Alt+V` settings. Other common editing commands use Command; redo is Command+Shift+Z. Plain-text paste inserts text without replacing the rich clipboard. CopyQ is replaced by the installed Raycast clipboard history.

## Xbox and per-layer settings

Both devices share the current application layer and colored digit, but have independent mappings. Open **分层按键设置…** from the Bridge menu, choose **12E4** or **Xbox**, then choose L1–L8 and change a control. Missing overrides use that layer's built-in default. Reset resets the entire selected device/layer, including its other overrides. Existing saved overrides survive upgrades; edit A/B/X/Y individually to keep unrelated custom bindings, or reset that whole layer to adopt all new defaults. Keyboard holds and K12 app chords remain fixed; short taps and encoder gestures can be overridden. Defaults and action choices live in `src/Cxt12E4Mac/Profiles.swift`; the web manual's controller table is exported from those definitions.

The default controller base is left stick = pointer, right stick = two-axis scroll, X = left button/hold to drag, Y = right button, A = Enter/confirm, B = Escape, R3 = Tab in Codex (L1) / middle button in other layers, D-pad = arrow keys and L3 = current-layer settings. In Codex, RT+R3 retains the middle button; RT+L3 can focus the composer before using Tab. Tab is delivered to the current focus. Blender instead defaults its right stick to orbit. LT holds precision movement; RT selects the per-layer combination map. RT+A/B/X/Y are copy/paste/undo/redo. The four face buttons use the same defaults in all eight layers. A sends Enter with the foreground app's normal semantics (including executing terminal input); it is not a universal dialog-confirm command. Layer-specific actions previously on X now use RT+L3; plain L3 still opens the current-layer settings. RT+L3 and LB/RB follow the layer:

| Layer | RT + L3 | LB / RB |
|---|---|---|
| L1 Codex | Focus composer | Previous / next Codex task |
| L2 tmux | Zoom pane | Previous / next window |
| L3 Notion | Quick search | Page back / forward |
| L4 Obsidian | Quick switcher | Previous / next tab |
| L5 Blender | Object / edit mode | Orbit left / right |
| L6 Finder | Open selected item | Previous / next tab |
| L7 Messaging | Search | Previous / next conversation |
| L8 Browser/general | New tab | Previous / next tab |

In the settings window, mappings save immediately. Controller A completes and B or Menu closes the window after saving and validating pending parameters; invalid input keeps the window and error visible. B does not revert parameter edits here: use **撤销修改** before closing to discard pending parameters. D-pad input goes to the focused field or menu, and Menu does not stack the task chooser over settings.

Menu short press opens a **Codex tasks / existing tmux sessions** chooser. Use the D-pad to select, A to confirm, B or Menu to close. The chooser captures a stable task UUID or full session/server generation identity rather than resolving a slot again after the list changes. It does not include an app-launcher group and never creates a tmux session. Switching to another application closes the chooser without reclaiming focus. A Menu hold of about 850 ms pauses/resumes controller handling in ordinary applications; keyboard handling continues.

Xbox/Home, View and Share buttons remain system-owned and are not remapped. GameController input is not exclusive: a foreground game can receive the same physical controller input. Before playing, pause the controller manually or add the actual game's Bundle ID to the settings pause list. The default entries for Steam and Games cover those launchers only, not all games they launch. The menu item **在当前应用暂停 / 恢复 Xbox** manages the current app entry. While an application is on this list, even Menu remains untouched; resume from the menu bar or leave the application. In ordinary apps, a manual pause can be resumed with Menu hold.

The settings panel exposes dead zone, pointer speed, scroll speed, LT precision multiplier and scroll inversion. Ordinary focus/layer changes preserve an active drag; changing a stick mode requires physical neutral before motion resumes. Disconnect, sleep, pause and configuration edits release held mouse buttons; resuming requires all buttons/triggers released and sticks centered. Settings are saved under `~/Library/Application Support/CXT12E4Bridge/profiles.json`, with the preceding document copied to `profiles.previous.json`. An invalid document is rejected; the keyboard uses defaults and Xbox remains paused until fixed. This is a local configuration, not GitHub synchronization.

The controller feature and revised app mappings still require physical acceptance, including drag, double-click, precision motion, modifier transitions and application-specific actions. Automated input tests and isolated tmux tests do not establish the controller's physical feel or every app shortcut.

## Rebuild the Mac tmux setup

The Mac adaptation code is stored in [`integrations/tmux/macos/`](../../integrations/tmux/macos/), but the shared winsettings configuration it expects is personal, not public, and not bundled here. The commands in this section are for users who already have that configuration locally. New users can install the Bridge without it; these commands are not a standalone installer for a complete tmux configuration. Local values and session snapshots remain machine-specific.

For an existing setup, the minimum dependencies are tmux, Bash 4+, Python 3.10+, GNU coreutils/findutils and fzf. Install the Homebrew packages, then use that local winsettings installer's pinned plugin setup:

```sh
brew install tmux bash python coreutils findutils fzf
bash /path/to/winsettings/tmux/install.sh --apply --plugins
```

Replace `/path/to/winsettings` with the location of your existing configuration. From the CXT12E4 checkout, preview the setup first; without `--apply`, it only checks and plans:

```sh
python3 install/macos/setup_tmux.py --winsettings /path/to/winsettings
python3 install/macos/setup_tmux.py --apply --reload --winsettings /path/to/winsettings
```

The setup checks the required tools and plugins, deploys templates into `~/.config/tmux/macos/`, installs `~/.config/tmux/macos-keys.conf` and the helpers under `~/.config/tmux/macos-bin/`, and preserves user content in `~/.config/tmux/local.conf` while adding a managed source block. Changed files are backed up under `~/.local/state/tmux-macos-backups/<timestamp>-setup/`. It refuses a custom tmux entry that does not load `local.conf`, or an existing winsettings link pointing to a different source; use the existing source or migrate it explicitly first. `--reload` loads the configuration and verifies that existing pane IDs and process IDs are preserved.

Bare `tmux` now attaches to an existing session through the native server setting `default-client-command = attach-session`. The loaded server setting takes effect immediately in already-open terminals; restarting the shell is unnecessary. If no session exists, it reports that there is nothing to attach to and does not create one. Use explicit `tmux new` when you want a new session; the existing Option+Shift+S new-session binding remains available inside tmux. Automatic restore and boot policy remain unchanged.

The Mac adaptation preserves extended Shift shortcuts, overrides `Alt+O` to open a window in the current session and pane directory, and supplies a Mac pane lifecycle handler: an exiting Agent returns to the default shell; prefix → **Shift+R** restarts that pane's recorded Agent. **Alt+Shift+Q** keeps its original `kill-pane` behavior: it closes the current pane, ending the window only when it was the last pane, and ending the session only when it was the last window.

Custom tmux-resurrect save and restore strategies leave its upstream source unchanged. The `cxt-macos.sh` save strategy normalizes supported Agent names; each Agent's restore strategy registers its command inside the actual pane before `exec`, so a later exit returns to the shell and prefix → **Shift+R** can restart it. Manual restore starts a new installed `claude`, `opencode` or `grok` process; it does not promise to resume an old AI conversation. An unavailable Agent returns to the default shell. Automatic restore and tmux boot are disabled. Setup does not enable automatic cross-machine session-state publishing or migrate another machine's running processes. GitHub configuration updates and importing a saved session layout remain separate operations.

Setup adds a managed Ghostty block setting `macos-option-as-alt = true` and unbinding its `alt+arrow_left` / `alt+arrow_right` defaults so tmux receives those keys. On a new Mac, reload Ghostty with **Command+Shift+,**. Existing Karabiner editing/app mappings remain in place. Check input-method shortcuts separately: for example, WeType's **Option+Shift+H** full-/half-width hotkey can intercept the tmux resize shortcut. Disable or reassign that shortcut in the input method if it conflicts.

## tmux and agent lights

The Bridge reads the local tmux server. Installing the Bridge does not import WSL or remote Windows sessions; a separately requested layout import recreates local sessions and starts available programs. Ordinary `tmux new` sessions are detected automatically, with a white idle light even before agent hooks report a state. Use a prefix such as `01-main` only when you want a fixed slot. Lights stay off for empty slots; six sessions can be displayed at a time. Direct L2 actions require one attached client, or `CXT_TMUX_TTY` to identify a particular client; they refuse to guess between multiple terminal windows. Global session holds deliberately opt into the most recently active client, matching Windows behavior. Switches use the live session ID; chooser selections additionally validate creation time and server PID. Terminal focus follows the selected client's ancestor process rather than guessing the last terminal app; ambiguous multiple-window focus is reported. Missing-server errors are treated as an empty inventory only for reads, never as a successful switch. The K4 bottom action sends the terminal shortcut only after the tmux operation succeeds.

The installer merges CXT hooks into existing Claude settings, copies the OpenCode status plugin and sets `prompt_stash=ctrl+s` in a strict JSON TUI config. An existing `tui.jsonc` is preserved with a setup notice. It does not put TUI keybindings in the modern core `opencode.json` config. Existing unrelated hooks are preserved. Restart already-running agents to load hooks. The reporter validates live pane/server identity and uses Python time/locking instead of GNU `date` or `flock`. Optional Grok hooks are installed only when its configuration directory exists.

## Diagnostics and removal

The colored menu-bar badge identifies the current layer. Hover for its layer name, automatic/manual mode and connection status; open the menu for layer selection, logs and recent action errors. Runtime files are under `~/Library/Application Support/CXT12E4Bridge/`: `runtime.json`, `bridge.log`, `status-cache.json`, `profiles.json`, `profiles.previous.json` and `terminal-status/`. No conversation content is written into the status cache. An explicitly requested accessibility diagnostic can contain visible control labels (including task titles); it stays local and is not included in Git.

```sh
'/Applications/CXT12E4 Bridge.app/Contents/MacOS/CXT12E4Bridge' --status
'/Applications/CXT12E4 Bridge.app/Contents/MacOS/CXT12E4Bridge' --self-test
python3 -m unittest discover -s src/Cxt12E4Mac -p 'test_*.py'
```

To stop now, choose **退出 Bridge**. A clean exit stays stopped until the next login or manual app launch; crash exits restart. To remove startup registration:

```sh
launchctl bootout "gui/$(id -u)/com.zhangyu.cxt12e4.bridge"
```

Then remove its LaunchAgent/app and the rule whose description starts `CXT12E4 Bridge:`. Restore only the affected settings from the timestamped backups if desired; do not replace newer unrelated settings wholesale. The keyboard firmware remains unchanged and is still compatible with the Windows Bridge.

Implementation references: [Karabiner native user commands](https://karabiner-elements.pqrs.org/docs/json/complex-modifications-manipulator-definition/to/send-user-command/), [Apple HID open API](https://developer.apple.com/documentation/iokit/1588670-iohiddeviceopen), [Codex commands](https://learn.chatgpt.com/docs/reference/commands).

## Validation

Run the relevant checks from the repository root:

```sh
./install/macos/build.sh
python3 -m unittest discover -s src/Cxt12E4Mac -p 'test_*.py'
node tests/manual-navigation.test.js
node tests/layer-profile-manual.test.js
```

The build runs native self-tests and verifies the app signature. The helper tests do not require the personal tmux configuration. The full installer test suite below additionally requires the Homebrew tools listed above and existing TPM, tmux-resurrect and tmux-continuum plugin files under `~/.tmux/plugins/`; it is not a clean-environment baseline:

```sh
python3 -m unittest discover -s install/macos -p 'test_*.py'
```

The tmux tests use isolated servers and PTYs; these checks do not establish that a physical key reached Ghostty, or that a controller action worked in a particular app.

For a new installation, use the step-by-step checklist in the [Mac manual](https://cxt12e4.11166666.xyz/mac/index.html), first with a test window. Confirm permissions, layer changes, short taps versus holds, knob direction, task/session focus and controller release after pause or reconnect. App-specific automation depends on the installed app version, language and focus. Historical checks in the manual describe the maintainer's tested setup rather than a guarantee for every device.

See [CONTRIBUTING.md](../../CONTRIBUTING.md) for current-profile exports and mapping/manual parity checks.
