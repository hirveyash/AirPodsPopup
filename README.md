# AirPods Popup for Windows

A tiny tray app that shows a Mac-style popup (model, battery, **Connect** button) when you open your AirPods case or wake them near your PC.

- Free, MIT licensed, no server, no telemetry.
- Single `.exe`, runs in the tray. Right-click the tray icon for "Start with Windows" and "Exit".

## Use
1. Pair your AirPods with Windows once (Settings > Bluetooth & devices).
2. Download `AirPodsPopup.exe` from [Releases](../../releases) and run it.
3. Open the case lid near your PC. The popup appears; click **Connect**.

Windows SmartScreen may warn about an unknown publisher because the exe is not code-signed. Choose "More info" > "Run anyway", or build it yourself.

## Build
`dotnet publish -c Release` (needs the .NET 8 SDK). Parser self-check: `AirPodsPopup.exe --selftest` (exit code 0 = ok).

## Limits
- Reads Apple's undocumented Bluetooth LE advertisement; battery values are approximate (10% steps) and left/right order is best-effort.
- Only pops up for AirPods within roughly 1-2 m (signal threshold -70 dBm).
- No iCloud-style auto-switching between devices. Windows has no public "connect" API, so Connect toggles the paired device's audio services.
- Needs a Bluetooth adapter that supports LE scanning.
