# MagicKeyboard

A clean-room Windows utility for Apple Magic Keyboards, built to replace Magic Keyboard Utilities 3.1.x.
It follows the same structure as the MagicMouse project in `..\magic_mouse`. See
[`docs/REA-findings.md`](docs/REA-findings.md) for what was learned from the installed app and the device.
No vendor code is used, and the vendor's protected binaries were not unpacked.

## Layout
| Path | What |
|---|---|
| `driver/MagicKeyboardFilter/` | KMDF lower filter (C) below hidbth/hidusb. It rewrites keyboard reports in place and hands media keys to the app |
| `driver/MagicKeyboardFilter/tests/` | Host unit tests for the remap engine (`run-tests.cmd`) |
| `shared/MagicKeyboard.Shared/` | Driver protocol mirror, driver client, settings model and presets |
| `gui/MagicKeyboard/` | Tray app `MagicKeyboard.exe`: settings window, media/brightness/launcher actions, battery |
| `tools/KeyProbe/` | Diagnostics: `KeyProbe` (status), `KeyProbe raw` (decoded live reports), `KeyProbe battery` |
| `installer/` | Inno Setup script and the driver install/uninstall scripts it runs (same structure as MagicMouse) |
| `tools/icon/` | `make-icon.ps1` renders the app icon (same family as MagicMouse) and `preview.png` |

## How it works
- **In the driver:** these work without the app, from boot, including the lock screen:
  - modifier remapping, Windows/macOS/Apple presets, and Command/Option swap;
  - the AltGr key, Caps Lock remapping, and the Clear key;
  - Fn lock;
  - Fn+Return/Delete/arrows/Esc/P for Insert, Delete, Home/End/PgUp/PgDn, Pause and Print Screen;
  - Task View, Search and Dictation chords (Win+Tab, Win+S, Win+H).
  - The driver saves its config in `HKLM\SYSTEM\CurrentControlSet\Services\MagicKeyboardFilter\Parameters`.
- **In the app:**
  - media keys, volume (with the Windows flyout and hold-to-repeat) and rewind/fast-forward;
  - brightness, through WMI for internal panels and DDC/CI for external monitors that support it;
  - Fn+Space sleep and the F16–F19 launchers.
  - If the app isn't running, these keys act as plain F-keys rather than doing nothing.
- **Battery:** the app reads HID report `0x90` from the vendor collection `0xFF00:0x14`, the same way MagicMouse does. It falls back to the battery property Windows' Bluetooth stack publishes.
  - The tray icon shows a colored level bar: red at 10% or below, orange at 25% or below, green above that. The tooltip shows the percentage.
  - Alerts fire once per crossing: low (15%), critical (5%) and charged (80%). The thresholds are configurable on the Battery tab.
- **Key state is tracked per key.** If you release Fn before the arrow key, the key still lifts as Home, so nothing gets stuck.

Supported devices are the Magic Keyboard family (2015, 2017 Numeric, 2021 and 2024, including Touch ID) over Bluetooth and USB.
- **Verified:** the report layout of the 2024 Touch ID model (PID 0321), which comes from its SDP HID descriptor.
- **Other models:** they are expected to share that layout. Reports in any other layout pass through unchanged, and `KeyProbe` flags them.

## Build
- **Installer:** run `build-installer.cmd`. It builds the driver, runs the engine tests, publishes the app and KeyProbe self-contained, and produces `artifacts\installer\MagicKeyboard-Setup-<version>.exe`.
  - It needs the .NET 8 SDK, VS 2022 Build Tools plus WDK 10.0.26100, and Inno Setup 6. That's the same toolchain as MagicMouse.
  - The version comes from `Directory.Build.props`.
- **Driver only:** run `driver\MagicKeyboardFilter\build.cmd`.
  - It test-signs with a **"MagicKeyboard Test"** certificate, created once in CurrentUser\My.
  - This certificate is deliberately separate from MagicMouse's, because each uninstaller removes its own certificate.
- **App only:** `dotnet build MagicKeyboard.sln -c Release`.
- **Engine tests:** run `driver\MagicKeyboardFilter\tests\run-tests.cmd` (20 checks).

## Install
1. **Remove Magic Keyboard Utilities** (Settings > Apps) if it's still installed. Setup warns you if its driver is present.
2. **Run `MagicKeyboard-Setup-<version>.exe`.** It does the following:
   - installs the app to `Program Files\MagicKeyboard`;
   - trusts the test certificate and installs the driver;
   - restarts connected Magic Keyboards so the filter attaches right away;
   - sets up Start with Windows (`--minimized`), which you can turn off in Setup;
   - offers to turn on test-signing mode if it's off. Secure Boot must be off for that, and it takes effect after a restart.
3. **Check it works** with `KeyProbe.exe` in the install folder. Your keyboard should show `layoutKnown=True` after a keypress.

**Updating:** run a newer Setup over the existing install. It closes the app first, because an open handle would delay the driver update until a reboot.

**Uninstalling:** use Settings > Apps > MagicKeyboard. This:
- removes every copy of the driver package and its service, which takes the saved driver config with it;
- removes the test certificate and the startup entry;
- offers to delete your settings in `%APPDATA%\MagicKeyboard`.

The keyboard goes back to the stock Windows driver, and MagicMouse is not affected.

**Development installs:** `driver\MagicKeyboardFilter\install.cmd` and `uninstall.cmd` install the freshly built driver from `out\` without the installer.

**Safety:**
- **Unrecognized reports** go through untouched.
- **If the app exits**, its action keys fall back to F-keys.
- **If the keyboard misbehaves**, uninstall MagicKeyboard using the mouse, or a laptop or USB keyboard. Turning the Magic Keyboard off also unloads the filter.
- **Test with a second input device available.** This is a kernel driver.

## Differences from the original
- **Bluetooth pairing:** the vendor ships a pairing helper. This project leaves pairing to Windows (Settings > Bluetooth & devices), as MagicMouse does.
- **Media keys:** the vendor rewrites the HID descriptor to add consumer-control collections, which shows up in Device Manager as "HID-compliant consumer control device". This project injects media keys from the app instead. As a result, media keys need the app running and don't work on the lock screen.
- **Eject:** not implemented. No supported keyboard has an Eject key, and the old Apple Wireless Keyboard is excluded until its layout is verified.
- **Touch ID key action:** off by default until its report bit is confirmed with `KeyProbe raw`.
- **Elevated windows:** injected media keys may not reach an elevated foreground window, because of how Windows isolates elevated apps.
