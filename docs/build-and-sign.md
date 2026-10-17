# Build, sign, and install

Install, rollback, and safety steps are in the [README](../README.md#install-test-signed-driver). This page covers building.

## Everything (installer)
```
build-installer.cmd
```
This produces `artifacts\installer\MagicKeyboard-Setup-<version>.exe`. The first run downloads the win-x64 runtime packs from nuget.org.

## Managed apps
```powershell
dotnet build MagicKeyboard.sln -c Release
```

## Driver
Prerequisites: VS 2022 Build Tools (C++ toolset, Windows 11 SDK 26100, Spectre libs) and WDK 10.0.26100.
```
driver\MagicKeyboardFilter\build.cmd
```
- **Compiler:** it compiles `Driver.c` and `Remap.c` with cl/link directly at `/W4 /WX`.
  - The only suppressed warning is C4324, which comes from the KMDF headers.
- **Packaging:** it stamps the INF, runs Inf2Cat, and signs the `.sys` and the `.cat` with "MagicKeyboard Test".
  - build.cmd creates that certificate once in CurrentUser\My and exports it to `out\MagicKeyboardTest.cer`.
  - The installer trusts it on install and removes it on uninstall.
  - It's separate from MagicMouse's certificate, so the two apps never untrust each other's driver.
- **Full Visual Studio:** if you have it with the WDK extension, `MagicKeyboardFilter.vcxproj` builds the same sources.

## Engine tests
```
driver\MagicKeyboardFilter\tests\run-tests.cmd
```
This builds `Remap.c` together with `tests\remap_test.c` as a user-mode exe. The tests cover:
- Fn layer;
- sticky mappings;
- media events with and without the app;
- Fn lock;
- chords;
- presets;
- swaps;
- Caps remap;
- sleep shortcut;
- pass-through cases.

## Production signing
Loading on machines without test signing needs an EV code-signing certificate and Microsoft attestation signing through Partner Center.
