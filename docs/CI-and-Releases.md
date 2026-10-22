# CI & Releases

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [**CI & Releases**](CI-and-Releases.md) |

---

This page describes how XISO Studio is built, tested, documented, and released. Everything runs in
GitHub Actions; no local machine is required to cut a release.

- **Repository:** <https://github.com/purelogiccode/XISOStudio>
- **Actions:** <https://github.com/purelogiccode/XISOStudio/actions>
- **Releases:** <https://github.com/purelogiccode/XISOStudio/releases>
- **Wiki:** <https://github.com/purelogiccode/XISOStudio/wiki>
- **Documentation site:** <https://purelogiccode.github.io/XISOStudio/>

---

## Workflows

| Workflow | File | Trigger | Purpose |
|:---|:---|:---|:---|
| **CI** | `.github/workflows/ci.yml` | Push / pull request to `master`, manual | Restore, build in Release, test on Windows/Linux/macOS, collect coverage, run InspectCode (gating), validate documentation, and publish six RID artifacts |
| **Release** | `.github/workflows/release.yml` | Push of a `release_MAJOR.MINOR.PATCH` tag, manual dispatch | Re-run the three-platform tests, verify the tag matches the project version, build and validate the six bundles, and create or update the GitHub release |
| **Update Wiki** | `.github/workflows/wiki.yml` | Push to `docs/**`, manual | Copy `docs/*.md` into the GitHub wiki, rewrite `.md` links, verify the published page, and verify the Pages build for the same commit |
| **Deploy Pages** | `.github/workflows/pages.yml` | Push to `docs/**`, manual | Build the Jekyll site with GitHub Pages and deploy it through the Pages environment |

### CI stages

1. **Build & test matrix** - `windows-latest`, `ubuntu-latest`, and `macos-latest` restore and build
   the solution in Release, then run the full xUnit suite. The Linux leg also collects
   `XPlat Code Coverage` and uploads it as the `coverage` artifact.
2. **InspectCode** - installs the pinned JetBrains command-line tools, runs
   `jb inspectcode --swea`, uploads the SARIF report, and **fails when the report contains any
   finding**. The solution is expected to stay at zero.
3. **Documentation** - verifies that every documentation page is reachable from the side menus and
   that internal `.md` links resolve, then builds the Jekyll site to catch Liquid or layout errors.
4. **Publish** - cross-publishes all six runtime identifiers (`win-x64`, `win-arm64`, `linux-x64`,
   `linux-arm64`, `osx-x64`, `osx-arm64`) as framework-dependent single-file artifacts.

### Release stages

1. **Test** - runs the suite on Windows, Linux, and macOS against the tagged commit.
2. **Version guard** - the tag must match `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in
   `XISOStudio/XISOStudio.csproj`. A mismatch fails the release before anything is published.
3. **Build bundles** - publishes each RID with `--self-contained false -p:PublishSingleFile=true
   -p:IncludeNativeLibrariesForSelfExtract=true`, stages the required files, restores Unix execute
   bits, and writes `release_<version>_<rid>.zip`.
4. **Validate bundles** - checks the six expected archive names, the complete file list in each
   archive, and the execute bit on `XISOStudio` and the bundled 7-Zip helper.
5. **Publish** - writes the release notes from the matching section of
   [Release Notes](Release-Notes.md), creates or updates the GitHub release, and uploads all bundles
   plus `SHA256SUMS.txt`.

---

## Release Bundles

Every release publishes six archives. The name is part of the update contract:

```text
release_MAJOR.MINOR.PATCH_<rid>.zip
release_3.1.2_win-x64.zip
release_3.1.2_win-arm64.zip
release_3.1.2_linux-x64.zip
release_3.1.2_linux-arm64.zip
release_3.1.2_osx-x64.zip
release_3.1.2_osx-arm64.zip
```

The application does not download a specific asset; `UpdateChecker` reads the latest release tag,
extracts the numeric version, and offers the release page. Keeping the `MAJOR.MINOR.PATCH` part of the
tag intact is therefore what makes the in-app update check work.

### Bundle contents

Each archive contains these files at its root (no wrapping folder):

| File | Notes |
|:---|:---|
| `XISOStudio.exe` / `XISOStudio` | Framework-dependent single-file executable; the .NET 10 runtime is **not** embedded |
| `7za.exe` + `7za_arm64.exe` | Windows bundles only; 7-Zip CLI fallback for x64 and ARM64 |
| `7zz_linux_x64` / `7zz_linux_arm64` | Matching Linux bundle only |
| `7zz_osx` | Both macOS bundles (universal binary) |
| `7-Zip-License.txt` | License for the bundled 7-Zip binaries |
| `ReadMe.md` | Project overview, badges, and installation instructions |
| `WhatsNew.md` | Highlights of the current release |
| `LICENSE.txt` | GNU GPL v3.0 |

Linux and macOS executables keep the Unix execute bit inside the ZIP (`0755`, stored in the ZIP
external attributes as `0x81ED << 16`), so extracting without `unzip` permission loss leaves the app
runnable. `chmod +x XISOStudio 7zz_*` remains a safe fallback.

### Local bundle archive

Finished bundles are also kept in the repository working tree under `XISOStudio/bin/Release/`. That
folder is an append-only archive: existing `release_*.zip` files are never deleted, moved, or
overwritten. New bundles are built in a temporary staging folder and only the final ZIP is copied in.
This is why the folder contains historical releases next to the current one.

---

## Documentation Publishing

`docs/` is the single source for two destinations, and both show the same side menu:

- **GitHub wiki** - `docs/_Sidebar.md` is the menu. The *Update Wiki* workflow clones the wiki
  repository, copies `docs/*.md`, renames `index.md` to `Home.md`, rewrites `.md` links, pushes, and
  checks the published `Home.md` for the current version.
- **GitHub Pages** - `docs/_layouts/default.html` renders the menu for every page, and
  `docs/_config.yml` holds the title, description, and theme. The *Deploy Pages* workflow builds the
  site with GitHub Pages and deploys it through the `github-pages` environment.

Every documentation page also starts with the same four-column navigation table, so the docs stay
navigable when read directly in the repository. When adding or renaming a page, update:

1. `docs/_Sidebar.md` (wiki menu)
2. `docs/_layouts/default.html` (Pages menu)
3. The page's top navigation table
4. `docs/index.md` (documentation map)

The wiki sync requires a repository secret named `WIKI_TOKEN` - a personal access token with `repo`
scope, because the default `GITHUB_TOKEN` cannot push to the wiki repository. When the secret is
absent, the sync step is skipped with a notice.

---

## Cutting a Release

1. Update `WhatsNew.md`, `ReadMe.md`, `docs/Release-Notes.md`, `docs/index.md`, and
   `XISOStudio/XISOStudio.csproj` to the new version.
2. Run the full suite locally:

   ```bash
   dotnet build CSharp_XISOStudio.sln -c Release
   dotnet test CSharp_XISOStudio.sln -c Release --no-build
   ```

3. Commit the release preparation and push `master`.
4. Tag and push:

   ```bash
   git tag release_3.1.2
   git push origin master
   git push origin release_3.1.2
   ```

5. The tag triggers the *Release* workflow, which tests, validates, builds the six bundles, and
   publishes the GitHub release. The docs push triggers the wiki sync and the Pages deployment.
6. Optionally build the bundles locally into the append-only archive
   (`XISOStudio/bin/Release/`) using the same `dotnet publish` settings as the workflow.

### Verifying a release

- **Actions:** all jobs green, including the InspectCode, documentation, and bundle validation steps.
- **Release assets:** six `release_<version>_<rid>.zip` files plus `SHA256SUMS.txt`.
- **Update check:** launching the previous version should offer the new release page.
- **Wiki:** `Home.md` shows the new version.
- **Pages:** <https://purelogiccode.github.io/XISOStudio/> shows the new version on the home page.

---

## Troubleshooting the Pipeline

| Symptom | Cause | Fix |
|:---|:---|:---|
| Release fails with *"Tag release_X does not match the project version"* | The csproj version and the tag disagree | Update all three version fields in `XISOStudio.csproj`, commit, and re-tag |
| Wiki step logs *"WIKI_TOKEN is not set"* | Missing repository secret | Add a PAT with `repo` scope as `WIKI_TOKEN` |
| Pages deployment is skipped or fails | Pages source is not configured | Set **Settings → Pages → Build and deployment → Source** to **GitHub Actions** |
| Bundle validation fails on the execute bit | The archive was created on Windows without mode attributes | Build on Linux/macOS or set the ZIP external attributes (`0x81ED << 16`) before uploading |
| InspectCode fails | A new finding was introduced | Fix the finding and re-run `jb inspectcode CSharp_XISOStudio.sln` |

---

*See [Repository](Repository.md) for the repository layout and [Building from Source](Building-from-Source.md)
for local build and publish commands.*
