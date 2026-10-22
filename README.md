# CXT12E4 Bridge

为 CXT 12 键、4 旋钮键盘和 Xbox 手柄提供 Windows / macOS 桌面 Bridge：随前台应用切换八层按键，用状态灯显示 Codex 任务与 tmux 会话状态，并支持按设备、按层自定义动作。

Application-aware keyboard and controller bridges for Windows and macOS, with a QMK keymap and optional terminal status integrations.

[Windows 手册](https://cxt12e4.11166666.xyz/index.html) · [Windows 快捷键](https://cxt12e4.11166666.xyz/shortcuts.html) · [Mac 手册](https://cxt12e4.11166666.xyz/mac/index.html) · [Mac tmux 配置](https://cxt12e4.11166666.xyz/mac/tmux.html) · [个人快捷键参考](https://cxt12e4.11166666.xyz/)

## 项目包含什么

| 部分 | 用途 |
| --- | --- |
| Windows Bridge | 托盘程序，读取键盘和 XInput 手柄，在本机执行快捷键、切换任务与控制状态灯。 |
| macOS Bridge | 原生菜单栏程序，配合 Karabiner-Elements 接收键盘输入，通过 GameController 读取手柄。 |
| QMK keymap | 针对 `cxt_studio/12e4` 的自定义固件源码，发送私有触发码并接收灯光状态。 |
| 可选终端集成 | 为 tmux、Claude Code、OpenCode、Grok 连接会话和任务状态。 |
| HTML 手册 | 按设备、应用层查看映射；可搜索、打印，也随 Mac 应用附带离线副本。 |

键盘固件负责输入协议和灯光，桌面 Bridge 负责本机操作；在线手册不代替本机 Bridge。已有兼容固件时，安装或更新 Bridge 无需重新刷写。

“个人快捷键参考”记录维护者的 T90 / Karabiner / Hammerspoon / tmux 环境，包括三屏布局、Caps 和应用数字快启；这些是配置示例，**不会随 Bridge 安装自动部署**。

## 获取源码

安装 Git 后，在终端运行；也可以下载 GitHub 的源码 ZIP 后解压：

```sh
git clone https://github.com/zhangyu0806/CXT12E4-Bridge.git
cd CXT12E4-Bridge
```

默认 `main` 包含两端源码。以下命令都从仓库根目录执行。生成的应用、EXE、固件产物和本机配置不存入 Git。

## Windows 安装

要求 Windows 10/11 x64，以及系统的 .NET Framework 64 位 C# 编译器。键盘需先有本项目的兼容固件；Xbox 通过 USB 或蓝牙连接，并由 XInput 识别。安装器无需管理员权限。

在 PowerShell 运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Install-Cxt12E4Bridge.ps1
```

安装器从源码构建并自测，通过后备份旧版，安装用户级 Bridge 和 watchdog，设置当前用户登录启动并验证进程。默认运行目录是 `outputs\home-runtime`，安装后不要删除它；可用 `-InstallDirectory` 指定长期保留的目录。

需要已有 WSL 环境中的终端状态集成时，增加 `-InstallWslIntegrations`。该选项安装 CXT 集成，不安装 WSL、tmux 或 Agent 本体。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Verify-Cxt12E4Bridge.ps1
```

详细的准备、更新、启动项、离线准备与卸载步骤见 [Windows 安装说明](install/windows-home/README.md)；WSL 部分见 [WSL 集成说明](install/wsl-home/README.md)。

## macOS 安装

要求 macOS 13+、Xcode Command Line Tools（`xcode-select --install`）和 Python 3。使用 12E4 键盘还需启用支持 `send_user_command` 的 Karabiner-Elements；项目曾在 16.3.0 验证此输入通道。当前安装器会配置 Karabiner，运行前应至少打开过一次 Karabiner，生成用户配置。

```sh
./install/macos/install.sh
```

安装器构建和自测后，安装 `/Applications/CXT12E4 Bridge.app`，合并只匹配 12E4 的输入规则，并注册当前用户的登录启动项。若已有受支持的 Agent 配置，还会合并相关状态 hooks；改动的旧设置会备份。它不会为你安装目标应用。

首次运行时，在“系统设置 → 隐私与安全性”中开启 **CXT12E4 Bridge 的辅助功能**，按提示允许输入监控；Karabiner 的驱动和权限也需就绪。Finder 相关动作可能请求自动化权限。保持 Karabiner 运行。

现成的 tmux 配置适配是可选项，针对已有的个人 `winsettings` 配置；该配置尚未公开，也不包含在本项目中。没有它时，安装器跳过这套 tmux 配置，Bridge 本体仍可安装和使用；不会自动获得手册示例中的全部终端快捷键。已有配置但缺依赖时会在替换旧 Bridge 前报错。已有配置用户的本地路径参数与完整要求见 [Mac 安装说明](install/macos/README.md)。

## 开始使用与自定义

1. 连接设备，确认托盘或菜单栏有彩色数字 `1–8`。
2. 切换到目标应用，观察当前层变化；也可从 Bridge 菜单手选层。
3. 打开“分层按键设置…”，选择 **12E4 / Xbox → 应用层 → 控件**。未自定义的控件使用该层默认值。
4. 从菜单打开“使用说明”查看对应平台的在线手册；“快捷键总表”打开对应平台的快捷键页。手册顶部先选 Windows / Mac，再选 12E4 / 手柄 / 快捷键。Mac 的“离线说明”打开应用内副本，断网时可用。

12E4 短按在松开时执行；长按约 650 毫秒切换任务或会话。键 `1、2、3、5、6、7` 对应六个 Codex 任务槽，`9、10、11、12、8、4` 对应六个 tmux 灯位。按住键 12 再按其他键是应用启动组合。长按和应用组合固定，普通短按与旋钮动作可在设置里覆盖。两端的应用启动来源和 tmux 编号规则不同，以对应平台手册为准。

默认八层为 Codex / ChatGPT、终端 / tmux、Notion、Obsidian、Blender、文件管理器、通讯、浏览器 / 通用。Windows 的文件管理器层面向 Total Commander；Mac 使用 Finder。Xbox 与键盘共用当前层，但分别保存映射。先在测试窗口熟悉鼠标、拖拽、确认和取消，再操作工作内容；手柄 A 默认发送 Enter，在终端会执行当前输入。

| 平台 | 自定义配置 | 日志 |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%\Cxt1024Bridge\profiles.windows.json` | 同目录的 `bridge.log` |
| macOS | `~/Library/Application Support/CXT12E4Bridge/profiles.json` | 同目录的 `bridge.log` |

优先用设置界面修改。升级会保留已有覆盖；“恢复本层默认”会重置整个当前设备、当前层。两端配置文件不可直接互换。Mac 应用数字槽可从 `~/.config/karabiner/dock-slots.txt` 读取，未配置的槽位回退到本机 Dock 顺序；手册中的固定应用列表是维护者的例子。

截图、剪贴板等动作依赖相应第三方工具及其快捷键：例如 PixPin，Windows 的 CopyQ / Total Commander，以及 Mac 的 Raycast / Cursor。未使用这些工具时，可在分层设置中换成适合自己的动作。完整按键和旋钮表见在线手册或 [Windows 详细说明](README-CXT12E4.md)。

## 构建与检查

只构建 Windows 程序，不安装：

```powershell
.\build.ps1
.\outputs\Cxt12E4Bridge.exe --self-test
# 发布用的无控制台窗口版本
.\build.ps1 -Windowed
```

macOS 构建（包含原生自测，不安装应用）：

```sh
./install/macos/build.sh
python3 -m unittest discover -s src/Cxt12E4Mac -p 'test_*.py'
```

完整 tmux 安装测试需要已配置的本机工具和插件，不属于干净环境的基础检查；命令与依赖见 [贡献说明](CONTRIBUTING.md#按改动范围验证)。

手册检查需要 Node.js：

```sh
node tests/manual-navigation.test.js
node tests/layer-profile-manual.test.js
```

控制器映射检查还需当前构建的两端默认配置导出，命令见 [贡献说明](CONTRIBUTING.md)。Windows 原生 CI 会构建、自测并验证安装准备流程；[交叉构建](install/windows-home/BUILDING-CROSS.md) 和 .NET 8 可移植测试是补充，不能替代 Windows 运行验证。自动检查也不等于实体键盘、蓝牙手柄或目标应用的操作已通过。

## 固件与兼容性

键盘目标为 QMK 的 `cxt_studio/12e4`。当前 Bridge 面向项目自定义固件的 USB 标识 `5754:c401`、固件版本 `0100`；原厂固件不能直接当作同一输入协议使用。构建 keymap、协议和恢复信息见 [固件说明](firmware/cxt12e4_chatgpt_bridge/README.md)。仓库不包含完整 QMK 源码；需在匹配硬件的 QMK 工作树中构建，桌面安装器不会执行刷写。

应用快捷键和辅助功能控件依赖应用版本、语言、焦点与权限。例如模型选择、Obsidian 命令、Notion 全宽菜单和通讯输入框识别都有适用范围。Windows 的 Obsidian 模板 / 大纲自动选择仍有旧版库路径依赖，不能自动识别任意库；可在分层设置中替换此动作。

Codex 六任务槽来自本机可读取的任务清单，不代表所有云端聊天或侧栏分组。终端动作需要已有 tmux 会话；多个客户端时可能需要指定 `CXT_TMUX_TTY`，Bridge 不会据此自动创建会话或迁移其他机器正在运行的进程。

Mac 手柄输入不独占，前台游戏也可能收到按键。进入游戏前暂停 Xbox，或把实际游戏的 Bundle ID 加入暂停名单；只加入 Steam 等启动器不能覆盖它启动的全部游戏。

## 目录

```text
src/Cxt1024Bridge/              Windows Bridge（历史目录名）
src/Cxt12E4Mac/                 macOS Bridge 与 Python helper
firmware/cxt12e4_chatgpt_bridge/ QMK keymap 和灯光协议
integrations/                  Agent / tmux 状态集成
install/windows-home/          Windows 安装、验证和卸载
install/macos/                 Mac 构建、安装与 tmux 适配
install/wsl-home/              WSL 状态集成安装
docs/cxt12e4-manual/            在线及随应用打包的 HTML 手册
tests/                         导航、映射、终端等检查
```

## 反馈与贡献

欢迎通过 [GitHub Issues](https://github.com/zhangyu0806/CXT12E4-Bridge/issues) 报告问题，或提交 Pull Request。请附系统版本、设备型号、目标应用版本、最小复现步骤和脱敏后的相关日志；不要上传完整个人配置、任务数据库、聊天内容或凭据。

开发前请看 [CONTRIBUTING.md](CONTRIBUTING.md)。修改映射或安装行为时，同时更新对应平台手册，并清楚区分自动检查与实机结果。

## 许可证

桌面 Bridge、安装工具、集成脚本和文档采用 [MIT 许可证](LICENSE)，有单独声明的文件除外。QMK keymap 位于 `firmware/cxt12e4_chatgpt_bridge/`，沿用 **GPL-2.0-or-later**；QMK 与其他第三方软件仍使用各自的许可证。完整范围见 [第三方与许可说明](THIRD_PARTY_NOTICES.md)。
