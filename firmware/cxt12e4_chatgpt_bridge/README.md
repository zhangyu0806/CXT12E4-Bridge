# CXT 12E4 ChatGPT bridge keymap

Target: `cxt_studio/12e4` from the official QMK firmware repository.

This keymap deliberately has one firmware layer. All 12 keys and all 12 encoder
gestures emit private F13–F24 bridge triggers. Logical L0–L7 selection remains in
the Windows or macOS Bridge; no encoder changes layers. Key 12 keeps its current-layer
short action, holds tmux session slot 04 at 650 ms, and keeps taskbar-chord
priority when combined with keys 1–11.

On logical L1, tapping key 4 forks the current task. The former key-4 chord
prefix is removed. This host-side behavior requires no firmware change.

The Raw HID report is exactly 32 bytes:

| Offset | Value |
|---:|---|
| 0–3 | ASCII `C12E` |
| 4 | protocol version `1` |
| 5 | `1` ping, `2` set state, `3` raw RGB test, `0x7F` bootloader; response sets bit `0x80` |
| 6 | logical layer `0..7`, or physical key `1..12` for raw RGB test |
| 7–12 | task slots 1–6: `0` off, `1` idle, `2` thinking, `3` completed, `4` waiting, `5` error |
| 13 | host sequence number |
| 14 | danger pre-trigger flag: `1` lights physical key 9 red, `0` clears it |
| 15–20 | tmux session slots 1–6, using the same state values as task slots |
| 21–31 | reserved |

Lighting policy:

- On every logical layer, keys 1, 2, 3, 5, 6, 7 show the six ChatGPT task states.
  Thinking keys animate with a phase-shifted deep-blue flow; other task states
  stay solid.
- On every logical layer, keys 9, 10, 11, 12, 8, 4 show tmux sessions 01–06.
- L1–L7 map directly to physical keys 1–7, and fallback L0 maps to key8. The
  matching key starts a breathing double-flash as soon as the layer changes and repeats it about every 1.5
  seconds, then restores that key's live task/tmux state. Thinking remains a
  separate phase-shifted deep-blue breathing animation on the status banks.
- A set-state packet with byte 14 equal to `1` temporarily overrides physical
  key 9 with red danger feedback. The next packet with `0` restores normal
  task/layer rendering.
- If the bridge heartbeat stops for 3 seconds, all LEDs turn off.

## Build from source

Obtain [QMK Firmware](https://github.com/qmk/qmk_firmware) and its build
prerequisites separately. The keymap was built against QMK commit
`0456ffb38e67160e2c59a91ab00f424d3765f1f6` with AVR GCC 14.3.0. Use that revision
when reproducing the existing build; other upstream revisions require a fresh
build and hardware check.

Copy this directory into the QMK checkout as
`keyboards/cxt_studio/12e4/keymaps/chatgpt_bridge`, then run from the QMK root:

```sh
make cxt_studio/12e4:chatgpt_bridge
```

This builds a firmware image; it does not flash a connected keyboard. No
prebuilt image, manufacturer recovery file or private hardware readback is
included in this repository.

## Hardware and flashing

The intended board is QMK's `cxt_studio/12e4` with an ATmega32U4 and the Atmel
DFU bootloader. A similar product name or a matching USB ID alone is not enough
to establish that a different hardware revision is compatible.

Bridge installation never flashes firmware. Before flashing manually, identify
the board revision, obtain a compatible recovery image and preserve the
existing firmware. Do not write over the bootloader region. On the verified
board definition, bootmagic uses matrix position `(0, 0)` (physical key 1) held
while reconnecting USB; verify the applicable board definition for your device.

After flashing, reconnect normally, check the Raw HID protocol version, and
verify the keys, encoders and both lighting banks in a disposable test window.
A successful build does not establish those physical behaviors.

## License

This directory is licensed under **GPL-2.0-or-later**; see [LICENSE](LICENSE).
QMK itself and its dependencies retain their upstream licenses. When sharing a
compiled image, include the corresponding source and notices required by those
licenses.
