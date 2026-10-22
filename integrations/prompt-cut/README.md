# CXT12E4 prompt cut (legacy, manual use)

This older helper is retained for manual use through tmux prefix + uppercase
`Y`; it is no longer assigned to L2 key 9. The current key-9 stash/restore
action uses prefix + `Ctrl+S` and the [agent control helper](../terminal-controls/README.md).
See the [Windows manual](../../docs/cxt12e4-manual/index.html) for current
default mappings; saved per-layer overrides take precedence.

The legacy tmux binding calls
`cxt-prompt-cut-trigger`, which opens the current OpenCode or Claude Code prompt
through the shared external-editor entry point. `cxt-prompt-cut-editor` copies
the complete temporary buffer into the Windows clipboard, truncates it, and
returns immediately. Outside an armed manual request it delegates to Vim.

The marker is isolated by `TMUX_PANE` and expires after three seconds so a
failed or delayed request cannot affect a later ordinary editor invocation.
