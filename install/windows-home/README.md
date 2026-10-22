# Windows 安装 CXT12E4 Bridge

这个目录是 Windows 的安装入口。键盘固件只负责发送私有触发码；自动切层、快捷键、K4 加速滚动、ChatGPT/tmux 切换和状态灯都需要 Windows Bridge 在本机运行。

## 准备

- Windows 10/11 x64。
- 使用已刷入项目自定义固件的 CXT 12E4；Xbox 可用蓝牙或 USB 连接，Bridge 通过 XInput 读取一个手柄。
- 从 [项目仓库](https://github.com/zhangyu0806/CXT12E4-Bridge) 克隆默认 `main` 分支，或下载源码 ZIP 后解压，进入含 `build.ps1` 的目录。
- Windows 自带的 .NET Framework 64 位 C# 编译器必须存在于 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。

## 推荐安装

在仓库根目录打开 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Install-Cxt12E4Bridge.ps1
```

安装器会依次执行：

1. 从当前源码构建控制台版并运行无输入、无 HID 写入的 `--self-test`；
2. 自测通过后构建窗口版；
3. 停止旧 Bridge，并在覆盖前保留时间戳备份；
4. 安装到仓库内被 Git 忽略的 `outputs\home-runtime\Cxt12E4Bridge.exe`；
5. 安装同源构建出的 `Cxt12E4Watchdog.exe`，并写入三条当前用户登录启动链路：已批准的 `HKCU\...\Run`、启动文件夹快捷方式、以及新的 watchdog 登录计划任务；
6. 移除旧的直启 Bridge Task Scheduler 路径，隐藏启动 Bridge 和 watchdog，并验证二者各有一个目标进程。

安装不需要管理员权限，也不会刷写键盘固件。

默认安装目录放在仓库 `outputs\home-runtime`，是为了让启动进程和当前安装进程看到同一份文件；如需安装到其他位置，可显式传入 `-InstallDirectory`。登录入口统一启动 watchdog，再由 watchdog 拉起和守护 Bridge。默认安装在仓库内，安装后请保留运行目录，不要删除整个源码目录。

## 同时安装 WSL/tmux Agent 状态集成

已有 WSL 环境，并使用 OpenCode、Claude Code、Grok 和 tmux 时，可运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Install-Cxt12E4Bridge.ps1 -InstallWslIntegrations
```

WSL 安装器会备份再更新用户目录中的 CXT 专用脚本、OpenCode 插件、Claude hooks 和 Grok hooks；不会安装或修改 systemd DrvFS watchdog，也不会覆盖非 CXT 的 Claude hooks。

## 打开说明

托盘“使用说明（Windows / Mac）”打开 [Windows 在线手册](https://cxt12e4.11166666.xyz/index.html?device=xbox)，顶部可切换平台；“快捷键总表”打开 [新域名首页](https://cxt12e4.11166666.xyz/)。离线时可在浏览器直接打开源码中的 `docs/cxt12e4-manual/index.html`。快捷键总表是维护者的 Mac 配置参考，不会给 Windows 自动添加那些普通键盘映射。

## 双设备设置与实机检查

安装后打开托盘“分层按键设置…”，分别选择键盘 / Xbox 和应用层，修改后点“保存并关闭”。Windows 配置在 `%LOCALAPPDATA%\Cxt1024Bridge\profiles.windows.json`；不要导入 Mac 的配置文件。升级保留已有覆盖，若旧文件已写入旧 A / B / X / Y 映射，请逐个修改并保存；“恢复本层默认”会重置整个当前设备当前层，也会清除该层其他自定义项。

八层默认统一：X 左键 / 按住拖拽、Y 右键、A 回车确认、B Esc。A 是 Enter，具体效果由前台应用决定，在终端会执行当前输入。原 X 的专用动作移到 RT+L3，L3 单按仍打开本层设置；例如 Codex 聚焦输入框、tmux 缩放 pane、Notion 快速搜索。Codex 层（L1）按下右摇杆 R3 发送 Tab，RT+R3 保留中键；其他层 R3 仍是中键。RT+L3 可先聚焦输入框，Tab 按当前焦点处理。RT+A/B/X/Y 的原有组合保留，终端层 RT+X/Y 仍是 copy-mode 和暂存 / 恢复输入。

先在空白桌面或测试窗口验证左摇杆、X 拖拽、Y 右键、A 确认、B Esc 和右摇杆滚动，再验证 Menu 短按面板 / B 关闭、Menu 长按暂停与恢复，再在 Codex 测试 R3 的 Tab 和 RT+R3 中键，最后逐层验证 RT 组合。终端测试使用已有会话，普通操作若有多个客户端需明确 `CXT_TMUX_TTY`，不会自动建 session。说明和具体默认映射见 [Windows 在线说明](https://cxt12e4.11166666.xyz/index.html?device=xbox)。

下载包的 `build-manifest.json`（原生构建）或 `package-manifest.json`（交叉构建）记录构建来源和验证范围。标为 cross-compiled 的 EXE 未在 Windows 执行过；安装器仍会在本机重新构建并完整自测。GitHub Windows runner 的原生自测也不代表手柄和应用已实测。首次安装后按在线清单确认。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Verify-Cxt12E4Bridge.ps1
```

正常结果应包含 `Verified : True`、Bridge 进程 PID、watchdog 进程 PID、`StartupApprovedRun : enabled`、`StartupShortcutApproved : enabled`、`WatchdogScheduledTask : Cxt12E4BridgeWatchdog` 和已安装 EXE 的 SHA-256。运行日志仍在：

```text
%LOCALAPPDATA%\Cxt1024Bridge\bridge.log
```

watchdog 会每 30 秒确认 Bridge 常驻进程仍在；如果 Bridge 被系统或其他程序结束，它会自动重新拉起。登录启动有三条冗余入口，重复启动会被 watchdog 的单实例互斥锁挡住。


## 只准备文件，不影响当前 Bridge

用于测试安装包或指定临时目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Install-Cxt12E4Bridge.ps1 `
  -InstallDirectory C:\Temp\Cxt12E4Bridge-Test -PrepareOnly
```

此模式会构建、自测并复制文件，但不会停止现有 Bridge、写开机启动或启动新进程。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install\windows-home\Uninstall-Cxt12E4Bridge.ps1
```

卸载只删除安装器管理的 EXE、watchdog、说明、`HKCU\...\Run` 启动项、StartupApproved 记录、启动文件夹快捷方式、新 watchdog 登录任务和旧直启 Bridge 计划任务；时间戳备份及运行日志会保留，避免误删诊断和恢复材料。
