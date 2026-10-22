# CXT12E4 L2 agent controls

The Windows Bridge sends one dedicated tmux prefix chord for L2 key 9:

- prefix + `Ctrl+S`: stash or restore the current OpenCode, Claude Code, or Grok draft;

Install `cxt-l2-agent-action` as `~/.local/bin/cxt-l2-agent-action`, add the
binding from `integrations/tmux/local.conf`, and configure OpenCode's
`prompt_stash` keybind as `ctrl+s`. The extra tmux binding is required because
this installation already owns `Ctrl+S` as its tmux prefix.
