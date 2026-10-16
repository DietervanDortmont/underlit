# scripts/

Local dev helpers so you don't have to push a tag and wait 7 minutes for CI.

## One-time setup

Open File Explorer, navigate to this folder, and **double-click** `setup-shortcuts.cmd`.

This drops three shortcuts on your desktop:

| Shortcut | Cycle time | When to use |
|---|---|---|
| **Underlit dev (run)** | ~10–20 s | Code change → see it run. The 99% loop. |
| **Underlit dev (install)** | ~1–2 min | Testing installer behaviour, autostart paths, uninstall flow. |
| **Underlit dev (uninstall)** | ~5 s | Wipe the installed copy. |

After that, just double-click the desktop shortcuts whenever you want to test.
No terminal, no execution-policy hassle — the .cmd wrappers handle that.

## What's in this folder

- `dev-run.cmd` / `dev-install.cmd` / `dev-uninstall.cmd` — the double-clickable wrappers. They call the `.ps1` scripts via `powershell -ExecutionPolicy Bypass`, so they work regardless of your system's policy.
- `run.ps1` / `install-local.ps1` — the actual logic. You can also run these from a terminal if you prefer.
- `setup-shortcuts.cmd` / `setup-shortcuts.ps1` — the one-time desktop-shortcut creator.

## Sharing on GitHub later

When you're happy with a build and want to release it, the existing CI workflow takes over. Just:

1. Commit & push your changes via GitHub Desktop (regular branch commits — these don't trigger CI).
2. When ready to release, create a tag like `v0.6.51` (in GitHub Desktop: Repository → Create new tag, or via the GitHub web UI: Releases → Draft a new release).
3. Push the tag. The `.github/workflows/release.yml` workflow notices the `v*` tag, builds the installer, and attaches it to a new GitHub Release automatically.

Plain commits without a `v*` tag never trigger CI, so you can push as often as you want during development without waiting for builds.

## Why does CI take so long?

`release.yml` runs on a fresh Windows runner that has to install the .NET 8 SDK and Inno Setup from scratch every time. That's the 7 minutes. `dev-install.cmd` skips the runner spin-up and the GitHub upload step — everything else is identical.

## Testing autostart

For most code changes, **dev (run)** is fine. The dev build now intentionally does NOT touch the autostart registry entry — it leaves whatever your installed copy registered alone. So you can iterate freely on the dev build without breaking autostart for your real install.

If you want to test autostart end-to-end (i.e. "Underlit launches when I log in"), use **dev (install)** to deploy a fresh installed copy, then sign out and back in.

## .NET Hot Reload (for non-XAML changes)

```pwsh
dotnet watch --project src/Underlit run
```

Save a `.cs` file and the running process gets patched in place — no restart at all. XAML changes still need a full re-launch though, so for this codebase the `dev (run)` shortcut is usually the better default.
