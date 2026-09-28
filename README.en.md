# dsh-desktop

[中文](README.md) · English

![Desktop shell window](assets/dsh-desktop-window.png)
![Tray menu](assets/dsh-desktop-tray.png)

*Screenshots of the running DSH desktop shell and its tray menu.*

A Windows desktop shell and system-tray guard for DSH: two single-file C# programs, open-sourced at source level.

| Program | Role |
|---|---|
| `apps/dsh-desktop/App.cs` | WebView2 window shell: hosts the DSH web UI in a native window, launches the engine, resolves the launch token, internalises the recharge / usage / API-key pages, confirms on close |
| `dsh-tray/dsh-tray.cs` | System-tray guard: self-drawn dark menu, status light, start/stop for the 3080 engine and the 3081 phone reverse proxy, with "Quit DSH" as the only exit |

Boot splash: [`dsh-boot-splash`](https://github.com/Ln1m/dsh-boot-splash) — a full-frame intro layer that plays while the window is up but the page has not painted yet (frosted-glass Skip pill, clip library).

## Layout convention

Both programs build paths from the DSH install root (default `%USERPROFILE%\DeepSeek_harness`), overridable by environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `DSH_ROOT` | `%USERPROFILE%\DeepSeek_harness` | Install root; `node_modules\.bin\dsh.cmd`, `dsh-tray\`, `apps\dsh-desktop\`, `logs\` and `assets\` are derived from it |
| `DSH_NODE` | `%ProgramFiles%\nodejs\node.exe` | Node the tray uses to launch the engine |
| `DSH_TRAY_EXE` | `<DSH_ROOT>\dsh-tray\DSH-Tray.exe` | Path the desktop shell uses to make sure the tray is running |
| `DSH_ICON` | none | Window icon for the desktop shell (omit for no icon) |

The repo layout is the install-root layout: drop `apps/` and `dsh-tray/` into your DSH install root.

## Build

Requires the csc shipped with .NET Framework (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) and the WebView2 runtime.

```powershell
# Desktop shell (produces dsh-desktop-new.exe by default, for hot-swapping via apply-new.ps1)
powershell -File .\apps\dsh-desktop\build.ps1

# Tray (produces dsh-tray\build\DSH-Tray.exe)
powershell -File .\dsh-tray\build.ps1
```

`apps/dsh-desktop/` ships the three WebView2 SDK DLLs (Microsoft redistributables): `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll`.

## Icons and brand art

The build takes the window icon from `DSH_ICON`; the repo ships a self-made default at `assets/deepseek_harness.ico` (a blue "DS" tile, not official brand art) — omit it and the shell simply has no window icon. The tray brand image is read from `<DSH_ROOT>\assets\` and falls back to a drawn placeholder.

## Requirements

- Windows, .NET Framework 4.x
- WebView2 runtime installed
- `dsh` installed at `<DSH_ROOT>` (so that `node_modules\.bin\dsh.cmd` exists)
