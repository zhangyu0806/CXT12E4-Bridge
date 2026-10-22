# WSL/tmux 家用集成

`Install-Cxt12E4Integrations.sh` 由 Windows 安装器的 `-InstallWslIntegrations` 开关调用，也可以在默认 WSL 发行版中单独运行。

它只管理以下 CXT 专用内容：

- `~/.local/bin/cxt-l2-agent-action`
- `~/.local/bin/cxt12e4-agent-status`
- `~/.local/bin/cxt12e4-claude-idle`
- `~/.config/opencode/plugins/cxt12e4-terminal-lights.js`
- `~/.grok/hooks/cxt12e4-terminal-lights.json`
- 合并到 `~/.claude/settings.json` 的 CXT hooks

所有已存在的目标都会先生成 `.bak-时间戳` 备份。Claude 配置采用 JSON 结构化合并：只替换命令中包含 `cxt12e4-` 的旧 hook，保留其他 hook 和设置。Windows 状态目录会在安装时根据当前 Windows 用户的 `%LOCALAPPDATA%` 自动计算，不复制开发电脑的用户名或路径。

脚本不会：

- 安装或升级 WSL、tmux、OpenCode、Claude Code、Grok；
- 覆盖整个 tmux 配置；
- 安装需要 `sudo` 的 DrvFS watchdog；
- 修改 Windows Bridge 或键盘固件。
