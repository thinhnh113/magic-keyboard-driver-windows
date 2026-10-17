# REA analysis of Magic Keyboard Utilities v3.1.5.3

Target: `C:\Program Files\MagicUtilities` (the installed, licensed product).
Method: REA static inspection of the shipped binaries + the vendor's published
feature documentation. No runtime instrumentation of the shipped product was
performed, and none of its protected code was unpacked.

## Binaries

| File | Role | Observations |
|------|------|--------------|
| `DriverKeyboard\MagicKeyboard.sys` (5.6 MB) | KMDF **HID lower-filter** driver (KMDF 1.15). | PE x64, normal sections (`.text/.rdata/.data/.pdata/INIT/.rsrc/.reloc`). PDB path `C:\Kunden\Products3\Driver\_out\Release\x64\MagicKeyboard.pdb`. Strings show device support for Magic Mouse 1/2/3, Magic Trackpad 1/2/3, Magic Keyboard 1/2/3, Apple Wireless Keyboard; a raw PDO `{B28812FC-53E1-4035-9EE6-190ED0EF6025}\MagicKeyboardRaePdo`; license crypto (`\Registry\Machine\Software\MagicUtilities\Driver`, `MachineGuid`, `BCryptGenerateSymmetricKey`, AES CBC). Imports `HidP_*`, `WdfVersionBind`. Registered as `LowerFilters = MagicKeyboard` on the HID device. |
| `MagicKeyboardUtilities.exe` (16 MB) | Qt6 GUI / tray settings app. | **Protected with a WinLicense/Themida-class packer**: sections `.winlice`, `.boot`, `.vm_sec`, plus a Win32 manifest ("macOS magic for Windows", `uiAccess="true"`). Qt6 referenced. Internal logic is virtualized → not decompilable, and not needed: behaviour is fully documented publicly. |
| `Service\MagicUtilities_Service.exe` (3.4 MB) | Background Windows service. | Same protector (`.winlice/.boot/.vm_sec`). Owns privileged driver communication + licensing in the real product. |
| `Service\BluetoothPairing.exe` (3.2 MB) | Bluetooth pairing helper. | **.NET** assembly (CommandLineParser / `CSharpx`, `System.Security.Cryptography` types). Not protected. |
| `Service\DriverUnInstaller.exe` | Driver uninstall helper. | — |

## Architecture (as shipped)

```
 Apple Magic Keyboard (USB MI_01 / Bluetooth BTHENUM)
        │  HID input reports
        ▼
 MagicKeyboard.sys  ── LowerFilter under HIDClass, rewrites reports,
        │               exposes raw PDO for the service, checks license
        ▼
 kbdhid / HID stack → Windows input
        ▲
 MagicUtilities_Service.exe ── pushes settings to the driver
        ▲
 MagicKeyboardUtilities.exe (Qt6 tray GUI) ── user edits settings
 BluetoothPairing.exe ── pairs/manages BT devices
```

Hardware binding (from `MagicKeyboard.inf`): matches Apple VID `05AC` keyboards
by USB interface `MI_01` and by Bluetooth `BTHENUM\...VID&0001004c_PID&...`,
covering Magic Keyboard 2015 → 2024, Touch ID, Numeric Keypad, and the Apple
Wireless Keyboard.

## Documented feature set (reproduced in the rebuild)

- **Fn lock**: top row = brightness/media/volume by default; Fn reaches F1–F19.
- **Function row** F1..F19 defaults: brightness ×2, Task View, Search, Dictation,
  Sleep (F4–F6 on 2021 models), prev/play/next, mute/vol−/vol+, Print Screen,
  Scroll Lock, Pause/Break, and launchers (Explorer/Calc/Notepad/Task Manager).
- **Missing keys via Fn**: Fn+Return=Insert, Fn+Delete=Forward Delete,
  Fn+Arrows=Home/End/PageUp/PageDown, Fn+Esc=Pause/Break, plus Print Screen and
  Ctrl+Alt+Del combinations.
- **Modifier remapping** for Shift/Ctrl/Win/Alt/Fn/CapsLock/Menu with
  **Windows / macOS / Apple presets** and **Command↔Option swap**.
- **Clear key** → Num Lock / Delete / Backspace / Off.
- **Revive Eject** → Delete / Insert behaviour (small vs. numeric boards).
- **Third-character (AltGr)** modifier.
- **Low-battery alerts** for Bluetooth and USB devices; **seamless USB** charging.
- **Bluetooth pairing**, high-DPI tray UI, start-with-Windows.

## Device findings (this PC, 2026-10-17)

The keyboard is a **Magic Keyboard with Touch ID 2024**, PID `0x0321`, connected over Bluetooth. Its HID descriptor comes from the BTHPORT SDP cache, attribute 0x0206, 227 bytes.

**Report 0x01, keyboard (10 bytes):**
- `[1]` modifier bits;
- `[2]` reserved;
- `[3..8]` six key usages;
- `[9]` bit 1 is Fn (vendor page 0xFF01, usage 0x03);
- `[9]` bit 2 is consumer usage 0x40, which is probably the Touch ID / lock key (unconfirmed).
- **The top row always sends F1–F12.** The firmware never sends media usages.

**Other reports:**
- **Report 0x55** is a 64-byte vendor feature report (page 0xFF02).
- **Report 0x90** carries battery state in its own top-level collection (0xFF00:0x14). It's the same format as the Magic Mouse.
- **Reports 0x20–0x22** are in a vendor top-level collection (0xFF00:0x4B), probably used for Touch ID.

**What the vendor driver changes:** with it installed, Device Manager shows "HID-compliant consumer control device" (COL02) and "system controller" (COL03) entries for this keyboard. Neither collection is in the stock descriptor. So the vendor driver rewrites the descriptor to emit media and sleep keys natively. This rebuild injects those keys from user mode instead (see README).

## What was deliberately NOT done

The shipped GUI/service protection (WinLicense/Themida) was not defeated or
unpacked, and the license-check logic was not analysed or reproduced. The
rebuild is a clean-room implementation from documented behaviour and the HID
specification, not a copy of the vendor's code.
