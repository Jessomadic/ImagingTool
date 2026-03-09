[![Build & Test](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml)

# ImagingTool

A Windows system imaging tool built around [wimlib](https://wimlib.net/). Takes full VSS snapshots of your system drive, stores them as compressed WIM files, and can restore them back onto a drive. Also has a registry/data migration mode for moving to a fresh install without a full restore.

Requires administrator privileges to run. Will prompt for UAC elevation automatically if you forget.

---

## What actually works

### Backup
The main thing this tool does. Takes a VSS (Volume Shadow Copy) snapshot of your C: drive and captures it into a `.wim` file using wimlib.

- Uses **LZMS solid compression** by default — good compression ratio, especially worth it when writing to a network share since less data = faster transfer
- Exclusion list covers the usual junk: temp files, crash dumps, browser caches (Chrome/Edge/Brave/Firefox), Teams/Slack/Discord/Spotify caches, Windows Update downloads, OneDrive cloud-only stubs, npm/pip/NuGet package caches, Steam download folders, and more
- Progress displays natively in the terminal as wimlib runs (percent complete, GiB processed)
- Every 15 seconds a supplemental stat line shows disk read/write speeds and current WIM file size
- WIM is written with `--check` so integrity data is embedded and can be verified later
- Dirty bit check on the source volume before starting — warns you if chkdsk should be run first

### Restore
Applies a WIM image to a target drive and configures the bootloader. **You cannot restore to the drive Windows is currently running from** — that's intentional, not a bug. You'd need to boot from WinPE or recovery media to restore the active OS partition.

- Restores all files including registry hives, then runs `bcdboot /f ALL` for both UEFI and BIOS firmware compatibility
- Error output from wimlib is now visible in the terminal (red for errors, yellow for warnings) — if individual files fail during apply you'll see exactly what
- If registry hive files fail to restore, the tool tells you to run Extract (option 4) to re-import the registry from the WIM

**Tested:** The restore has been used on a real system. Works. The main thing to know is that the first time you run it on a fresh backup, boot configuration might need a manual `bcdboot` pass if the target drive letter changes after reboot.

### Verify
Checks the integrity of a WIM file using wimlib's built-in verification. Quick sanity check that your backup isn't corrupted.

Note: WIMs created before the `--check` flag was added (i.e., older backups) won't have integrity data and verify will say so. That's expected — it's not corruption, just missing checksums. Re-run a new backup to get a verifiable one.

### Extract (migrate data to a clean install)
This mode pulls your programs, user data, and app registry out of a WIM and drops them onto an existing Windows installation. The idea is: do a clean Windows install, then run Extract to bring everything back without a full restore.

What it copies:
- `\Users` — all user profiles and AppData
- `\Program Files`, `\Program Files (x86)`, `\ProgramData`
- Registry — third-party vendor keys from `HKLM\SOFTWARE`, selected Microsoft app-compat keys (`Uninstall`, `App Paths`, `SharedDLLs`, `Fonts`), and WOW6432Node third-party keys

**Honest caveat:** This isn't magic. Programs that are just "file dumps" (Notepad++, most portable apps, a lot of games) will work fine. Programs that rely heavily on COM registration, kernel drivers, or custom installer state (some antivirus, VPN clients, Adobe apps, etc.) will probably need to be reinstalled. But at least your settings and data will carry over, and the Uninstall list will be populated.

**Not heavily battle-tested.** The logic is solid but this is newer code that hasn't seen as many real-world runs as backup/restore.

---

## What's not implemented yet

- **Incremental / differential backups** — every backup is a full capture right now. wimlib supports append with delta references (`--update-of`) so this is doable, just not built yet
- **Scheduled backups** — no built-in scheduler; use Task Scheduler manually if you want recurring backups
- **Multi-image WIM management** — no UI for listing, deleting, or selecting images within a multi-image WIM

---

## Setup

No installer. Download the latest release zip, extract it anywhere, run `ImagingTool.exe`. On first run it will download wimlib automatically if it's not already in the `wimlib\` subfolder next to the exe.

Requirements:
- Windows 10/11 or Windows Server 2019+ (x64)
- .NET 9.0 Runtime ([download](https://dotnet.microsoft.com/en-us/download/dotnet/9.0))
- Internet connection on first run (wimlib download)
- Admin rights

---

## Configuration

`appsettings.json` next to the exe. The important setting:

```json
"WimCompressionLevel": "Maximum"
```

| Value | What it does |
|-------|-------------|
| `Maximum` | LZMS solid compression — best ratio, recommended for network destinations or if disk space matters |
| `Fast` | XPRESS solid — faster CPU, still decent compression |
| `None` | No compression — fastest possible write, largest file |

The updater (`Update.ps1`) deliberately skips `appsettings.json` when installing a new version so your settings aren't overwritten.

---

## CLI / non-interactive mode

You can drive it without the interactive menu:

```powershell
# Backup
ImagingTool.exe -dest="D:\Backup.wim"

# Restore
ImagingTool.exe -source="D:\Backup.wim" -target="E:"

# Verify
ImagingTool.exe -verify="D:\Backup.wim"

# Extract
ImagingTool.exe -extractsrc="D:\Backup.wim" -extractdst="C:\"
```

---

## Building from source

```bash
dotnet restore ImagingTool.sln --source https://api.nuget.org/v3/index.json
dotnet build ImagingTool.sln -c Release
dotnet test ImagingTool.sln
```

Targets `net9.0-windows` (x64). Windows only — uses VSS, WinForms dialogs, and Win32 APIs.
