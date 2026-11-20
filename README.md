# AGS-Copy

Windows copies and backups with **robocopy** as the engine, as a replacement for Uranium Backup.
The interface is in Italian (default) and English.

- **Guided job editor** in seven steps: general, source, destination, mode, options, schedule,
  summary. A job is saved only from the **summary** step. That step describes in plain words
  what is copied, where, and what is deleted. It also runs a **simulation** (`robocopy /L`) that
  lists the files to copy and the files and folders to delete.
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
- **History and logs** per job: the last 200 runs, with robocopy's full log.

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
build/installer.sh          # → publish/AGS-Copy-Setup-<version>.exe (NSIS, in a Debian container)
```

On Windows with the SDK: `dotnet build`, `dotnet test`.

## Install

### Setup program (recommended)

Run `AGS-Copy-Setup-<version>.exe`. It asks for administrator rights, then lets you choose
Italian or English and the folder.

The setup does five things:

- installs AGS-Copy in `C:\Program Files\AGS-Copy`;
- creates and starts the `AGS-Copy` service (automatic start, restart on failure);
- adds the tray icon to every user's logon;
- adds the Start menu entry;
- adds AGS-Copy to *Apps & features*, from where it can be uninstalled.

Running a newer setup updates the installation and keeps jobs, credentials and history. When
AGS-Copy is uninstalled, it asks whether to delete them too; the default is to keep them.

Unattended:

```bat
AGS-Copy-Setup-0.1.0.exe /S
AGS-Copy-Setup-0.1.0.exe /S /D=D:\Programs\AGS-Copy
"C:\Program Files\AGS-Copy\uninstall.exe" /S
```

The silent uninstall keeps jobs, credentials and history.

### PowerShell scripts

`publish/AGS-Copy` also holds `install.ps1` and `uninstall.ps1`, which do the same as the setup
without a wizard. `uninstall.ps1 -RemoveData` also deletes the jobs and history.

## Status

This is the first version. The Core logic is covered by unit tests. The GUI, the service and
robocopy itself have not been tried on Windows yet.

Planned next:

- an email report after each run;
- dated backup sets with retention;
- scripts before and after a job;
- an MSI package for Group Policy deployment (WiX builds only on Windows).
