# Fast Steam Tools

FST is a Windows/Linux tray app that adds an **Add to library for free** button to Steam store pages. It downloads available Lua/manifest archives using your Ryuu account, validates and installs them for a compatible loader, then optionally restarts Steam.

[Download v0.1.0](https://github.com/superhe11/FST/releases/tag/v0.1.0) · [Releases](https://github.com/superhe11/FST/releases) · [Русский](README.md)

## Downloads

- Windows x64: **FST-Setup.exe**.
- Arch x86_64: **fast-steam-tools-bin-0.1.0-1-x86_64.pkg.tar.zst**.
- Linux per-user installer: **fst-0.1.0-linux-x86_64.tar.gz**.
- PKGBUILD and SHA256SUMS.txt are included in the release.

The .NET runtime is bundled. Game files, manifests, account credentials and Steam loader binaries are not included.

## Windows installation

Requires Windows 10/11 x64, Steam and an existing compatible Lua loader, such as [OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool). FST does not install the loader.

1. Run FST-Setup.exe.
2. Choose whether to start FST with Windows and click Install.
3. Setup installs to `%LOCALAPPDATA%\FST\bin` and restarts detected Steam.
4. Click **Done** to open FST.
5. Connect Ryuu and open a game page in the Steam store.

Administrator privileges are not required. Use Folders if Steam was not detected. Click the tray icon to open the menu.

## Arch Linux installation

**Experimental:** targets native pacman/AUR Steam on x86_64. Flatpak and Windows Steam under Wine are not supported by this build.

First install **SLSsteam + lumalinux**, following the [upstream desktop setup](https://github.com/jayool/lumalinux#manual-desktop-no-plugin). FST does not install these components automatically. Both are required for the native download backend.

Download the package and run:

```bash
sudo pacman -U ./fast-steam-tools-bin-0.1.0-1-x86_64.pkg.tar.zst
fst
```

A running, unlocked Secret Service keyring is required to save Ryuu sign-in. `libsecret` supplies secret-tool. Wayland needs XWayland; GNOME may need an AppIndicator/StatusNotifier extension for the tray icon.

Alternatively:

```bash
tar -xzf fst-0.1.0-linux-x86_64.tar.gz
cd fst-linux-x64
bash install.sh
```

The script asks about startup and opens FST after the final Enter. See [Linux installation details](packaging/linux/README-LINUX.md).

Use Folders to select the Steam directory, SLSsteam.so and liblumalinux.so if automatic detection failed. Restart Steam through FST once to enable CEF debugging and load the installed components.

## Ryuu sign-in

FST first restores the key from the system keyring, then tries Ryuu's session-discovery API. If no session is found, it opens Discord sign-in in your browser and polls for completion. The key is saved for future launches; there is no manual token-copying workflow.

Windows uses Credential Manager (`FST/Ryuu`). Linux uses Secret Service attributes `application=fst`, `service=ryuu`. FST does not read Discord passwords or browser cookies. Different browser/app VPN routes may prevent session discovery.

Disconnect removes the local saved key. It does not revoke a server-side Ryuu session; Ryuu may return that session when you connect again. Service limits and file availability are determined by Ryuu.

## Features

- Steam store integration, with a Denuvo label only when declared by the store page.
- RU/EN interface and logs.
- Tray menu and optional startup.
- Added Lua list with AppID/name search and multi-selection removal.
- Archive validation, backups and rollback on write errors.
- Linux: SLSsteam library registration and lumalinux depot-key handoff.

FST parses supported declarative Lua statements as data; it never executes downloaded Lua. Removing Lua does not delete installed game files. Game launch or DRM compatibility is not guaranteed.

## Uninstall

Windows: use **Uninstall FST** in the app. Removing added Lua is a separate, initially unchecked option.

Arch package:

```bash
sudo pacman -R fast-steam-tools-bin
```

Per-user Linux installation: exit FST and run `bash uninstall.sh` from the extracted archive. The script preserves Lua and recovery backups; the in-app screen additionally offers Lua removal. Separately installed Steam loaders are not removed.

## Build and test

Requires .NET 9 SDK. From the repository root:

```powershell
# Windows + setup executable
powershell -NoProfile -File tools/Build-Setup.ps1
```

```bash
# Linux archive + Arch package
bash tools/Build-Linux.sh

# Offline core checks
dotnet run --project tests/Fst.Tests/Fst.Tests.csproj -c Release

# Isolated Linux backend/package checks
./release/fst-linux-x64/app/fst --self-test
bash tools/Test-Linux-Package.sh
```

Outputs are in `release/`. The Linux packager also requires tar, zstd, sha256sum and standard shell tools.

v0.1.0 passed 37 offline core checks and 32 Linux checks locally. Windows compilation, RU/EN Linux GUI rendering, package contents and checksums were verified. **Linux testing used WSL, not an actual Arch Steam client. Game downloading/launching on Arch has not been verified.** Live test modes are opt-in and may consume Ryuu quota.

For troubleshooting, open Logs and check Steam paths, loader installation/compatibility and the local CEF endpoint on `127.0.0.1:8080`. Report OS, FST version, Steam installation type and the error in [Issues](https://github.com/superhe11/FST/issues); do not upload keys, cookies or credential files.

Dependency notices: [packaging/linux/LICENSES](packaging/linux/LICENSES).
