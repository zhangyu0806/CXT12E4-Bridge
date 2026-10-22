# CXT12E4 Bridge contributor rules

Read `CONTRIBUTING.md` before changing this project.

- Keep shortcut behavior, keyboard/controller mappings, app-launcher slots, window layouts and related settings consistent with their configuration examples, affected manuals and tests in the same change. This includes the optional external keyboard/window/terminal integrations.
- Manual sources live in `docs/cxt12e4-manual/`. Preserve Windows/Mac separation and the 12E4/controller/shortcuts navigation. Clearly distinguish personal setup examples from Bridge installer defaults.
- Maintainers publish reviewed source and documentation to `zhangyu0806/CXT12E4-Bridge` and update the canonical manual at `https://cxt12e4.11166666.xyz/`. Verify the published manual matches the repository; report any publishing blocker. Contributors without deployment access submit source changes for maintainers to publish.
- Publish portable, sanitized configuration examples only. Exclude credentials, private Git history, personal conversation links, machine identifiers, runtime state, backups, generated binaries and diagnostic snapshots.
- Run checks relevant to the change. Separate isolated tests, programmatic desktop checks and physical keyboard/controller acceptance. Preserve unrelated user settings and active sessions; source or documentation work does not authorize running installers.
