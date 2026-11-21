# AGS-Copy

[![build](https://github.com/stefanozanarini/ags-copy/actions/workflows/build.yml/badge.svg)](https://github.com/stefanozanarini/ags-copy/actions/workflows/build.yml)

Windows copies and backups with **robocopy** as the engine, as a replacement for Uranium Backup.
The interface is in Italian (default) and English.

- **Guided job editor** in seven steps: general, source, destination, mode, options, schedule,
  summary. A job is saved only from the **summary** step. That step describes in plain words
  what is copied, where, and what is deleted. It also runs a **simulation** (`robocopy /L`) that
  lists the files to copy and the files and folders to delete.
- **Layout preview**: when you leave the destination step (or with *Preview the layout*), a
  window shows the source folder next to how the destination will look after the copy, folder by
  folder, with what is new, updated, kept, deleted or excluded. Robocopy copies the *content* of
  the source, so `C:\Data` → `E:\Backup` puts the files in `E:\Backup`, not `E:\Backup\Data`.
  The window says so, and warns when the destination already holds a folder named like the
  source (earlier copies went one level deeper), a `Data\Data` level too deep, or nothing in
  common with the source (a mirror would empty it). One click switches to
  `Destination\<source folder name>`.
- **The source is read-only, always.** There is no move mode. Extra robocopy arguments that
  would write to the source are refused, both when the job is saved and when the command is
  built: `/MOV` and `/MOVE` delete the source after copying, and `/M` resets its archive
  attribute. The source access check only reads.
- **Open files** of a local source are copied through a **Volume Shadow Copy** (VSS):
  - At the start of a run the service takes a snapshot of the volume (`Win32_ShadowCopy`,
    ClientAccessible) and links it as a folder under `%ProgramData%\AGS-Copy\vss`.
  - Robocopy reads from that folder; the snapshot is deleted when the run ends.
  - Snapshots left by a crash are removed when the service starts.
  - If VSS fails, for example on FAT volumes or with too little space, the run copies without
    it and ends with a warning.

  Network sources cannot use VSS.
- **File versions** (optional, per job): before each copy, destination files that robocopy
  would overwrite or (in Mirror) delete are moved to
  `Destination\_AGS-Copy-Versions\<yyyy-MM-dd_HHmmss>\`, keeping their relative path.
  - A `robocopy /L` pass decides what to move.
  - Version folders older than the retention (days, 0 = forever) are deleted after each run.
  - The versions folder is always excluded from robocopy, so a mirror never purges it.
- **HTML reports**: every run writes a page to `%ProgramData%\AGS-Copy\reports\<job>\`, and
  an `index.html` shows all jobs.
  - A run page has outcome, duration, speed and files copied, skipped and failed. Errors are
    listed with path, Windows code and a plain explanation. It also lists files moved to the
    versions and items a mirror deleted.
  - The index has, for each job, the last successful backup and the last 30 runs as coloured
    squares linking to their pages.
  - The GUI opens them from *Report* (main window) and *Open report* (history).
- **Two modes**:
  - *Copy* (`/E`) never deletes anything.
  - *Mirror* (`/MIR`) deletes destination items the source no longer has. It is shown in red
    and needs an explicit confirmation to save.
- **Network paths** (`\\server\share`) with their own user and password. The service connects
  with `WNetAddConnection2` and needs no mapped drive. Passwords are encrypted with DPAPI and
  bound to the service account: they never leave the service, and the GUI only learns that one
  is stored.
- **Speed**:
  - *Automatic* (default): when a run starts the service measures free CPUs, CPU load
    and available RAM. From those it picks processes and threads (`AutoTune`): several threads
    per free core, more over the network, up to a quarter of the free RAM. The run's history
    records the values it used.
  - *Threads* `/MT:n` (1-128) inside each robocopy process, when set by hand.
  - *Processes*: up to 16 robocopy processes side by side, one per top-level folder of the
    source, each with its own threads.
  - Optional `/J` for very large files.
- **Scheduling** in a Windows service: once, daily, weekly, monthly or every N minutes. Runs
  missed while the computer was off can be caught up at the next start.
- **Tray icon** for every user, unelevated. Its colour shows the state (idle, copying, failed,
  paused, service down). It shows a notification when a job ends, and its menu runs jobs and
  opens the configuration.
- **History and logs** per job: the last 200 runs, with robocopy's full log and report.
- **Watchdog**: a second service, `AGS-Copy-Watchdog`, checks the main one every minute. It
  starts it when it has been stopped for two checks in a row, and kills and restarts it when it
  is running but has not answered on its pipe for three checks in a row (or is stuck starting
  or stopping). It does nothing while a Windows Installer setup runs or when `AGS-Copy` is set to
  *Disabled*. To stop AGS-Copy on purpose, stop the watchdog first.
- **Updates**: at every start, the configuration window and the tray icon ask GitHub for the
  latest release. When it is newer, the window shows a banner and the tray a notification and a
  menu entry. *Update now* downloads the `.msi`, runs it (Windows asks for an administrator) and
  starts the tray icon again. Offline computers just skip the check.

## Architecture

```
AGS-Copy.exe (WPF, elevated)  ──┐
AGS-Copy.exe --tray (unelevated)├── named pipe "AgsCopy.Service.v1" ──► AgsCopy.Service.exe
AGS-Copy.exe --run <job id>   ──┘   JSON lines, one request per connection     (Windows service,
                                                                                LocalSystem)
                                                                                  │
                                                          %ProgramData%\AGS-Copy  ├─ jobs.json
                                                          (SYSTEM + Administrators├─ history\<job>.json
                                                           only)                  └─ logs\<job>\*.log
```

| Project | Content |
|---|---|
| `src/AgsCopy.Core` | Job model, robocopy command builder, output parsers, preview, parallel plan, scheduling, DPAPI and network shares, pipe protocol, texts (`Loc.Strings.cs`) |
| `src/AgsCopy.Service` | Worker service: job store, history, executor, scheduler, pipe server |
| `src/AgsCopy.Watchdog` | Worker service that starts or restarts `AGS-Copy` when it is down or hung |
| `src/AgsCopy.App` | WPF GUI: job list, editor, history, tray |
| `tests/AgsCopy.Core.Tests` | xUnit tests of the Core logic (they also run on Linux) |

### Points worth knowing

- **Robocopy output is translated** by Windows ("New File" becomes "Nuovo file"). The parsers
  never read the labels. A line is a number, a tab and an absolute path (`/FP /BYTES`). Paths
  under the source are copies and paths under the destination are extras. The final table is
  read by position.
- **Parallel runs** have one *root* pass and one pass per top-level folder:
  - The root pass copies the root files. In Mirror mode it also purges destination folders the
    source does not have.
  - It excludes the source folders with `/XD`, so it never touches the folders the per-folder
    passes are copying.
  - If the command line would grow past 30,000 characters (thousands of folders), the job falls
    back to a single process.
- **Permissions**: everyone logged on can read the status (the tray). Every other request
  (edit, run, simulate, read logs) needs an elevated administrator. The service checks this on
  the process token at the other end of the pipe.
- Two shares of the **same server with different accounts** are a Windows limit (error 1219).
  Use the same account, or reach one of them by IP address.

## Build

The .NET 8 SDK is not needed on the host. `build/docker.sh` runs `dotnet` in the official
container:

```bash
build/docker.sh build -c Release
build/docker.sh test tests/AgsCopy.Core.Tests
build/publish.sh            # → publish/AGS-Copy (self-contained win-x64)
```

The MSI is built with WiX 5, which runs only on Windows: `build\msi.ps1 [-Version x.y.z]`
gives `publish\AGS-Copy-<version>.msi`. On Windows with the SDK: `dotnet build`, `dotnet test`.

### GitHub Actions

`.github/workflows/build.yml` runs on every push and pull request:

- tests on Linux;
- the MSI on Windows, kept as a workflow artifact.

A `v1.2.3` tag also publishes a GitHub release named after it, with the MSI attached. That
release is what installed copies find when they look for updates. To release:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

## Install

### MSI (recommended)

Run `AGS-Copy-<version>.msi`. It installs per machine and does five things:

- installs AGS-Copy in `C:\Program Files\AGS-Copy`;
- creates and starts the `AGS-Copy` service and the `AGS-Copy-Watchdog` service (automatic
  start, restart on failure);
- adds the tray icon to every user's logon (it appears at the next logon);
- adds the Start menu entry;
- adds AGS-Copy to *Apps & features*, from where it can be uninstalled.

A newer MSI upgrades the installation in place. Jobs, credentials and history live in
`%ProgramData%\AGS-Copy`, outside the package: upgrades and removals leave them there.

Unattended, or through Group Policy / Intune:

```bat
msiexec /i AGS-Copy-0.2.0.msi /qn
msiexec /x AGS-Copy-0.2.0.msi /qn
```

The package is not code-signed yet: SmartScreen may warn the first time.

### PowerShell scripts

`publish/AGS-Copy` (from `build/publish.sh`) also holds `install.ps1` and `uninstall.ps1`, which
install the same services without an MSI. `uninstall.ps1 -RemoveData` also deletes the jobs and
history.

## Status

This is the first version. The Core logic is covered by unit tests. The GUI, the services, the
MSI and robocopy itself have not been tried on Windows yet.

Planned next:

- an email report after each run;
- dated backup sets with retention;
- scripts before and after a job;
- code signing of the MSI.
