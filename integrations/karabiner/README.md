# Mac 数字快启：固定前四项，其余跟随 Dock

这是普通键盘的可选集成，不由 Bridge 安装器自动部署。需要 macOS、Karabiner 和可用的 `/usr/bin/python3`（Xcode Command Line Tools，Python 3.9+）。

`dock-n.sh N` 每次启动都重新解析应用位：`dock-slots.txt` 中明确列出的编号保持固定，其他编号读取当前 Dock 的 `persistent-apps` 顺序，不缓存旧清单。随附示例只固定 1–4：ChatGPT、Ghostty、Notion、Finder；5–9 对应 Dock 第 5–9 位，0 / 10 对应第 10 位。以后拖动常驻图标，下一次按键即使用新位置。

编号从 Finder 后面的常驻应用区域开始，Finder 自身、右侧“最近使用的应用”、临时运行但未固定的应用、文件夹和废纸篓不计入。不会先扣掉四个固定应用再重新计数；固定覆盖某个编号也不会挤动其他编号。自定义的空白占位保留其序号且无法启动，不会错开到下一个应用。目标已删除或位置不存在时提示错误，不退回旧应用。

## 接入已有 Karabiner 配置

1. 备份 `~/.config/karabiner/` 下已有的 `dock-n.sh`、`dock-slots.txt`，把本目录的 `dock-n.sh`、`dock-launcher.py` 放到该目录。
2. 编辑已有的 `dock-slots.txt`，只保留需要固定的编号；要使用示例则参考 `dock-slots.example.txt`。每行格式为 `编号 应用名称`，不要覆盖自己仍要保留的条目。
3. 在已有数字键规则中调用 `/bin/bash /绝对路径/.config/karabiner/dock-n.sh 5` 等；数字 0 传 `10`。保留原有设备、修饰键、Caps 和 Fn 条件，本目录不提供会覆盖个人键位的整套 Karabiner 配置。

Win、Caps、Fn 入口调用同一脚本即可保持一致。Mac Bridge 的键 12 + 数字原本就支持这份固定清单和未配置位置的 Dock 回退；清单只剩 1–4 时，12E4 的 5–11 也随 Dock 顺序变化，不需重装 Bridge。

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
