# 隔离回归测试

从仓库根目录或任意目录运行：

```sh
python3 integrations/hammerspoon/tests/run.py
```

运行器优先寻找 Lua 5.4（`lua5.4` / `lua54` / `lua`），也可用 `--lua /path/to/lua` 指定。macOS 未安装独立 Lua 时，回退到 `/Applications/Hammerspoon.app` 内的 LuaSkin；自定义位置使用 `--luaskin /path/to/LuaSkin`。`--verbose` 显示逐项结果。

每个测试文件在独立子进程 / Lua VM 中运行。所有 `hs` API 都是 mock；测试不会连接正在运行的 Hammerspoon、操作实际窗口、启动应用、发送键鼠输入或修改 tmux 状态。需要拖动模块的场景也只在 mock VM 中加载本目录随附的 `window_span_drag.lua`。

| 测试文件 | 检查数 | 内容 |
| --- | ---: | --- |
| `test_presentation.lua` | 31 | 输入释放预算、原生全屏退出、按序动画、有限聚焦重试、Show Desktop / Mission Control 保护、诊断 |
| `test_preparation_rollback.lua` | 11 | 准备阶段快照、全屏回滚、撤销次序、窗口已关闭 / AX 异常 |
| `test_workspace_layouts.lua` | 90 | 应用获取、缺少窗口、Notion 唤回、Ghostty 新窗口、队列、回滚 |
| `test_workspace_retry.lua` | 15 | 跨屏尺寸重试、恢复原生全屏、精确恢复普通窗口 |
| `test_obsidian_span.lua` | 15 | 跨屏 Dock 边界（含 ChatGPT / Codex / Obsidian）、动态 Dock 高度、水平跨屏不变、轻微屏幕错位、A 恢复、固定布局与撤销、维护修复、拒绝单屏及错误宽度 |
| `test_layout_failure_diagnostics.lua` | 3 | 失败信息、恢复及真实用户取消 |
| `test_routing.lua` | 3 | 布局快捷键路由和菜单 |
| `test_placement.lua` | 2 | Claude 布局和默认配置 |
| **合计** | **170** | |

默认加载公开包中的源码和示例角色占位符。可设置绝对路径 `WM_TEST_SOURCE`、`LAYOUT_TEST_SOURCE` 对比其他源码；显示器默认值必须与公开测试夹具相同。测试夹具中的 `REPLACE_WITH_*` 和 `TEST_UNUSED_SCREEN_UUID` 是合成标识，不对应真实设备。

这些检查不证明实体键盘映射、辅助功能授权、真实应用启动速度或 macOS 原生全屏动画时序；这些需要在自己的设备上验证。
