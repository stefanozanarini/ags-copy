# AGS-Copy

Windows copies and backups with **robocopy** as the engine, as a replacement for Uranium Backup.
The interface is in Italian (default) and English.

- **Guided job editor** in seven steps: general, source, destination, mode, options, schedule,
  summary. A job is saved only from the **summary** step. That step describes in plain words
  what is copied, where, and what is deleted. It also runs a **simulation** (`robocopy /L`) that
  lists the files to copy and the files and folders to delete.
- **Three modes**:
  - *Copy* (`/E`) never deletes anything.
  - *Mirror* (`/MIR`) deletes destination items the source no longer has.
  - *Move* (`/E /MOV`) deletes source files once they are copied.

  Destructive modes are shown in red and need an explicit confirmation to save.
- **Network paths** (`\\server\share`) with their own user and password. The service connects
  with `WNetAddConnection2` and needs no mapped drive. Passwords are encrypted with DPAPI and
  bound to the service account: they never leave the service, and the GUI only learns that one
  is stored.
- **Speed**:
  - *Threads* `/MT:n` (1-128) inside each robocopy process.
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
- **Move** uses `/MOV` and not `/MOVE`, because `/MOVE` would also delete the source folder of
  the job.
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

On Windows with the SDK: `dotnet build`, `dotnet test`.

## Install

1. Copy the `publish/AGS-Copy` folder to the computer.
2. In an administrator PowerShell, run `.\install.ps1`. Add `-InstallDir` for a folder other
   than `C:\Program Files\AGS-Copy`.

The script does five things:

- installs the files;
- creates and starts the `AGS-Copy` service (automatic start, restart on failure);
- adds the tray icon to every user's logon;
- adds the Start menu entry;
- starts the tray for the current user.

Running it again updates an existing installation and keeps the jobs.

To remove AGS-Copy, run `.\uninstall.ps1` from `C:\Program Files\AGS-Copy`. Jobs and history
are kept unless you add `-RemoveData`.

## Status

This is the first version. The Core logic is covered by unit tests. The GUI, the service and
robocopy itself have not been tried on Windows yet.

Planned next:

- an email report after each run;
- VSS snapshots for open files;
- dated backup sets with retention;
- scripts before and after a job;
- an MSI installer.
