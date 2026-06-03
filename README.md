# ProjectCloner

Cross-platform (macOS + Windows) desktop tool that turns an existing project into a fresh,
clean clone published to a brand-new Bitbucket repository.

In one run it:

1. Verifies the source git repo is clean (aborts otherwise — your uncommitted work is never touched).
2. Checks out `master` and pulls the latest changes on the source.
3. Copies the project into a new folder, **replacing the namespace** in file names and contents
   and regenerating the GUID in `AssemblyInfo` files. `node_modules`, `bin`, `obj` and `.git`
   are never copied.
4. Removes `bitbucket-pipelines.yml`.
5. Initializes a **fresh git history** (single initial commit).
6. Runs a **build gate** — `npm ci` + `npm run build` for each React/Node project and
   `dotnet build -c Release` for the .NET solution. If a build fails, nothing is pushed.
7. Creates a new **Bitbucket repository via the REST API** and pushes the clone to it.

The app can also **update itself** from GitHub Releases.

## Architecture

| Project | Purpose |
| --- | --- |
| `src/ProjectCloner.Core` | All logic: git, copy/namespace, build gate, Bitbucket API, self-update. No UI. |
| `src/ProjectCloner.App`  | Avalonia GUI (MVVM, CommunityToolkit). |

The pipeline lives in [`CloneOrchestrator`](src/ProjectCloner.Core/Services/CloneOrchestrator.cs).
Git is driven through the system `git` CLI via [`ProcessRunner`](src/ProjectCloner.Core/Infrastructure/ProcessRunner.cs).

## Requirements

- [.NET SDK 9](https://dotnet.microsoft.com/) (to build)
- `git` on `PATH`
- `node` / `npm` on `PATH` (only needed when a clone has a React/Node front-end to build)

## Build & run

```bash
dotnet build ProjectCloner.sln
dotnet run --project src/ProjectCloner.App
```

## Configuration

Open **Settings…** in the app. Settings are stored as JSON in your user profile
(`%APPDATA%/ProjectCloner/settings.json` on Windows, `~/.config/ProjectCloner/settings.json`
on macOS/Linux) and are **never** committed.

- **Bitbucket**: workspace, username, and an **App Password** (Bitbucket → Personal settings →
  App passwords) or repository access token with `repository:admin`/write scope. Used to create the
  repo and push.
- **Updates**: the GitHub owner/repo to pull releases from.
- **Database backup** (optional, see below).

### Dry run

Tick **Dry run** to execute everything up to (but not including) Bitbucket repo creation and push —
useful for verifying the clone, namespace replacement and the build gate safely.

## Self-update

`Check for updates` queries the configured GitHub repository's latest release, downloads the asset
matching the current runtime identifier (e.g. `ProjectCloner-osx-arm64.zip`), and launches a small
helper that swaps the files once the app exits, then relaunches it.

Releases are produced by [`.github/workflows/release.yml`](.github/workflows/release.yml) on a
`v*` tag: it publishes self-contained builds for `osx-arm64`, `osx-x64` and `win-x64` and attaches
them to a GitHub Release.

```bash
git tag v0.2.0 && git push origin v0.2.0   # triggers the release workflow
```

## Optional: MySQL backup

Tick **Backup database** to dump the source site's MySQL database before the pipeline file is removed.

- The DB **host/IP is read from the source's `bitbucket-pipelines.yml`** (first IPv4 found); the
  remaining credentials (user, password, database, port) come from Settings.
- The configured "tables to exclude" keep their **schema** in the dump but **no rows** — implemented
  with `mysqldump --ignore-table` for the data plus a `--no-data` pass for their structure. The
  **live database is never modified.**
- Requires `mysqldump` on `PATH`. The step is **non-fatal**: a missing tool, a missing host, or any
  error is logged as a warning and the clone continues.

Backups are written to the configured folder (or a temp folder) as `{database}_{host}.sql`.

## License

MIT — see [LICENSE](LICENSE).
