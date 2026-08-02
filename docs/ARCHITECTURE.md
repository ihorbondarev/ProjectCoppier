# Architecture

Deep reference for ProjectCloner. See `../CLAUDE.md` for working conventions and commands.

## Layers

```
ProjectCloner.App (Avalonia, MVVM)        ── views, view models, composition root
        │  depends on
        ▼
ProjectCloner.Core (class library)        ── all logic, no UI dependency
```

The App's composition root is `src/ProjectCloner.App/App.axaml.cs`
(`OnFrameworkInitializationCompleted`): it news up one `ProcessRunner`, the services, the
`CloneOrchestrator`, `SettingsStore`, `UpdateService`, and the `MainWindowViewModel`.

## Core module map

| File | Responsibility |
| --- | --- |
| `Infrastructure/ProcessRunner.cs` | Async `Process` wrapper: streamed stdout/stderr, cancellation (kills the tree), env vars. Clear error when an executable can't start. |
| `Infrastructure/PathUtil.cs` | `Expand(path)` — env vars + leading `~` + `Path.GetFullPath`. |
| `Infrastructure/ExecutableResolver.cs` | `Resolve(name, extraDirs)` → full path or null; `CommonMysqlClientDirs()` for Homebrew/MySQL. |
| `Infrastructure/LogExtensions.cs` | Null-safe `IProgress<ProgressReport>` helpers (`Step/Info/Success/Warning/Error`). |
| `Models/ProgressReport.cs` | `LogLevel` enum + `ProgressReport` record (one streamed log line). |
| `Models/CloneModels.cs` | `CloneRequest`, `CloneResult`, `BuildResult`. |
| `Config/AppSettings.cs` | Settings DTOs: root, namespace, `SshKeyPath`, `BitbucketSettings`, `UpdateSettings`, `DatabaseSettings`. |
| `Config/SettingsStore.cs` | Load/save JSON in the user profile (camelCase). Corrupt file ⇒ defaults (never crashes). |
| `Services/GitService.cs` | git CLI wrappers: `IsRepository/IsClean/GetCurrentBranch/GetBranch/GetUpstream/Checkout/Pull/ResetHard/Clean/InitFresh/AddRemote/Push`. `Pull` takes an env dict for `GIT_SSH_COMMAND`; `GetUpstream` answers null for a purely local branch. |
| `Services/ProjectCopier.cs` | Recursive copy + namespace replace (filenames + text content) + AssemblyInfo GUID regen. Thread-safe (`Parallel.ForEach`, no shared Console). Skips binary extensions and excluded dirs. |
| `Services/PipelineCleaner.cs` | Find/remove `bitbucket-pipelines.yml`. |
| `Services/BuildRunner.cs` | Build gate: npm (`npm.cmd` on Windows) for each `package.json` with a `build` script; `dotnet build` for the `.sln` (else root `.csproj`). Skips `node_modules/bin/obj`. |
| `Services/BitbucketClient.cs` | `POST /2.0/repositories/{ws}/{slug}` (Basic auth, app password); parses the https clone URL + html URL. |
| `Services/DatabaseBackupService.cs` | Standalone, non-fatal MySQL backup. Reads host IP from the source's pipeline file; resolves `mysqldump`; dumps with `--ignore-table` for excluded tables' data + a `--no-data` pass for their schema. |
| `Services/CloneOrchestrator.cs` | Ties the 7-step pipeline together; builds `GIT_SSH_COMMAND`; builds the authenticated push URL; slugifies the repo name. |
| `Update/UpdateService.cs` | GitHub Releases check + download; writes a platform helper script that swaps files after exit and relaunches. |

## App module map

| File | Responsibility |
| --- | --- |
| `App.axaml(.cs)` | Theme + global styles (dark, cards, accent/ghost buttons); composition root. |
| `Views/MainWindow.axaml(.cs)` | Form, options, **Database backup card** (standalone), log panel (auto-scroll), status bar, file pickers. |
| `Views/SettingsWindow.axaml(.cs)` | Settings form grouped into cards; SSH key / mysqldump file pickers. |
| `ViewModels/MainWindowViewModel.cs` | `CloneCommand`, `BackupDatabaseCommand` (standalone), `CancelCommand`, `CheckUpdatesCommand`; streams progress to `Log`. |
| `ViewModels/SettingsViewModel.cs` | Loads/saves `AppSettings` through `SettingsStore`. |
| `ViewModels/LogLine.cs` / `BoolBrushes.cs` | Coloured log line; busy/idle indicator brush converter. |

## Clone pipeline (sequence)

```
RunAsync(request, settings, log, ct)
  expand source/target paths (PathUtil)
  validate: source exists + is a git repo; target empty, or an empty cloned repo → adopt mode
  1. IsClean(source)                         → abort if dirty
  2. current branch (abort on detached HEAD); pull --ff-only if it has an upstream (GIT_SSH_COMMAND)
  3. ProjectCopier.Copy(source→target, replace namespace)
     [optional] DatabaseBackupService (only if request.BackupDatabase; GUI uses the standalone button instead)
  4. PipelineCleaner.RemovePipelineFiles(target)
  5. fresh mode: GitService.InitFreshAsync(target)   → rm .git, init -b master
     GitIgnoreNormalizer.NormalizeAsync(target)      → must sit between init and add
     GitService.CommitAllAsync(target)               → add -A, commit
  6. BuildRunner.RunAsync(target)             → npm + dotnet; failure aborts
  7. if !DryRun:
       fresh mode: BitbucketClient.CreateRepositoryAsync → AddRemote → Push(auth URL, master)
       adopt mode: Push to the existing origin on its own branch
```

### Adopt mode

A target that already holds a **freshly cloned, still-empty Bitbucket repository** is accepted instead
of rejected: create the repo on Bitbucket, clone it into the future project folder (`P0079`), and point
the app at that folder. Its `.git` and `origin` survive, step 7 pushes there rather than creating a
second repository, and the branch is taken from the repo itself (`symbolic-ref`, which also answers
before the first commit) instead of being forced to `master`. Anything in the target beyond git's own
scaffold (`README`, `LICENSE`, `.gitignore`, …) aborts the run — that folder holds real work.

### `.gitignore` normalization

The clone inherits the source's `.gitignore`, and both templates in circulation are broken: the legacy
one lists build output per project folder by name (`/BusinessLogic/bin`), so every other project leaks
`bin/obj`; the newer one blanket-ignores media (`*.mp4`, `*.mov`), so UI videos never enter the commit —
the local build gate passes because the files are on disk, and CI fails on a fresh clone.
`GitIgnoreNormalizer` keeps the inherited content untouched and appends a marked section: the missing
build-output rules, plus `!` negations for media that the sources actually reference (unreferenced
videos stay ignored — these projects carry unused ones of tens of MB). It asks git itself which files
the rules hide, so it has to run **after `git init` but before `git add`**.

## Release / CI

- `.github/workflows/ci.yml` — `dotnet build -c Release` on push/PR.
- `.github/workflows/release.yml` — on tag `v*`: matrix publish `osx-arm64 / osx-x64 / win-x64`
  (self-contained), zip as `ProjectCloner-<rid>.zip`, attach to a GitHub Release.

## Self-update flow

`UpdateService.CheckForUpdateAsync` → compares assembly version to the latest release tag → if newer,
finds the asset whose name contains `RuntimeInformation.RuntimeIdentifier` → `DownloadAndApplyAsync`
downloads + extracts + launches a wait-for-exit helper (`.sh`/`.bat`) that copies the files over the
install dir and relaunches. The view model then requests app shutdown.
