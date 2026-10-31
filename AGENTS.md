# AGENTS.md

Guidance for AI coding agents working in this repository. Read the linked documentation before
making larger changes; `docs/Repository.md` is the human-facing source of truth for the layout and
contribution workflow.

## Project overview

XISO Studio is a cross-platform desktop application for the Xbox preservation and emulation
community. It converts, verifies, and explores Xbox and Xbox 360 disc images (ISO/XISO, CSO, ZAR,
and CHD). The UI is **Avalonia** on **.NET 10**, and the conversion engines are the managed
**XISOSharp**, **ZArchiveSharp**, and **CHDSharp** libraries. The application runs on Windows,
Linux, and macOS (x64 and ARM64).

- Repository: <https://github.com/purelogiccode/XISOStudio>
- Documentation: <https://purelogiccode.github.io/XISOStudio/>
- License: GNU GPL v3.0 (`LICENSE.txt`)

## Repository layout

```text
XISOStudio/              Avalonia application (UI, services, interfaces, models)
XISOStudio.Tests/        xUnit + Moq test project
docs/                    Documentation source for the GitHub wiki and Pages site
.github/workflows/       CI, release, wiki, and Pages workflows
XISOStudio/bin/Release/  Append-only local archive of release bundles
```

## Build and test

Run `dotnet` commands from the repository root (`global.json` pins the .NET 10 SDK).

- Build: `dotnet build CSharp_XISOStudio.sln -c Release`
- Test: `dotnet test CSharp_XISOStudio.sln -c Release --no-build`

Both projects enable nullable reference types and implicit usings. **Meziantou.Analyzer** and
**Roslynator** run as build warnings; keep the build clean. CI also runs ReSharper InspectCode and
fails on any finding. Add or update tests in `XISOStudio.Tests` for behavioral changes.

## Coding conventions

- Keep business logic in services behind interfaces in `Interfaces/`, registered through
  `App.ConfigureServices`; UI code should not construct services manually.
- UI partial classes (`MainWindow*.cs`) orchestrate services and update the UI; they contain no
  business logic.
- User-facing error messages must be actionable. Environmental failures (disk space, permissions,
  locked files, network/device errors, invalid user input) are expected conditions and are logged at
  **Information**, not Warning or higher — every Warning-or-higher Serilog event is forwarded to the
  bug-report API.
- Preserve the existing architecture, naming, and formatting. Avoid unrelated refactors.
- Never commit secrets, tokens, personal paths, or user data.

## Documentation

`docs/` is the single source for both the GitHub wiki and the Pages site. When adding, renaming, or
removing a page, update all navigation locations: `docs/_Sidebar.md`, `docs/_layouts/default.html`,
the page's top navigation table, and `docs/index.md`. Update the relevant documentation in the same
change as the code.

## Release bundles (do not delete)

- **NEVER delete, move, or overwrite any file inside `XISOStudio/bin/Release/`** (or any of its
  subfolders). That folder is the release-bundle archive; bundles from previous versions are kept
  there on purpose. A previous session deleted them and they had to be recovered from GitHub.
- Always add new bundles alongside the existing ones. Never clean the folder, never `rm`/`Remove-Item`
  a glob there, and never publish directly into it.
- Build to a staging folder outside the repository (for example
  `%TEMP%\opencode\xiso-publish\<rid>`) and only copy the final `release_*.zip` file into
  `XISOStudio/bin/Release/`.
- Bundle naming pattern: `release_<MAJOR.MINOR.PATCH>_<rid>.zip` for the six RIDs:
  `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- Each zip contains, at the root (no wrapping folder): the single-file executable (`XISOStudio.exe`
  on Windows, `XISOStudio` elsewhere), the bundled 7-Zip fallback for that platform
  (`7za.exe`/`7za_arm64.exe` on Windows, `7zz_linux_x64`/`7zz_linux_arm64` on Linux, `7zz_osx` on
  macOS), `7-Zip-License.txt`, `ReadMe.md`, `WhatsNew.md`, and `LICENSE.txt`.
- The Linux/macOS executables must keep the Unix execute bit inside the zip (`ExternalAttributes`
  `0x81ED << 16`); `Compress-Archive` does not set it, so the bundling step patches the zip after
  creation. Never extract a bundle over the existing zip; only add new files.
- The bundles are framework-dependent (`--self-contained false`, do not embed the .NET runtime) and
  single-file (`-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`).

## Releases

Releases are cut from `master` with a `release_MAJOR.MINOR.PATCH` tag. The tag must match
`<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in `XISOStudio/XISOStudio.csproj`; the release
workflow refuses to publish otherwise. The application's update checker parses the numeric part of
the latest release tag, so keep the `MAJOR.MINOR.PATCH` format intact. Do not bump the version or
create a tag unless the task explicitly asks for a release.

See `docs/CI-and-Releases.md` for the complete pipeline and `docs/Building-from-Source.md` for local
publish commands.

## Security

Report vulnerabilities privately as described in `SECURITY.md`; never open a public issue for a
security problem. Do not include exploit details, credentials, or personal data in commits, logs, or
test fixtures.
