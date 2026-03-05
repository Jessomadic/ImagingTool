# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

<!-- Last updated: 2026-03-05 -->

## Build & Run Commands

```bash
# Restore dependencies (required first time or after csproj changes)
dotnet restore ImagingTool.sln --source https://api.nuget.org/v3/index.json

# Build
dotnet build ImagingTool.sln -c Debug
dotnet build ImagingTool.sln -c Release

# Run (requires administrator privileges)
dotnet run --project ImagingTool.csproj

# CLI arguments (non-interactive modes)
dotnet run --project ImagingTool.csproj -- -dest="D:\Backup.wim"
dotnet run --project ImagingTool.csproj -- -source="D:\Backup.wim" -target="E:"
dotnet run --project ImagingTool.csproj -- -verify="D:\Backup.wim"

# Run all tests
dotnet test ImagingTool.sln

# Run a single test class
dotnet test ImagingTool.Tests/ImagingTool.Tests.csproj --filter "ClassName=ImagingTool.Tests.BackupServiceTests"

# Run a single test by name
dotnet test ImagingTool.Tests/ImagingTool.Tests.csproj --filter "DisplayName~ResolveCompressionLevel"
```

The project targets `net9.0-windows` (x64) and requires Windows to build and run.

> **Note:** The NuGet feed must be configured to include nuget.org. If restore fails with "No packages exist in source(s): Microsoft Visual Studio Offline Packages", use the `--source` flag above.

## Architecture

### Project Structure

```
ImagingTool/
├── Program.cs                  # Entry point — config, admin check, menu dispatch only
├── AppSettings.cs              # Config model; exposes WimlibDir/WimlibPath as computed properties
├── appsettings.json            # URLs, WimLib config, compression level
├── Helpers/
│   ├── IProcessRunner.cs       # Interface for external process execution (mockable in tests)
│   ├── ProcessRunner.cs        # Real implementation using System.Diagnostics.Process
│   ├── DialogHelper.cs         # WinForms save/open dialogs on dedicated STA threads
│   └── VolumeHelper.cs         # IsRunningAsAdministrator, IsVolumeDirty, Truncate
├── Services/
│   ├── RequirementsService.cs  # Checks/downloads .NET runtime and WimLib on startup
│   ├── BackupService.cs        # VSS capture via wimlib, progress parsing, perf counters
│   ├── RestoreService.cs       # WIM apply + bcdboot UEFI boot config
│   └── VerifyService.cs        # WIM integrity check via wimlib verify
└── ImagingTool.Tests/
    ├── BackupServiceTests.cs   # Compression level, error detection, exclusion config
    ├── RestoreServiceTests.cs  # Restore flow with mocked IProcessRunner
    ├── VerifyServiceTests.cs   # Verify flow with mocked IProcessRunner
    └── VolumeHelperTests.cs    # Truncate utility
```

### Key Flow

1. Load `appsettings.json` → `AppSettings`
2. Admin check (`VolumeHelper.IsRunningAsAdministrator`)
3. Menu: **1** Backup, **2** Restore, **3** Verify (or via `-dest=`, `-source=`/`-target=`, `-verify=` CLI args)
4. `RequirementsService.InitializeRequirements()` — checks .NET runtime version, downloads WimLib if missing
5. Dispatch to the appropriate service

### Services

**BackupService** — manages the wimlib `capture` invocation directly (not via `IProcessRunner`) because it needs custom async STDERR progress parsing. Key internals exposed for testing:
- `ResolveCompressionLevel(string?)` → `(arg, display)` tuple; maps `None/Fast/Maximum` config value to wimlib args
- `IsWimlibError(string)` → distinguishes real errors from progress/file lines on STDERR
- `WriteExclusionConfig(string)` → writes the `[ExclusionList]` temp file used by wimlib

**RestoreService / VerifyService** — accept `IProcessRunner` in constructor; use `ProcessRunner` in production and a `Mock<IProcessRunner>` in tests. `ApplyWimImageAndConfigureBoot` is `internal` for direct testing.

**RequirementsService** — WimLib is downloaded from `WimlibDownloadUrl` in `appsettings.json`, extracted from ZIP, and placed at `<AppBaseDir>/wimlib/wimlib-imagex.exe`. The `.NET` runtime check runs `dotnet --list-runtimes` and parses the output.

### Testing Approach

- Pure logic (`ResolveCompressionLevel`, `IsWimlibError`, `Truncate`) — direct calls
- File I/O (`WriteExclusionConfig`) — temp files created and cleaned up per test
- Process-dependent flows (`RestoreService`, `VerifyService`) — `Moq` mock of `IProcessRunner`
- `[assembly: InternalsVisibleTo("ImagingTool.Tests")]` in `Properties/AssemblyInfo.cs` exposes `internal` members to the test project
- The main `.csproj` excludes `ImagingTool.Tests/**` from compilation via `<Compile Remove>`
