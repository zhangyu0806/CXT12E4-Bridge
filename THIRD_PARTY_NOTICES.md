# License scope and upstream software

The Windows and macOS Bridge applications, integration scripts, installers,
tests and project documentation are licensed under the [MIT License](LICENSE),
except for files that explicitly carry another license notice.

## QMK keymap and firmware

`firmware/cxt12e4_chatgpt_bridge/` is licensed under **GPL-2.0-or-later**.
The existing SPDX notices in `keymap.c` and `config.h` remain authoritative;
the root MIT license does not replace them. A copy of GPL version 2 is provided
in [the firmware directory](firmware/cxt12e4_chatgpt_bridge/LICENSE).

The keymap is built with [QMK Firmware](https://github.com/qmk/qmk_firmware),
which is obtained separately and has its own license and third-party notices.
The QMK tree, manufacturer firmware, bootloaders and prebuilt firmware images
are not included in this repository. When distributing a compiled firmware
image, follow the applicable licenses for the full source and components used
to build that image.

## Build and runtime dependencies

Apple frameworks, Microsoft .NET Framework/.NET SDK, Karabiner-Elements, tmux
and the applications controlled by the Bridge are separate dependencies. Their
names describe interoperability; this project does not grant rights to their
software or trademarks. The cross-build helper downloads Microsoft reference
assemblies for compilation and does not bundle them in the Windows installer
package.

This is an independent community project, not an official CXT, QMK, Microsoft,
Apple, OpenAI or Anthropic product.
