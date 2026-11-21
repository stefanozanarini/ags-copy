# AGS-Copy

[![build](https://github.com/stefanozanarini/ags-copy/actions/workflows/build.yml/badge.svg)](https://github.com/stefanozanarini/ags-copy/actions/workflows/build.yml)

Scheduled copies and backups for Windows. Local folders and Windows shares are copied with
**robocopy**, wherever it can do the job. **SFTP** servers work too, as source or destination,
through AGS-Copy's own copy engine. The interface is in Italian (default) and English.

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
- **SFTP** sources and destinations (`sftp://server[:port]/path`), with a password or an
  OpenSSH/PEM private key (its passphrase in the password field), stored encrypted like the
  Windows passwords. *Check access* records the server's host key fingerprint (SHA256); runs
  accept only that key and stop with a clear error if the server presents another one.
- **Compression** (option, per job), of the files worth it: photos, videos, archives, PDFs and
  Office documents, already compressed, are left as they are (`Compression.cs`).
  - NTFS destination (local disk or Windows share): robocopy copies as usual, then the copies
    are compressed with Windows (NTFS) compression. They stay ordinary files.
  - Any other destination (a NAS without NTFS compression, SFTP): each such file is written as
    `name.gz` while it is copied, so no temporary space is needed on the PC. 7-Zip and Windows 11
    Explorer open them.
- **Progress bar** in the job list while a job runs: percentage, bytes and files done out of the
  total, and an estimate of the time left. Robocopy jobs first run a quick `robocopy /L` pass for
  the totals (the bar moves without a percentage meanwhile); the same pass tells what versions
  must keep. The tray tooltip shows the percentage.
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
| `src/AgsCopy.Core` | Job model, robocopy command builder, output parsers, preview, parallel plan, scheduling, DPAPI and network shares, pipe protocol, copy engine (`Sync/`), texts (`Loc.Strings.cs`) |
| `src/AgsCopy.Service` | Worker service: job store, history, executor, scheduler, pipe server, SFTP (SSH.NET), NTFS compression |
| `src/AgsCopy.Watchdog` | Worker service that starts or restarts `AGS-Copy` when it is down or hung |
| `src/AgsCopy.App` | WPF GUI: job list, editor, history, tray |
| `tests/AgsCopy.Core.Tests` | xUnit tests of the Core logic (they also run on Linux) |

### Points worth knowing

- **Two copy engines.** Robocopy does every Windows-to-Windows job. Jobs with an SFTP endpoint,
  or with compression on a destination without NTFS compression, go to AGS-Copy's own engine
  (`Core/Sync`). Its rules are the same:
  - the source is reached only through `ISourceStore` (list and read);
  - Copy never deletes, and Mirror deletes extras or moves them to the versions folder;
  - files are written under a temporary name and moved in place;
  - files are compared by time (2 s tolerance) and, for plain files, size.

  It keeps dates and times but not Windows permissions and attributes. Extra robocopy arguments
  do not apply to it.

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

The icon (`src/AgsCopy.App/AGS-Copy.ico`, `Tray.png`) is drawn from the AGS Media mark by
`python3 assets/make_icon.py` (Pillow); the result is committed, so builds do not need Python.

`build/publish.sh` also makes `publish/AGS-Copy-Portable.exe`. The MSI is built with WiX 5,
which runs only on Windows: `build\package.ps1 [-Version x.y.z]` gives
`publish\AGS-Copy-<version>.msi` and `publish\AGS-Copy-Portable-<version>.exe`. On Windows with the SDK: `dotnet build`, `dotnet test`.

### GitHub Actions

`.github/workflows/build.yml` runs on every push and pull request:

- tests on Linux;
- the MSI and the portable exe on Windows, kept as workflow artifacts.

A `v1.2.3` tag also publishes a GitHub release named after it, with both attached. That
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

### Portable

`AGS-Copy-Portable-<version>.exe` is one file that needs no installation: copy it anywhere
(a USB stick too) and run it. It asks for administrator rights, like the installed version.

- The service's engine runs inside the window, on a pipe private to the process. Jobs, copies,
  summary, layout preview, VSS, versions and reports work the same.
- Nothing runs in the background: scheduled jobs start only while the window is open, there is
  no tray icon and no watchdog. Closing the window stops running copies (it asks first).
- Jobs, history, logs and reports go to `AGS-Copy-Data` next to the exe, so they travel with it.
  If that folder is read-only they go to `%LocalAppData%\AGS-Copy-Portable`.
- Stored network passwords are encrypted for the Windows user and computer that saved them: on
  another PC they must be typed again.
- When a newer version is out, *Update now* opens the release page to download the new exe.

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

## License

AGS-Copy is free software, released under the GNU Affero General Public License v3.0
(AGPL-3.0); see [LICENSE](LICENSE). Copyright © AGS Media.

It uses [SSH.NET](https://github.com/sshnet/SSH.NET) (MIT) for SFTP.
