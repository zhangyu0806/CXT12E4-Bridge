# Hammerspoon 多屏布局（可选集成）

用全局快捷键摆放常用应用：左侧上下两屏，右侧一块竖屏；支持当前窗口分区、跨屏、整套布局及撤销。这里公开的是独立的 Hammerspoon 模块，**CXT12E4 Bridge 不会自动安装或启用它**。不需要连接 12E4 也能使用。

2026-10-26 补充 Obsidian 跨屏兼容：上下双屏铺满时，保留底部 Dock 占用的空间，避免窗口已经跨屏却因尺寸校验失败而恢复到单屏。仍隐藏顶部菜单栏；再次 A 可恢复，固定布局与跨屏维护使用同一边界。其他应用、右侧单屏铺满和原有快捷键保持原行为。

本包包括 2026-10-22 的窗口准备修复：固定布局会先有序退出目标窗口的 macOS 原生全屏，再检查窗口是否真正显示；按住快捷键的时间不再占用窗口准备预算；临时聚焦失败最多重试三次；失败和撤销会使用原始窗口身份及快照，避免移动已关闭窗口的替代品。

## 安装与本机配置

需要 macOS、Hammerspoon、辅助功能授权，以及用于编译小型菜单栏高度读取工具的 Xcode Command Line Tools。本包不修改 Caps Lock、Karabiner、tmux 或 Dock 应用编号。

1. 先备份自己的 `~/.hammerspoon` 目录。例如：

   ```sh
   cp -a "$HOME/.hammerspoon" "$HOME/.hammerspoon.backup-$(date +%Y%m%d-%H%M%S)"
   ```

   初次使用且目录不存在时，先创建 `~/.hammerspoon`。已有配置请逐项合并，尤其是 `init.lua` 和现有同名模块。

2. 从本目录手动复制四个模块到 `~/.hammerspoon/`：`window_manager.lua`、`workspace_layouts.lua`、`window_span_drag.lua`、`menu_bar_mode.lua`。把 `helpers/menu-bar-inset.swift` 放到 `~/.hammerspoon/helpers/`。不提供会覆盖现有配置的自动安装脚本。

3. 在自己的 Mac 上编译辅助工具：

   ```sh
   xcrun swiftc "$HOME/.hammerspoon/helpers/menu-bar-inset.swift" \
     -o "$HOME/.hammerspoon/helpers/menu-bar-inset"
   ```

   该工具只读取主显示器的菜单栏顶部占用高度，不激活应用。源文件随包提供，生成的二进制不进仓库。

4. 将 `workspace_layouts.example.json` 复制为 `~/.hammerspoon/workspace_layouts.json`。在 Hammerspoon Console 执行以下只读命令，找到本机显示器 UUID 和坐标：

   ```lua
   for _, screen in ipairs(hs.screen.allScreens()) do
     print(screen:name(), screen:getUUID(), hs.inspect(screen:fullFrame()))
   end
   ```

   把 `roles.leftTop`、`roles.leftBottom`、`roles.right` 的 `REPLACE_WITH_*` 改成本机 UUID。保留占位符时，示例启动代码会提示配置缺失，固定布局不能使用。显示器顺序由角色确定，不依赖 Dock 顺序。`apps` 使用应用 bundle ID；修改后也应更新相应预设说明。

5. 阅读并把 [`init.example.lua`](init.example.lua) 合并到自己的 `init.lua`，再 Reload Config。示例会保留并调用原 shutdown callback，但合并时仍需检查与已有模块、快捷键及全局变量是否冲突。

6. 若要让一个窗口跨越两块屏幕，需要在系统设置 → 桌面与程序坞中关闭“显示器具有单独的空间”，并按 macOS 提示注销后重新登录。左侧上下两屏应在系统显示器排列中对齐。仅单屏布局不要求关闭该设置。无需更改显示器镜像 / 扩展模式。

菜单栏隐藏只用于“跨两屏并铺满”的窗口模式，退出该模式时恢复原偏好。正常停止模块请先调用 `workspaceLayouts.stop()` 和 `wm.stop()`；示例已加入关闭回调。

关闭“显示器具有单独的空间”后，各显示器共享桌面空间。此时进入 macOS 原生全屏会切换空间，其他屏幕的普通窗口也会暂时不见；退出全屏后返回。维护者的外部 Karabiner 配置将 F11 映射为 Control + Command + F，因此也有此效果。希望当前单屏最大化且保留其他屏幕时，先退出原生全屏，再用实体 Win + ↑（同样是外部映射）；它保留应用工具栏。四修饰键 + A 使用桌面窗口铺满，左侧跨两屏、右侧铺满单屏。不要为了改变 F11 的效果直接开启独立空间，否则左侧跨屏及 Z 预设将无法使用。本包不安装这些 F11 / Win 映射。

## 快捷键与预设

模块收到的修饰键为 **Control + Option + Command + Shift**。示例增加了直接按这四键 + X / C / L 的绑定。已有 T90 的“Caps 代替 Command + Shift + 实体 Win”是**外部 Karabiner 映射**，不由本包定义：原配置会把 X / C / L 转成相同四修饰键 + F17 / F18 / F19。这里保留了这些兼容入口，不会自行修改实体 Win、Alt 或 Caps 的意义。

| 四修饰键 + | 动作 | 说明 |
| --- | --- | --- |
| Z | 跨屏专注 | Codex 跨左侧两屏，Notion 右屏 |
| X（兼容 F17） | 终端开发 | Codex 左上，Ghostty 左下，Safari 右屏 |
| C（兼容 F18） | 三屏办公 | Codex 左上，Safari 左下，Notion 右屏 |
| S | Codex + Claude | Codex 左上，Claude 桌面版左下，Notion 右屏 |
| A | 当前窗口铺满 / 恢复 | 左侧跨两屏（Obsidian 保留底部 Dock 空间）；右侧铺满右屏；无法跨屏时回退单屏最大化 |
| L（兼容 F19） | 当前窗口移到右屏并铺满 | 右屏按本机 `roles.right` 查找 |
| ← / → / ↑ / ↓ | 当前屏左 / 右 / 上 / 下半屏 | 上下方向就是上半屏、下半屏 |
| 1 / 2 / 3 / 4 | 当前窗口移到对应屏幕 | 按坐标从左到右、同列从上到下编号 |
| R | 恢复当前窗口 | 使用本次调整前的位置 |
| 空格 | 布局菜单 | 可选整套布局、当前窗口分区和“撤销上次整套布局” |

兼容内部入口 U / M / N / O 分别为最大化、恢复或最小化、左半屏、右半屏，用于既有实体 Win + 方向键映射。需要换按法时，可在自己的 Hammerspoon 配置中绑定 `wm.dispatch("workspaceClaude")` 等命名动作，避免同时启用互相重叠的 remapper 规则。

三屏预设的所有角色必须在线。两屏测试时，请复制配置，在所有保留的预设中删去不参与布局的 placement，并删除未使用的 roles 项；保留有效的 defaultPreset。剩余角色都要填写自己的显示器 UUID，不能留下占位符。配置好后也可使用 A、分区和屏幕移动功能；不要给两个角色填同一块屏幕来伪装成三屏。

## 应用没打开时会怎样

| 应用状态 | 行为 |
| --- | --- |
| 已安装，但进程未运行 | 按 bundle ID 启动一次，最多约 8 秒等待可调整窗口；冷启动较慢可在应用出现后重新按一次 |
| 窗口存在但最小化 / 应用隐藏 | 保留原窗口，布局阶段取消最小化 / 取消隐藏 |
| 窗口处于 macOS 原生全屏 | 固定布局先退出原生全屏并等动画结束，撤销时恢复原状态 |
| Ghostty 仍运行但完全没有窗口 | 确认同一个进程持续无窗口后，执行一次 File → New Window；有现存窗口或对话框时不新建 |
| Notion 仍运行但完全没有窗口 | 确认同一个进程持续无窗口后，执行一次系统 reopen 请求 |
| Claude、Safari 或其他应用仍运行，但主窗口已关掉 | 本版本不自动给这些运行中的应用新建文档 / 窗口；若它自己没有恢复主窗口，会超时，请先从应用菜单打开窗口再重试 |
| 未安装应用、登录对话框、多个无法确定的候选窗口 | 提示具体应用或原因，停止本次布局 |

应用未打开不等于必定失败；关键是它能否在等待时间内提供一个可调整的标准窗口。程序会先取得整套窗口和快照，再开始布局；已启动的应用在失败后会保持运行，不会被强制退出。后续摆放失败会尝试恢复已改动的窗口。真正的用户鼠标操作会取消自动调整，保留用户当前安排。

同一布局尚在进行时重复按键会加入当前请求，不会不断重置计时；切换其他预设时保留最新一个排队请求。按住修饰键时等待释放，连续超过 15 秒会取消；窗口准备另有约 8 秒预算。失败提示会指出目标应用和原因，不再只显示笼统的“窗口尚未就绪”。

固定布局撤销仅适用于仍存在的原窗口。如果布局后关闭了 Claude 等窗口，撤销会停止并提示，不会给它新建一个窗口来代替。

## 调试与验证

Hammerspoon Console 中可只读检查：

```lua
hs.inspect(workspaceLayouts.lastAction)
hs.inspect(wm.lastPresentationFailure)
hs.inspect(wm.status())
```

状态可能含本机显示器 UUID、应用名称和进程 ID，分享前自行删去不想公开的信息。

执行 [`tests/run.py`](tests/run.py) 可运行 **168 项隔离回归检查**，详见 [测试说明](tests/README.md)。测试不触碰正在运行的 Hammerspoon 或真实窗口。公开包的运行逻辑与已验证修复保持一致；真实显示器 UUID 已换成占位符，测试绝对路径已改为包内路径。

实体按键、辅助功能授权、多屏排列、真实应用启动与全屏动画仍需要在自己的设备验证。先用单窗口分区确认快捷键，再测试整套布局和撤销。

本目录遵循仓库根目录的 [MIT License](../../LICENSE)。
