**English** | [简体中文](README.zh-CN.md)

# GammaPanel X

A modern remake of the classic [Gamma Panel](https://gammapanel.en.softonic.com/), with one key improvement: **every monitor can be selected and adjusted independently** (the original only affected the primary display).

Portable single-file app — no installer, no .NET SDK or runtime downloads (uses the .NET Framework 4.x that ships with Windows).

> Note: the UI is currently in Simplified Chinese.

## Download

Grab `GammaPanelX.exe` from the [Releases](../../releases) page and double-click to run.

## Features

| Feature | Description |
|---------|-------------|
| Software adjustment (gamma LUT) | Brightness / contrast / gamma written to the GPU's per-monitor lookup table, applied instantly |
| Independent RGB channels | Like the original: adjust channels linked, or tune red/green/blue separately (handy for quick color-temperature tweaks) |
| Hardware adjustment (DDC/CI) | Drives the monitor panel's own brightness / contrast / **saturation** (requires DDC/CI support) |
| Per-monitor control | Pick a target monitor from the list; each monitor's settings are saved independently. An optional "sync all monitors" mode is available |
| Profiles + global hotkeys | Save multiple full setups (e.g. Day / Night / Gaming) and switch with a single hotkey |
| Curve preview | Live plot of the current gamma curves |
| Tray app | Close button minimizes to tray; switch profiles straight from the tray menu |
| Reset protection | Optional periodic re-apply to survive games or drivers resetting colors; auto-restores after sleep/resume, unlock, and resolution changes |
| Run at startup | One checkbox; last settings are restored automatically on launch |

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# Output: build\GammaPanelX.exe
```

No .NET SDK required — it compiles with the `csc.exe` bundled with Windows (which is why the source sticks to C# 5 syntax).

## Tips

- **Identify monitors**: the "identify" button flashes a number in the center of each screen, matching the DISPLAY numbers in the list.
- **Why is saturation under "hardware adjustment"?** A gamma LUT is a per-channel mapping and physically cannot change saturation (that requires cross-channel mixing). So saturation is written to the monitor hardware over DDC/CI — equivalent to changing it in the monitor's OSD menu. Unsupported monitors show a notice (built-in laptop panels usually don't support it).
- **Extreme values do nothing?** Windows clamps gamma LUT ranges by default (to prevent an all-black/all-white screen). Click the "unlock Windows gamma range limit" link in the app (writes `GdiIcmGammaRange=256` to the registry; requires admin, takes effect after sign-out).
- **Windows Night Light / f.lux**: both write gamma LUTs, so they override each other. On first run (all settings at defaults) this app does not touch the LUT, so Night Light keeps working; once you adjust a monitor, this app owns that monitor's LUT.
- Settings live in `%APPDATA%\GammaPanelX\settings.json`, keyed by monitor hardware ID — swapping ports or replugging won't mix up your configs.
- On exit, the tray menu lets you either keep the current colors or restore defaults.

## Technical notes

- Per-monitor gamma: `CreateDC("\\.\DISPLAYn")` + `SetDeviceGammaRamp` (gdi32) — each output gets its own LUT.
- DDC/CI: `dxva2.dll` Monitor Configuration API, VCP codes 0x10 (brightness) / 0x12 (contrast) / 0x8A (saturation).
- Formula (same family as the original): `v = ((i/255)^(1/γ) − 0.5) × (contrast+100)/100 + 0.5 + brightness/200`, generating a 256-entry LUT per channel.
- Baseline protection: on startup the current LUT of each monitor is captured as a baseline and adjustments are composed on top of it — "reset" restores the system's ICC/vcgt calibration exactly instead of writing a linear ramp.

## License

[MIT](LICENSE)
