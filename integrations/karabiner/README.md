# Mac 数字快启：固定前四项，其余跟随 Dock

这是普通键盘的可选集成，不由 Bridge 安装器自动部署。需要 macOS、Karabiner 和可用的 `/usr/bin/python3`（Xcode Command Line Tools，Python 3.9+）。

`dock-n.sh N` 每次启动都重新解析应用位：先保留 `dock-slots.txt` 中明确列出的固定项，再从 Dock 常驻应用中去掉这些已固定的应用，按剩余顺序填入未配置的编号，不缓存旧清单。随附示例固定 1–4：ChatGPT、Ghostty、Notion、Finder；所以 5 是剩余的第一个应用，6 是第二个，以此类推，0 / 10 是第 10 个启动位。

Finder 不在系统 `persistent-apps` 列表里，但仍占用固定的第 4 个启动位。因此当 Dock 中依次为 ChatGPT、Ghostty、Notion、Dia、ego lite 时，快捷键是 1 ChatGPT、2 Ghostty、3 Notion、4 Finder、5 Dia、6 ego lite。移动或从 Dock 移除前四个固定应用不会改变它们的快捷键，也不会使动态部分重复出现这些应用。

右侧“最近使用的应用”、临时运行但未固定的应用、文件夹和废纸篓不计入。空白占位仍占一个动态位置但不能启动；目标失效时提示错误，不打开相邻或旧应用。Finder 原生图标的位置和 Win + 4 编号是两回事，本集成不改动 Dock 图标位置。

## 接入已有 Karabiner 配置

1. 备份 `~/.config/karabiner/` 下已有的 `dock-n.sh`、`dock-slots.txt`，把本目录的 `dock-n.sh`、`dock-launcher.py` 放到该目录。
2. 编辑已有的 `dock-slots.txt`，只保留需要固定的编号；要使用示例则参考 `dock-slots.example.txt`。每行格式为 `编号 应用名称`，不要覆盖自己仍要保留的条目。
3. 在已有数字键规则中调用 `/bin/bash /绝对路径/.config/karabiner/dock-n.sh 5` 等；数字 0 传 `10`。保留原有设备、修饰键、Caps 和 Fn 条件，本目录不提供会覆盖个人键位的整套 Karabiner 配置。

Win、Caps、Fn 入口调用同一脚本即可保持一致。本次更新的 Mac Bridge 在这两个脚本都存在时，以 `--resolve` 查询同一套编号，再启动应用，因此键 12 + 数字和普通键盘一致；已安装的旧 Bridge 需要更新应用才能使用此规则。未安装可选脚本时，Bridge 保留原有的固定清单及原始 Dock 位置回退，避免改变其他用户的配置。脚本存在但解析失败时会提示，不悄悄改用另一种编号。

Finder 固定项会沿用本机已配置的 `hammerspoon://show-finder` 窗口定位功能；没有安装 Hammerspoon 时使用普通 Finder。已安装 Hammerspoon 的用户需在其配置中注册该 URL 处理器，或自行改用普通 `open -a Finder`。

只查看解析结果，不打开或移动窗口：

```sh
bash ~/.config/karabiner/dock-n.sh --list
bash ~/.config/karabiner/dock-n.sh --resolve 5
```

隔离测试（不访问真实 Dock、不启动应用）：

```sh
python3 -m unittest discover -s integrations/karabiner -p 'test_*.py'
```

程序读取和启动验证不等于实体键盘验收；在本机确认 Win + 1–4、Win + 5 / 0，以及 Dock 重排后的下一次按键。

## T90 USB

图阁 T90 的 2.4G 接收器是 `258a:0150`，有线键盘是 `258a:017c`。给已有的 T90 专用规则增加第二个设备条件项，可在有线模式沿用 Alt+Tab、Caps 和编辑快捷键。在现有 `device_if` 的 `identifiers` 数组中并列这两个标识，保留同一规则中的修饰键和变量条件。

```json
{
  "type": "device_if",
  "identifiers": [
    {"vendor_id": 9610, "product_id": 336, "is_keyboard": true},
    {"vendor_id": 9610, "product_id": 380, "is_keyboard": true}
  ]
}
```

这是已有 T90 配置的可选示例。用支持数据传输的线连接键盘，先确认 Karabiner 识别到 `258a:017c`，再用实体左 Alt + Tab 验证快捷键。
