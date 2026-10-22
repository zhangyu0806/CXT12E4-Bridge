# 参与 CXT12E4 Bridge

请从 `main` 新建分支，把一个问题的修复、检查和相关说明放在同一 Pull Request 中。小型改动可直接提交；涉及固件协议、跨平台映射或安装流程的调整，建议先开 Issue 描述预期行为。

## 开发环境

- Windows Bridge：Windows 10/11 x64、PowerShell、系统 .NET Framework 64 位 C# 编译器。构建命令见 [首页](README.md#构建与检查)。
- Mac Bridge：macOS 13+、Xcode Command Line Tools、Python 3；真实键盘输入还需要 Karabiner-Elements 和系统权限。构建与安装是分开的，见 [Mac 安装说明](install/macos/README.md)。
- 手册检查：Node.js。HTML 源文件可直接用浏览器打开；共享导航也兼容本地文件。
- 固件：匹配硬件版本的 QMK 工作树，见 [固件说明](firmware/cxt12e4_chatgpt_bridge/README.md)。不需要刷写固件来修改普通桌面动作。

## 按改动范围验证

修改 Windows 源码后，原生构建并运行 `--self-test`。修改 Mac 源码后，运行 `./install/macos/build.sh` 和相关 Python 单元测试。不要为了检查代码直接运行安装器：安装器会替换本机应用、修改用户配置和启动项。

完整 Mac tmux 安装检查需要 Homebrew 提供的 Bash 4+、Python 3.10+、tmux、fzf、GNU coreutils/findutils，以及 `~/.tmux/plugins/` 中的 TPM、tmux-resurrect、tmux-continuum。现成适配面向已有的个人 `winsettings` 配置，该配置没有随公开项目提供；基础 Bridge 构建和 helper 测试不依赖它。具备上述本机测试依赖后可运行：

```sh
python3 -m unittest discover -s install/macos -p 'test_*.py'
python3 -m unittest discover -s integrations/tmux/macos -p 'test_*.py'
```

测试使用临时目录和独立 tmux socket，但会先检查本机依赖。缺少工具时，不应把跳过的集成测试计为通过。

修改可选 Hammerspoon 窗口配置时，先运行 `python3 integrations/hammerspoon/tests/run.py`（Python 3 + Lua 5.4，或 macOS 上已安装的 Hammerspoon）。测试使用模拟窗口，不连接桌面；还应使用自己的显示器配置验证全屏、跨屏、撤销和实际按键，明确区分程序触发与实体设备验收。不要提交真实显示器 UUID、个人 init.lua、运行日志或窗口快照。

修改手册时运行：

```sh
node tests/manual-navigation.test.js
node tests/layer-profile-manual.test.js
```

同时在浏览器确认桌面和窄屏布局、设备切换、层切换、搜索及相关链接。仅文字改动不需要增加模仿实现的测试。

修改控制器默认映射时，从当前两端构建导出默认配置，再检查手册。Windows PowerShell：

```powershell
.\outputs\Cxt12E4Bridge.exe --dump-default-profiles | Set-Content -Encoding UTF8 outputs/windows-default-profiles.json
```

macOS：

```sh
'outputs/CXT12E4 Bridge.app/Contents/MacOS/CXT12E4Bridge' --dump-default-profiles > outputs/mac-default-profiles.json
```

将两个新导出文件放在同一台开发机后运行：

```sh
node tests/controller-manuals.test.js outputs/windows-default-profiles.json outputs/mac-default-profiles.json
```

没有 Windows 环境时，可用 `python3 install/windows-home/test-portable.py --export-defaults outputs/windows-default-profiles.json` 运行实际 C# 源码的补充检查；该工具需要 .NET 8 SDK，使用测试专用 JSON 适配器。报告中需明确它未验证 Windows 序列化、托盘界面、HID / XInput 和真实输入。

涉及物理按键、旋钮、灯光、手柄或焦点切换的改动，还要记录使用的设备、固件、应用版本和实际复现步骤。没有对应设备或系统时，如实列出尚未验证的部分，不把编译通过当成实机验收。

## 文档与配置边界

- 快捷键、键盘 / 手柄映射、数字快启、窗口布局或相关设置每次变更，都要在同一任务中同步可公开的配置源码 / 示例、受影响的说明和检查。包括外部 Karabiner、Hammerspoon、Ghostty、tmux 配置；不能只改本机后留下旧手册。以实际配置为准，保留 Windows / Mac 与 12E4 / 手柄 / 快捷键的分层。
- 维护者完成检查后，将审核过的源码与文档发布到 `zhangyu0806/CXT12E4-Bridge`，并同步 [在线手册](https://cxt12e4.11166666.xyz/)。只有公开提交已推送、线上内容与仓库手册一致，才算同步完成；个人配置先整理成脱敏示例，私有历史、会话链接和机器运行记录不公开。外部贡献者仍只需提交源文件。
- 默认映射修改应同步 Windows / Mac 对应的 HTML 手册、安装说明，以及 `src/Cxt12E4Mac/使用说明.html` 中受影响的内容。
- 个人配置示例要明确标注。不要让安装器依赖开发者的目录、账户、显示器身份或正在运行的会话。
- 保留已有用户覆盖、无关 hooks、终端面板和活动进程；需要迁移时写明备份和恢复方式。
- `docs/cxt12e4-manual/` 是网站和离线手册的源文件。贡献者提交源文件即可，线上部署由维护者完成，不需要服务器凭据。
- 桌面程序、工具和文档使用 MIT；QMK keymap 使用 GPL-2.0-or-later。保留已有版权与 SPDX 标注，详见 [许可范围](THIRD_PARTY_NOTICES.md)。

## Issue 与 Pull Request

说明问题触发条件、预期结果、实际结果，以及修复后发生了什么。附上执行的检查和结果，并注明未覆盖的平台。截图可以帮助解释界面或物理布局，但请遮住任务名、聊天内容和个人路径。

只提交源码、必要测试和文档。不要提交 `outputs/`、运行时配置、备份、日志、诊断快照、任务数据库、固件 / 应用二进制、签名私钥或访问令牌。排错时摘取最小的脱敏日志；完整辅助功能诊断可能包含界面上的私人文字。
