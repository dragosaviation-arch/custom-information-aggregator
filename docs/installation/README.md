# CIA v1 Installation Guide

This guide covers installing, starting, upgrading and uninstalling Custom Information Aggregator (CIA) v1.

## Supported environment

- Primary baseline: Windows 11 x64.
- Compatibility target: Windows 10 x64. Physical product-owner verification on Windows 10 is pending.
- Architecture: x64 only for the current release.

## Prerequisites

CIA is distributed as a self-contained .NET application. The installer includes the required .NET runtime components, so no separate .NET installation or other non-bundled .NET prerequisite is required.

## Install CIA

1. Download the CIA installer from the [official GitHub Releases page](https://github.com/dragosaviation-arch/custom-information-aggregator/releases).
2. Launch the downloaded installer.
3. Follow the graphical installation flow. Administrator elevation is not required.
4. Optionally select the Desktop shortcut component.
5. Finish the installation and launch CIA if desired.

CIA is installed per user under `%LOCALAPPDATA%\Programs\Custom Information Aggregator`. The installer creates a Start Menu shortcut, creates the Desktop shortcut only when selected, and registers the `.cia` file association for the current user. Only `CIA.exe` is user-facing.

## Unsigned v1.0.0 release

CIA v1.0.0 is intentionally unsigned. Windows may display a Microsoft Defender SmartScreen or unknown-publisher warning; such a warning is expected for an unsigned release but may not appear on every system.

Download the installer only from the official CIA GitHub Releases page. If Windows permits installation after showing SmartScreen, select **More info**, verify that the installer is the file downloaded from the official release, and then select **Run anyway**. Do not disable Windows security features.

## First launch

Launch CIA from any of these supported entry points:

- the installer finish page;
- the Start Menu shortcut;
- the optional Desktop shortcut;
- an associated `.cia` working-state file.

CIA does not install a background service, startup application or permanent background process.

## Upgrade

Download the newer installer from the official GitHub Releases page and run it over the existing per-user installation. The installer replaces or updates the installed application binaries and bundled runtime components. CIA-managed mutable data and normal user-owned files and exports are preserved. CIA has no automatic updater.

## Uninstall

1. Open Windows **Settings**.
2. Go to **Apps > Installed apps**.
3. Find **Custom Information Aggregator** and select **Uninstall**.
4. Complete the graphical per-user uninstaller.

The uninstaller removes application binaries and Windows shell integration. By default, CIA-managed application data is preserved. To remove the managed settings, profiles, logs, working data, databases and temporary data under the CIA-managed application-data location, explicitly select **Remove CIA managed application data** in the uninstaller.

CIA does not search for or delete arbitrary user-created `.cia` files, exports, or persistent extracted content stored outside the managed application-data location.

## Installation troubleshooting

- **SmartScreen or unknown publisher:** Follow the unsigned-release guidance above and use only the installer from the official GitHub Releases page.
- **Installer will not start:** Confirm that the download completed, that the installer came from the official release, and that the current Windows account can run per-user applications. Download the installer again if the file is incomplete.
- **Start Menu shortcut is not immediately visible:** Search for **Custom Information Aggregator** again or sign out and back in to refresh the Windows shell.
- **Reinstall or upgrade:** Run the desired CIA installer again. CIA-managed mutable data is preserved by default.
- **Uninstall entry is missing:** Re-run the installer to restore the per-user installation registration, then use **Settings > Apps > Installed apps**.
- **Report a problem:** See [Support](../../SUPPORT.md).
