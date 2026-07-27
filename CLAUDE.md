# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**ProjectCloner** — a cross-platform (macOS + Windows) Avalonia desktop app that turns an existing
git project into a fresh, clean clone published to a **new Bitbucket repository**, with a build gate
and self-update. It grew out of the original `ProjectCoppierCore` console utility (namespace-replacing
copier), which has been removed; its copy logic now lives in `ProjectCopier`.

> Note: the GitHub repo is named `ihorbondarev/ProjectCoppier` and the working folder is
> `ProjectCoppierCore`, but the solution/app is **ProjectCloner**. Don't be confused by the old names.

## Solution layout

```
ProjectCloner.sln
├── src/ProjectCloner.Core/   class library, net9.0 — ALL logic, no UI
└── src/ProjectCloner.App/    Avalonia 12 + net9.0 — GUI (MVVM, CommunityToolkit.Mvvm)
```

- **Keep all logic in Core.** The App layer is thin: views, view models, and the composition root in
  `App.axaml.cs`. No business logic in code-behind beyond file pickers and window glue.
- Core has `ImplicitUsings` enabled; so does App. Avalonia namespaces are still imported explicitly.

See `docs/ARCHITECTURE.md` for the full module map and the clone pipeline.

## Core conventions (follow these)

- **Git is driven through the system `git` CLI**, never LibGit2Sharp. All process execution goes
  through `Infrastructure/ProcessRunner` (async, streamed output, cancellation, env vars).
- **Logging/progress** is via `IProgress<ProgressReport>` passed as a method parameter (services are
  stateless). Use the null-safe `LogExtensions` helpers: `log.Step/Info/Success/Warning/Error(...)`.
  The UI marshals these to the log panel automatically (the `Progress<T>` is created on the UI thread).
- **External tools must be resolved, not assumed on PATH.** A GUI app launched from Finder/Explorer
  has a minimal PATH. Use `Infrastructure/ExecutableResolver.Resolve(name, extraDirs)` (returns the
  full path or null). This is why `mysqldump` is resolved via `CommonMysqlClientDirs()`.
- **User-entered paths must be normalized** with `Infrastructure/PathUtil.Expand(path)` — it expands
  `~`, environment variables, and resolves to absolute. The shell does this in a terminal; .NET does
  not, and a Finder launch has `CWD=/`. Applies to source/target/backup-folder/ssh-key/mysqldump paths.
- **Git over SSH**: the source `git pull` may use an `git@…` remote. The orchestrator builds
  `GIT_SSH_COMMAND="ssh -o StrictHostKeyChecking=accept-new [-i <key> -o IdentitiesOnly=yes]"` from
  `AppSettings.SshKeyPath` and passes it to `PullAsync`. Push uses an authenticated HTTPS URL instead
  (so credentials are never written into `.git/config`).
- **The DB backup is non-fatal and standalone.** `DatabaseBackupService.TryBackupAsync` never throws
  (internal try/catch) and is invoked by its own UI command, independent of the clone — because the DB
  is behind a VPN and git is not. Don't re-couple it into the clone pipeline.
- **Settings** live in `AppSettings`, persisted as JSON in the user profile via `SettingsStore`
  (`~/.config/ProjectCloner/settings.json` / `%APPDATA%`). Never commit settings; secrets are stored
  in plaintext locally (acceptable for a local dev tool — flagged for future OS-keychain hardening).

## The clone pipeline (`CloneOrchestrator.RunAsync`, 7 steps)

1. Verify source is a **clean** git repo (abort on uncommitted changes — protects the user's work).
2. `git checkout master` + `git pull --ff-only` (SSH env applied here).
3. Copy source → target with **namespace replacement** (`ProjectCopier`); excludes
   `node_modules`, `bin`, `obj`, `.git`, `.svn`, `.vs`; regenerates the GUID in `AssemblyInfo`.
4. Remove `bitbucket-pipelines.yml` (`PipelineCleaner`).
5. Fresh `git init -b master` (`InitFreshAsync`) → **`GitIgnoreNormalizer`** → single initial commit
   (`CommitAllAsync`). The normalizer sits between init and add on purpose: it asks git which files the
   inherited `.gitignore` hides, and after staging that information is gone.
6. Build gate (`BuildRunner`): `npm ci` + `npm run build` for each React project, `dotnet build -c
   Release` for the solution. **Any failure aborts before pushing.**
7. Create the Bitbucket repo via REST API (`BitbucketClient`) and push (authenticated HTTPS URL).

`DryRun` stops before step 7. Why no `reset --hard` on the copy: step 1 already guarantees the source
equals committed master, and a reset would *undo* the namespace replacement on tracked files.

**Adopt mode.** The target may also be a folder holding a freshly cloned, still-empty Bitbucket repo
(create the repo, clone it into `P0079`, point the app there). Then `.git` and `origin` are kept, step 5
skips `init`, and step 7 pushes to the existing origin on the repo's own branch instead of creating a
second repository. Any content beyond git scaffold (`README`, `LICENSE`, …) aborts the run.

**`.gitignore` is inherited, and both source templates are broken** — the legacy one scopes build-output
rules to named folders (`/BusinessLogic/bin`), so every other project leaks `bin/obj`; the newer one
blanket-ignores `*.mp4`/`*.mov`, so UI videos never enter the commit and CI fails on a fresh clone while
the local build gate passes. `GitIgnoreNormalizer` leaves the inherited file alone and appends a marked
section with the missing build rules plus `!` negations for media the sources actually reference.

## Build / run / test

```bash
dotnet build ProjectCloner.sln                 # build everything
dotnet run --project src/ProjectCloner.App      # run the GUI
```

There is **no unit-test project**. To verify Core changes, the established pattern is a throwaway
console harness outside the repo that references `ProjectCloner.Core` and drives the orchestrator
against a temp git fixture (bare `origin.git` + clone, so `pull` works), then delete it. Use `DryRun`
+ `RunBuilds=false` for fast checks. Always clean up `/tmp` fixtures afterward.

Smoke-test the GUI by launching it briefly and checking the log is empty (a crash on init prints a
stack trace). XAML uses compiled bindings (`x:DataType`), so binding errors fail the build.

## Releasing a new version

Versioning is driven by the git **tag**; CI passes `-p:Version` from it.

1. Bump `<Version>` and `<ApplicationDisplayVersion>` in
   `src/ProjectCloner.App/ProjectCloner.App.csproj` to match the next tag.
2. Commit, `git push origin master`.
3. `git tag vX.Y.Z && git push origin vX.Y.Z` → triggers `.github/workflows/release.yml`, which
   publishes self-contained builds for `osx-arm64`, `osx-x64`, `win-x64` as
   `ProjectCloner-<rid>.zip` attached to a GitHub Release.

The self-updater (`UpdateService`) compares the running assembly version to the latest release tag and
downloads the asset whose name contains the current RID — so **the asset name must contain the RID**
and the **csproj version must be bumped** for older clients to detect the update.

> Releases live on `master`; this repo's workflow is master-based. Do not branch for a release.
> Commit messages end with the `Co-Authored-By: Claude …` trailer.

## Gotchas / things that have bitten us

- **Finder launch ⇒ minimal PATH and no ssh-agent.** Root cause of both the SSH-pull failure and the
  "mysqldump not found" (which surfaced as a confusing "no such folder" because the process error
  text includes the working directory). Fixes: SSH key setting + `ExecutableResolver`.
- **`~` is shell-only.** Always run user paths through `PathUtil.Expand`.
- **Avalonia 12 + net9.0.** Only .NET 9 SDK is installed; do not target net10. `Watermark` is obsolete
  — use `PlaceholderText`.
- **macOS Gatekeeper / Windows SmartScreen**: release builds are unsigned; first launch needs
  right-click → Open (no Apple Developer signing set up).

## Current status / known pending items

- Settings **"Check for updates on startup"** is persisted but **not wired** — only the manual button
  checks. Wiring it would go in `App.axaml.cs` / `MainWindowViewModel`.
- Settings **"Enable MySQL backup step"** (`DatabaseSettings.Enabled`) is persisted but **unused** —
  the standalone "Run backup now" button is the actual trigger. Either remove it or use it to gate the
  backup card.
- The plan that produced this app is at
  `~/.claude/plans/streamed-churning-galaxy.md` (outside the repo).
