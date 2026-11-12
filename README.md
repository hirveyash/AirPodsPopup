# AirPods Popup for Windows

A tiny tray app that shows a Mac-style popup (model, battery, **Connect** button) when you open your AirPods case or wake them near your PC.

- Free, MIT licensed, no server, no telemetry.
- Single `.exe`, runs in the tray. Right-click the tray icon for "Start with Windows" and "Exit".

## Download and use
Needs Windows 10 (version 1809 or newer) or Windows 11 with Bluetooth. No install and no .NET required.

1. **Pair your AirPods with Windows once.** Open the AirPods case, hold the button on the back until the light flashes white, then go to Settings > Bluetooth & devices > Add device > Bluetooth and pick your AirPods.
2. **Download the app.** Open the [Releases page](../../releases), and under the latest release download `AirPodsPopup.exe`. Save it somewhere permanent, such as `C:\Tools\`.
3. **Run it.** Double-click `AirPodsPopup.exe`. Nothing opens; the app sits in the system tray (click the `^` arrow by the clock if you don't see it).
   - If Windows shows "Windows protected your PC", click **More info** > **Run anyway**. This appears because the exe is not code-signed.
4. **Use it.** Close the case for about 20 seconds, then open the lid close to your PC (within roughly 30-50 cm). A popup appears with the model and battery levels. Click **Connect**. The button reads **Connected** once you're connected.
5. **Disconnect.** Put the AirPods back in the case and Windows shows a notification that they disconnected.
6. **Optional.** Right-click the tray icon and tick **Start with Windows** so it runs on every login. Choose **Exit** to quit.

**Not working?** Make sure the AirPods are paired in Windows (step 1), Bluetooth is on, and the case is close to the PC. Run `AirPodsPopup.exe --debug` to log what the app hears to `%TEMP%\airpods-debug.log`.

## Build
`dotnet publish -c Release` (needs the .NET 8 SDK). Parser self-check: `AirPodsPopup.exe --selftest` (exit code 0 = ok).

## Limits
- Reads Apple's undocumented Bluetooth LE advertisement; battery values are approximate (10% steps) and left/right order is best-effort.
- Only pops up when the case is very close (signal threshold -50 dBm, roughly 30-50 cm), so neighbours' AirPods are ignored. It identifies "your" AirPods by signal strength alone.
- No iCloud-style auto-switching between devices. Windows has no public "connect" API, so Connect toggles the paired device's audio services.
- Needs a Bluetooth adapter that supports LE scanning.
