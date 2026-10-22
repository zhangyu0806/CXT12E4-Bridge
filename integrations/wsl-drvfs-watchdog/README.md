# WSL DrvFS watchdog

This integration recovers broken Windows-drive mounts such as `/mnt/c` and
`/mnt/d` without stopping WSL or the tmux server. It is intended for the WSL
failure mode where the Linux filesystem remains healthy but an existing DrvFS
9P channel returns `Input/output error`.

Safety rules:

- A fresh DrvFS mount must pass validation before the old mount is touched.
- The watchdog uses an ordinary unmount only. If a mount is busy, it logs the
  condition and leaves it in place; it never uses forced or lazy unmounting.
- It never terminates a distro, shuts down WSL, or kills tmux/processes.
- It checks that the target filesystem is DrvFS-compatible (`9p`, `drvfs`, or
  `virtiofs`) rather than treating an empty mount directory as healthy.

Install in Ubuntu:

```bash
sudo install -m 0755 wsl-drvfs-watchdog /usr/local/sbin/wsl-drvfs-watchdog
sudo install -m 0644 wsl-drvfs-watchdog.service /etc/systemd/system/
sudo install -m 0644 wsl-drvfs-watchdog.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now wsl-drvfs-watchdog.timer
```

The defaults monitor C: and D: every 30 seconds and mount them for UID/GID
1000. Override `WSL_DRVFS_DRIVES`, `WSL_DRVFS_UID`, or `WSL_DRVFS_GID` with a
systemd service environment override if the local layout differs.

Useful checks:

```bash
systemctl status wsl-drvfs-watchdog.timer
journalctl -u wsl-drvfs-watchdog.service
findmnt -T /mnt/c
```
