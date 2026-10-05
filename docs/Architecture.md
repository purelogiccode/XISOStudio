# Architecture

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [Usage Guide](Usage-Guide.md) | [**Architecture**](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

The application is a cross-platform **Avalonia** (.NET 10, `net10.0`) desktop app — one codebase for Windows, Linux, and macOS — built on modern software engineering principles: dependency injection, service-oriented design, interface-driven contracts, and a comprehensive xUnit test suite.

## Solution Layout

```text
CSharp_XISOStudio.sln
├── XISOStudio/               Main Avalonia application
│   ├── Program.cs                       Entry point (Avalonia AppBuilder)
│   ├── App.axaml(.cs)                   Theme/styles + DI composition, global error handlers
│   ├── MainWindow.axaml(.cs)            Shell window + navigation
│   ├── MainWindow.ConversionAndTesting.cs   Convert/Test workflows (UI layer)
│   ├── MainWindow.FileSelection.cs      Folder scanning + selectable file lists (UI layer)
│   ├── MainWindow.XIsoExplorerLogic.cs  Explorer workflows (UI layer)
│   ├── MainWindow.CheckForUpdatesAsync.cs   Update check integration
│   ├── MainWindow.UIHelpersAndWindowEvents.cs  UI helpers, links, window events
│   ├── AboutWindow.axaml(.cs)           About dialog
│   ├── Dialogs/                         MessageBoxWindow (cross-platform modal dialogs)
│   ├── Interfaces/                      One interface per service (IOrchestratorService, IXisoSharpService, ...)
│   ├── Models/                          DTOs and enums (FileProcessingStatus, FileItem, BatchOperationProgress, ...)
│   └── Services/                        All business logic
│       ├── OrchestratorService.cs       Batch pipeline coordination
│       ├── SupportedFiles.cs            Extension filters shared by the UI lists and folder scans
│       ├── XisoSharpService.cs          XISO/ZAR/CSO conversion via the XISOSharp library
│       ├── ChdService.cs                CHD conversion via the CHDSharp library
│       ├── XisoIntegrityService.cs      Structural audit + deep scan via XISOSharp / ZArchiveSharp / CHDSharp
│       ├── ImageExplorerFactory.cs      Opens the right IImageExplorer (XISO/CISO, ZAR or CHD)
│       ├── XisoImageExplorer.cs         IImageExplorer over ISO/CSO via XisoExplorer
│       ├── ZarImageExplorer.cs          IImageExplorer over ZAR via ZArchiveReader (zip-slip safe)
│       ├── ChdImageExplorer.cs          IImageExplorer over CHD via CHDSharp + XISOSharp (on-demand decompression)
│       ├── ImagePaths.cs                Shared internal-path normalization helpers
│       ├── FileExtractorService.cs      Archive handling (zip/7z/rar), locked-file retries
│       ├── FileMoverService.cs          File moves with network/lock retries
│       ├── DiskMonitorService.cs        Read/write speed and free-space monitoring
│       ├── BugReportService.cs          Automatic bug reporting client
│       ├── BugReportSink.cs             Serilog sink: forwards Warning+ events to the bug report API
│       ├── UiLogSink.cs                 Serilog sink: on-screen log pane
│       ├── SynchronousProgress.cs       In-order IProgress adapter for library progress callbacks
│       ├── StatsService.cs              Anonymous usage statistics client
│       ├── UpdateChecker.cs             GitHub release update checks
│       └── ...                          Formatting, path helpers, etc.
└── XISOStudio.Tests/         xUnit + Moq test suite
```

The bundled 7-Zip CLI fallback (`7za.exe`/`7za_arm64.exe` on Windows, `7zz_linux_x64`/`7zz_linux_arm64` on Linux, `7zz_osx` on macOS) is copied next to the executable and invoked as an isolated child process when SharpCompress cannot extract an archive; a system `7z` on `PATH` is used as a further fallback. All XISO and CHD encoding/decoding is performed in-process by the `XISOSharp` and `CHDSharp` NuGet packages, and all image libraries are pure managed code with no native dependencies.

## Dependency Injection

`App.ConfigureServices` registers every service with `Microsoft.Extensions.DependencyInjection`. All core logic is decoupled from the UI behind interfaces, enabling the service layer to be unit-tested without the UI framework.

| Service | Lifetime | Responsibility |
|:---|:---|:---|
| Serilog `ILogger` | Singleton | Structured logging pipeline (UI, rolling file, and bug-report sinks) |
| `IDiskMonitorService` | Singleton | Drive throughput counters (Windows performance counters; `N/A` on Linux/macOS) and free-space queries |
| `IOrchestratorService` | Singleton | Batch pipeline: per-file dispatch for the selected files, progress, cancellation |
| `IXisoSharpService` | Singleton | XISO/ZAR/CSO conversion via the XISOSharp library |
| `IChdService` | Singleton | CHD conversion via the CHDSharp library (rewrites non-optimized inputs through `IXisoSharpService`, then encodes and verifies) |
| `IXisoIntegrityService` | Singleton | Structural audit + deep scan via XISOSharp (ISO/CSO), ZArchiveSharp (ZAR) and CHDSharp (CHD container + Xbox filesystem) |
| `IImageExplorer` | Per open image | Explorer over ISO/CSO (`XisoExplorer`), ZAR (`ZArchiveReader`) or CHD (`ChdImageStream` + XISOSharp), built by `ImageExplorerFactory` |
| `IFileExtractor` | Transient | Archive extraction with fallbacks and lock retries |
| `IFileMover` | Transient | Move/copy operations with retry + backoff |
| `IBugReportService` | Singleton | Sends exception reports to the developer endpoint |
| `IStatsService` | Singleton | Anonymous usage statistics |
| `IUpdateChecker` | Singleton | Queries the GitHub releases API |
| `IMessageBoxService`, `IUrlOpener`, `IScreenshotService` | Singleton | UI-adjacent helpers kept testable |

HTTP clients are created through `IHttpClientFactory` with named clients and pooled-connection handlers.

## Conversion Pipeline

```text
MainWindow (Convert tab)
   ├─ scans the input folder for supported files (SupportedFiles filter, recursive option)
   ├─ user ticks the files to process (selectable DataGrid list)
   └─► OrchestratorService (ConvertFilesAsync)
         ├─ for each selected file:
          │    ├─ .zip/.7z/.rar ──► FileExtractorService ──► temp ISO ──► convert ──► cleanup
          │    └─ .iso ──► XisoSharpService (in-process: XISO / ZAR / CSO)
          │                or ChdService (CHDSharp CHD encode + deep verify)
         ├─ after each file: optional integrity check, optional original deletion,
         │   file moves (retry-aware), progress + stats updates
         └─ final summary (success/fail/skip counts, elapsed time)
```

The requested output format flows from the UI through `ConvertFilesAsync`/`ConvertAsync`:
**XISO**, **ZAR**, and **CSO** go to `IXisoSharpService.ConvertIsoAsync` — XISO uses
`XisoReader.Rewrite`, ZAR streams the game-partition tree via `XisoZarchive.CreateZar` (Redump
partition offsets detected with `XgdTables`), and CSO repacks non-optimized inputs to a
temporary XISO and calls `CisoWriter.CompressToCso` (CISO v2/LZ4). **CHD** goes to
`IChdService.ConvertIsoToChdAsync`, which reuses the optimized-XISO rewrite for non-optimized
inputs and then calls `ChdEncoder.EncodeRaw` (CHDSharp) with the chdman `createdvd` preset
(4096-byte hunks, 2048-byte units, `lzma,zlib,huff,flac`, `DVD ` metadata), followed by a
header check and — when output integrity is enabled — a full `Chd.CheckFile` deep verification.

Already-optimized inputs are skipped by default; when **Skip $SystemUpdate** is enabled they are
rewritten through the `$SystemUpdate` filter before CSO/CHD packing, so the option is honored for
every output format.

The folder-scanning `ConvertAsync`/`TestAsync` overloads remain available for callers that want the
orchestrator to discover files itself; the UI always passes the explicit list of ticked files.

The test pipeline (`TestAsync`/`TestFilesAsync` → `IXisoIntegrityService`) dispatches by extension:
`.iso` and `.cso` (single or split `.1.cso`) go through `XisoReader.AuditXiso(...,
requireOptimizedTag: false)` and, with the deep scan enabled, a sequential read of the whole
decompressed image; `.zar` is opened, tree-walked, and (deep scan) fully decompressed through
`ZArchiveReader`; `.chd` (Xbox DVD images only) is verified with `Chd.CheckFile` (header-only, or
every hunk and checksum with the deep scan) and the Xbox filesystem is audited through
`XisoReader.AuditXiso(IBlockDevice)` over a `ChdImageStream`. Split CISO continuation parts are
hidden from the list and move together with part 1.

Safety characteristics of the pipeline:

- **Pre-flight checks** — output-drive free space (size-aware for compressed formats) and FAT32 file-size limits are verified before conversion starts; failures skip the file with a clear message instead of failing late.
- **Environmental errors are surfaced, not reported** — disk-space, network, and device failures (including `ERROR_IO_DEVICE` `0x45D` and the fatal `ERROR_DEVICE_HARDWARE_ERROR` `0x800701E3`) stop or skip with actionable messages and are excluded from automatic bug reports.
- **Transient failures retry** — locked files and network hiccups use exponential backoff (see `FileExtractorService`, `FileMoverService`); permanent errors are not retried. A pre-existing output held open by another process is retried briefly before the conversion is reported as failed (`PathHelper.TryDeleteExistingFileWithRetryAsync`).
- **Atomic replace-originals** — deletion of inputs happens only after the converted file exists and (optionally) passes validation. An archive is removed only when every entry was extracted and every extracted image was converted; skipped entries or unconverted images keep the archive.
- **Unique output names** — output paths are reserved per batch, so two inputs with the same base name cannot overwrite each other.
- **Per-file isolation** — an unreadable, missing, or invalid file is reported individually and never aborts the remaining batch.
- **Age- and ownership-checked temp cleanup** — only app-created GUID work folders older than six hours are deleted, so user folders and another instance's active work folders are safe.
- **Lease-guarded explorer** — background copy-outs hold an explorer lease; the explorer is never disposed mid-extraction, and the close flow is reentrancy-guarded.
- **Cancellation is cooperative** — child processes and I/O loops observe a `CancellationToken`; cleanup runs with `CancellationToken.None` so it cannot mask the original error.

## Logging

Logging uses a single [Serilog](https://serilog.net/) pipeline configured by `App.ConfigureLogging()` — called from `Program.Main` before Avalonia initializes its platform subsystems, and idempotently from the `App` constructor — with three sinks:

1. **UI** (`UiLogSink`) — timestamped lines in the on-screen log pane. The window queues them in a bounded buffer (`LogViewBuffer`, newest 2,000 lines) and updates the viewer in throttled batches (500 lines per 100 ms), so a logging burst can neither queue one dispatcher callback per line nor grow the text control without limit; a re-entrancy guard keeps a failing viewer from feeding back into the pipeline. XISOSharp's chunked console output is reassembled into complete, sanitized lines (`LibraryOutputLineBuffer`) first, so its backspace/space progress animation never floods the viewer.
2. **File** — rolling daily log under the per-user application-data folder (`%LocalAppData%\XISOStudio\logs` on Windows, `~/.local/share/XISOStudio/logs` or `~/Library/Application Support/XISOStudio/logs` elsewhere) as `log-*.txt` (10 MB per file, 14 files retained) with level and exception details.
3. **Bug report** (`BugReportSink`) — every event at **Warning or higher** is forwarded to the bug report API (fire-and-forget, never throws). Avalonia framework events (`SourceContext = "Avalonia"`) are written to the viewer and log file but excluded from automatic reports.

Avalonia's own diagnostics are routed into the same pipeline by `AvaloniaSerilogSink`, which is installed before the platform subsystems start so startup warnings are captured too.

Services inject `Serilog.ILogger` and log with structured message templates. Expected user/environmental errors are logged at Information level so they do not generate bug reports; genuine defects log at Warning/Error/Fatal. Failed bug-report deliveries are recorded at Information level instead of being silently dropped.

## Error Handling and Reporting

Three layers of defense:

1. **Global handlers** in `App` (`AppDomain.UnhandledException`, `Dispatcher.UIThread.UnhandledException`, `TaskScheduler.UnobservedTaskException`) log through Serilog and keep the app alive where possible; fatal shutdown paths also send a blocking report.
2. **Per-operation catches** translate known failure classes (disk full, access denied, FAT32 limits, locked files, invalid images) into user-facing messages and log at an appropriate level.
3. **Automatic bug reports** are sent by the Serilog `BugReportSink` for Warning+ events, with complete environment, error, and exception sections; expected environmental errors stay at Information level and are shown to the user instead.

## Models

| Model | Purpose |
|:---|:---|
| `FileProcessingStatus` | Per-file outcome (converted/skipped/failed/already-optimized/invalid-input) |
| `BatchOperationProgress` | Progress snapshot used for UI updates, including the invalid-image count |
| `ArchiveExtractionResult` | Archive extraction outcome (success + skipped entries) used by the archive-deletion safety check |
| `IsoTestResultStatus` | Test-view outcome states |
| `ImageEntry` | One file/directory inside an image or archive (name, path, size, type) |
| `XisoExplorerItem` | Row model for the explorer list (wraps an `ImageEntry`) |
| `GitHubReleaseInfo` | Deserialized GitHub release payload |
| `CloudRetryResult` | Result of a cloud-hydration retry |

## Testing

The `XISOStudio.Tests` project (xUnit, Moq) covers models, services, and helper utilities with
**1,452 tests**:

```bash
dotnet test CSharp_XISOStudio.sln
```

The suite includes service tests (e.g., `OrchestratorServiceTests`, `FileExtractorServiceTests`, `XisoSharpServiceTests`, `XisoIntegrityServiceTests`) plus model and helper coverage. Tests run
sequentially (`CollectionBehavior(DisableTestParallelization = true)`) because XISOSharp uses
process-wide static state and the process working directory during extract/pack. Analyzers
(Meziantou, Roslynator) enforce code quality on both projects.
