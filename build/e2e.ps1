# End-to-end test on a real Windows machine (the GitHub workflow runs it on windows-latest,
# as administrator). Installs the MSI, drives the service over its pipe the way the GUI does,
# checks the results on disk, then uninstalls.
#   build\e2e.ps1 -Msi publish\AGS-Copy-1.2.3.msi
param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference = 'Stop'

$work = 'C:\agscopy-e2e'
$src = "$work\src"
$dst = "$work\dst"
$failures = [System.Collections.Generic.List[string]]::new()

function Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Check($ok, $text) {
    if ($ok) { Write-Host "  ok   $text" -ForegroundColor Green }
    else { Write-Host "  FAIL $text" -ForegroundColor Red; $failures.Add($text) }
}

# One JSON line in, one JSON line out, like PipeClient.
function Call([string]$Command, $Payload = $null, [switch]$AllowError) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'AgsCopy.Service.v1', [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(15000)
    try {
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer.WriteLine((@{ command = $Command; lang = 'en'; payload = $Payload } | ConvertTo-Json -Depth 20 -Compress))
        $writer.Flush()
        $response = $reader.ReadLine() | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
    if (-not $response.ok -and -not $AllowError) { throw "$Command failed: $($response.error)" }
    if ($AllowError) { return $response }
    return $response.payload
}

function WaitFor([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    throw "Timed out after $Seconds s waiting for: $What"
}

function JobStatus($id) { (Call 'status').jobs | Where-Object { $_.id -eq $id } }

function RunJob($id) {
    Call 'jobs.run' @{ id = $id } | Out-Null
    WaitFor { -not (JobStatus $id).running } 600 'the job to finish' | Out-Null
    return (JobStatus $id).lastRun
}

function Text($path) { [System.IO.File]::ReadAllText($path) }

Step 'Install the MSI'
$p = Start-Process msiexec.exe -ArgumentList "/i `"$((Resolve-Path $Msi).Path)`" /qn /l*v `"$env:TEMP\agscopy-install.log`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "msiexec exit code $($p.ExitCode)"
WaitFor { (Get-Service 'AGS-Copy').Status -eq 'Running' } 120 'the AGS-Copy service' | Out-Null
WaitFor { (Get-Service 'AGS-Copy-Watchdog').Status -eq 'Running' } 60 'the watchdog service' | Out-Null
WaitFor { Call 'status' } 60 'the service pipe' | Out-Null
$status = Call 'status'
Check ($status.version.Length -gt 0) "service answers on its pipe (version $($status.version))"
Check (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') 'Run key present'
Check ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').'AGS-Copy' -like '*--tray*') 'tray started at logon'

Step 'Prepare the source'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$src\sub" | Out-Null
$text = ('AGS-Copy end-to-end test line. ' * 40 + "`r`n") * 300
[System.IO.File]::WriteAllText("$src\a.txt", "version 1`r`n$text")
[System.IO.File]::WriteAllText("$src\sub\b.txt", 'b version 1')
[System.IO.File]::WriteAllBytes("$src\photo.jpg", [byte[]](1..200000 | ForEach-Object { $_ % 251 }))
[System.IO.File]::WriteAllText("$src\locked.txt", 'open while copied')

Step 'Save a mirror job with versions and compression'
$job = Call 'jobs.save' @{
    name = 'e2e'
    source = @{ path = $src }
    destination = @{ path = $dst }
    mode = 'Mirror'
    deletionConfirmed = $true
    options = @{ keepVersions = $true; versionRetentionDays = 30; compress = $true; useShadowCopy = $true; autoTune = $true }
}
Check ($job.id) "job saved ($($job.id))"

Step 'First run, with a file held open (shadow copy)'
$lock = [System.IO.File]::Open("$src\locked.txt", 'Open', 'ReadWrite', 'None')
try { $run = RunJob $job.id } finally { $lock.Dispose() }
Check ($run.outcome -eq 'Success') "outcome $($run.outcome): $($run.message)"
Check ((Text "$dst\a.txt").StartsWith('version 1')) 'a.txt copied'
Check ((Text "$dst\sub\b.txt") -eq 'b version 1') 'sub\b.txt copied'
Check ((Test-Path "$dst\locked.txt") -and (Text "$dst\locked.txt") -eq 'open while copied') 'open file copied through the shadow copy'
Check (((Get-Item "$dst\a.txt").Attributes -band [IO.FileAttributes]::Compressed) -ne 0) 'a.txt NTFS-compressed'
Check (((Get-Item "$dst\photo.jpg").Attributes -band [IO.FileAttributes]::Compressed) -eq 0) 'photo.jpg left uncompressed'
Check ($run.message -like '*NTFS*') 'report notes the NTFS compression'
$reports = Get-ChildItem -Recurse "$env:ProgramData\AGS-Copy\reports" -Filter '*.html' -ErrorAction SilentlyContinue
Check ($reports.Count -ge 2) "HTML reports written ($($reports.Count))"
Check ((Get-ChildItem "$env:ProgramData\AGS-Copy\vss" -ErrorAction SilentlyContinue).Count -eq 0) 'shadow copy link removed'

Step 'Second run: changed and deleted files go to the versions'
Start-Sleep -Seconds 2
[System.IO.File]::WriteAllText("$src\a.txt", "version 2`r`n$text")
Remove-Item "$src\sub\b.txt"
$run = RunJob $job.id
Check ($run.outcome -eq 'Success') "outcome $($run.outcome): $($run.message)"
Check ((Text "$dst\a.txt").StartsWith('version 2')) 'a.txt updated'
Check (-not (Test-Path "$dst\sub\b.txt")) 'sub\b.txt removed from the copy'
$versions = Get-ChildItem "$dst\_AGS-Copy-Versions" -Directory
Check ($versions.Count -eq 1) "one versions folder ($($versions.Count))"
Check ((Text "$($versions[0].FullName)\a.txt").StartsWith('version 1')) 'old a.txt kept in the versions'
Check ((Text "$($versions[0].FullName)\sub\b.txt") -eq 'b version 1') 'deleted b.txt kept in the versions'
Check ($run.versionsArchived -eq 2) "versionsArchived = $($run.versionsArchived)"

Step 'Restore the files as they were before the second run'
$points = Call 'restore.points' @{ id = $job.id }
Check ($points.Count -eq 2) "restore points: $($points.Count)"
$stamp = $points[1].stamp
$op = Call 'restore.start' @{ jobId = $job.id; stamp = $stamp; items = @('a.txt', 'sub'); target = "$work\restore"; overwrite = $false }
WaitFor { -not (Call 'restore.status' @{ id = $op.id }).running } 300 'the restore' | Out-Null
$r = Call 'restore.status' @{ id = $op.id }
Check ($r.errorCount -eq 0 -and -not $r.error) "restore finished: $($r.files) files, $($r.errorCount) errors $($r.error)"
Check ((Text "$work\restore\a.txt").StartsWith('version 1')) 'a.txt restored as it was'
Check ((Text "$work\restore\sub\b.txt") -eq 'b version 1') 'deleted b.txt restored'
$refused = Call 'restore.start' @{ jobId = $job.id; items = @('a.txt'); target = "$src\restore" } -AllowError
Check (-not $refused.ok -and $refused.error -like '*read-only*') 'restore into the source refused'

Step 'A job with two sources, each in its own folder'
$src2 = "$work\src2"
$multi = "$work\multi"
New-Item -ItemType Directory -Force "$src2\deep" | Out-Null
[System.IO.File]::WriteAllText("$src2\deep\c.txt", 'second source')
$bad = Call 'jobs.save' @{
    name = 'e2e-overlap'
    source = @{ path = $src; folder = 'One' }
    extraSources = @(@{ path = "$src\sub"; folder = 'Two' })
    destination = @{ path = $multi }
} -AllowError
Check (-not $bad.ok -and $bad.error -like '*overlap*') 'overlapping sources refused'
$bad = Call 'jobs.save' @{
    name = 'e2e-samefolder'
    source = @{ path = $src; folder = 'One' }
    extraSources = @(@{ path = $src2; folder = 'one' })
    destination = @{ path = $multi }
} -AllowError
Check (-not $bad.ok -and $bad.error -like '*same folder*') 'two sources in one folder refused'
$mjob = Call 'jobs.save' @{
    name = 'e2e-multi'
    source = @{ path = $src; folder = 'One' }
    extraSources = @(@{ path = $src2; folder = 'Two' })
    destination = @{ path = $multi }
    mode = 'Mirror'
    deletionConfirmed = $true
    options = @{ keepVersions = $true; versionRetentionDays = 30; useShadowCopy = $true; autoTune = $true }
}
$run = RunJob $mjob.id
Check ($run.outcome -eq 'Success') "outcome $($run.outcome): $($run.message)"
Check ((Text "$multi\One\a.txt").StartsWith('version 2')) 'first source in One'
Check ((Text "$multi\Two\deep\c.txt") -eq 'second source') 'second source in Two'
Check (-not (Test-Path "$multi\a.txt")) 'nothing copied into the destination root'
Start-Sleep -Seconds 2
[System.IO.File]::WriteAllText("$src2\deep\c.txt", 'second source, changed')
$run = RunJob $mjob.id
Check ($run.outcome -eq 'Success') "second run: $($run.outcome): $($run.message)"
Check ((Get-ChildItem "$multi\Two\_AGS-Copy-Versions" -Directory).Count -eq 1) 'versions kept in the source folder'
$points = Call 'restore.points' @{ id = $mjob.id }
Check ($points.Count -eq 2) "restore points of both sources: $($points.Count)"
$op = Call 'restore.start' @{ jobId = $mjob.id; stamp = $points[1].stamp; items = @('One/a.txt', 'Two'); target = "$work\restore-multi"; overwrite = $false }
WaitFor { -not (Call 'restore.status' @{ id = $op.id }).running } 300 'the restore' | Out-Null
Check ((Text "$work\restore-multi\Two\deep\c.txt") -eq 'second source') 'second source restored as it was'
Check ((Text "$work\restore-multi\One\a.txt").StartsWith('version 2')) 'first source restored'
$refused = Call 'restore.start' @{ jobId = $mjob.id; items = @('Two'); target = "$src2\restore" } -AllowError
Check (-not $refused.ok -and $refused.error -like '*read-only*') 'restore into the second source refused'

Step 'Two destinations; extras deleted before the copy'
$destA = "$work\destA"
$destB = "$work\destB"
$djob = Call 'jobs.save' @{
    name = 'e2e-destinations'
    source = @{ path = $src2 }
    destination = @{ path = $destA }
    extraDestinations = @(@{ path = $destB })
    mode = 'Mirror'
    deletionConfirmed = $true
    options = @{ deleteExtrasFirst = $true; autoTune = $true }
}
$run = RunJob $djob.id
Check ($run.outcome -eq 'Success') "outcome $($run.outcome): $($run.message)"
Check ((Text "$destA\deep\c.txt") -like 'second source*') 'copied into the first destination'
Check ((Text "$destB\deep\c.txt") -like 'second source*') 'copied into the second destination'
[System.IO.File]::WriteAllText("$destB\stale.txt", 'only in the destination')
New-Item -ItemType Directory -Force "$destB\stale-dir" | Out-Null
[System.IO.File]::WriteAllText("$destB\stale-dir\x.txt", 'x')
$run = RunJob $djob.id
Check ($run.outcome -eq 'Success') "second run: $($run.outcome): $($run.message)"
Check (-not (Test-Path "$destB\stale.txt") -and -not (Test-Path "$destB\stale-dir")) 'extras of the second destination deleted'
Check ($run.filesExtra -ge 2) "extras counted: $($run.filesExtra) files, $($run.dirsExtra) folders"
Check ((Text "$destB\deep\c.txt") -like 'second source*') 'second destination still complete'
$bad = Call 'jobs.save' @{
    name = 'e2e-nested-destinations'
    source = @{ path = $src2 }
    destination = @{ path = $destA }
    extraDestinations = @(@{ path = "$destA\inner" })
} -AllowError
Check (-not $bad.ok -and $bad.error -like '*overlap*') 'nested destinations refused'
$points = Call 'restore.points' @{ id = $djob.id; destination = 1 }
Check ($points.Count -ge 1) "restore points of the second destination: $($points.Count)"
$op = Call 'restore.start' @{ jobId = $djob.id; destination = 1; items = @('deep'); target = "$work\restore-destB"; overwrite = $false }
WaitFor { -not (Call 'restore.status' @{ id = $op.id }).running } 300 'the restore' | Out-Null
Check ((Text "$work\restore-destB\deep\c.txt") -like 'second source*') 'restored from the second destination'

Step 'Junctions on a share are followed, a loop is not'
$shareDir = "$work\share"
$outside = "$work\outside"
New-Item -ItemType Directory -Force $shareDir, "$outside\inner" | Out-Null
[System.IO.File]::WriteAllText("$shareDir\top.txt", 'on the share')
[System.IO.File]::WriteAllText("$outside\inner\o.txt", 'behind the junction')
cmd /c mklink /J "$shareDir\linked" "$outside" | Out-Null
cmd /c mklink /J "$shareDir\loop" "$shareDir" | Out-Null
New-SmbShare -Name 'agse2e' -Path $shareDir -ReadAccess 'Everyone' | Out-Null
try {
    $sjob = Call 'jobs.save' @{
        name = 'e2e-share'
        source = @{ path = '\\localhost\agse2e' }
        destination = @{ path = "$work\sharecopy" }
        mode = 'Mirror'
        deletionConfirmed = $true
        options = @{ skipJunctions = $true; followShareJunctions = $true; autoTune = $true }
    }
    foreach ($n in 1, 2) {
        $run = RunJob $sjob.id
        Check ($run.outcome -eq 'Success') "run $($n): $($run.outcome): $($run.message)"
        Check ((Text "$work\sharecopy\linked\inner\o.txt") -eq 'behind the junction') "run $($n): junction followed into another folder"
    }
    Check ((Text "$work\sharecopy\top.txt") -eq 'on the share') 'share content copied'
    Check (-not (Test-Path "$work\sharecopy\loop")) 'junction back to the share not followed'
    Check ($run.message -like '*loop*' -or $run.message -like '*ciclo*') 'report names the loop'
}
finally {
    Remove-SmbShare -Name 'agse2e' -Force -ErrorAction SilentlyContinue
}

Step 'The configuration window shows every page and window'
$selftestLog = "$work\selftest.log"
$gui = Start-Process "$env:ProgramFiles\AGS-Copy\AGS-Copy.exe" -ArgumentList "--selftest `"$selftestLog`"" -PassThru
if (-not $gui.WaitForExit(300000)) { $gui.Kill(); Check $false 'GUI self-test finished in time' }
$guiLog = if (Test-Path $selftestLog) { (Get-Content $selftestLog) -join "`n    " } else { '(no log)' }
Check ($gui.ExitCode -eq 0) "GUI self-test exit code $($gui.ExitCode):`n    $guiLog"

Step 'The source was never written'
Check (-not (Test-Path "$src2\restore")) 'nothing created in the second source'
Check ((Get-Item "$shareDir\loop").LinkType -eq 'Junction' -and (Get-Item "$shareDir\linked").LinkType -eq 'Junction') 'junctions in the share untouched'
Check (-not (Test-Path "$src\restore")) 'nothing created in the source'
Check (((Get-Item "$src\a.txt").Attributes -band [IO.FileAttributes]::Compressed) -eq 0) 'source not compressed'

Step 'Watchdog restarts a stopped service'
Stop-Service 'AGS-Copy' -Force
WaitFor { (Get-Service 'AGS-Copy').Status -eq 'Running' } 240 'the watchdog to restart AGS-Copy' | Out-Null
Check ((Get-Service 'AGS-Copy').Status -eq 'Running') 'AGS-Copy running again'

Step 'A job cut short by a crash of the service runs again'
$slow = "$work\slow"
$slowCopy = "$work\slowcopy"
New-Item -ItemType Directory -Force $slow | Out-Null
foreach ($d in 1..60) {
    New-Item -ItemType Directory -Force "$slow\d$d" | Out-Null
    foreach ($f in 1..500) { [System.IO.File]::WriteAllText("$slow\d$d\f$f.txt", "file $d/$f") }
}
WaitFor { Call 'status' } 120 'the service pipe' | Out-Null
$cjob = Call 'jobs.save' @{
    name = 'e2e-crash'
    source = @{ path = $slow }
    destination = @{ path = $slowCopy }
    options = @{ useShadowCopy = $true; autoTune = $false; threads = 2; processes = 1 }
}
Call 'jobs.run' @{ id = $cjob.id } | Out-Null
WaitFor { (JobStatus $cjob.id).running } 60 'the job to start' | Out-Null
Start-Sleep -Seconds 1
$svcPid = (Get-CimInstance Win32_Service -Filter "Name='AGS-Copy'").ProcessId
Stop-Process -Id $svcPid -Force
Start-Sleep -Seconds 3
$orphans = @(Get-CimInstance Win32_Process -Filter "Name='robocopy.exe'" | Where-Object { $_.CommandLine -like '*slowcopy*' })
Check ($orphans.Count -eq 0) "robocopy ended with the service ($($orphans.Count) left)"
WaitFor { @(Call 'history.list' @{ id = $cjob.id } | Where-Object { $_.trigger -eq 'Resumed' }).Count -gt 0 } 420 'the watchdog to resume the job' | Out-Null
WaitFor { -not (JobStatus $cjob.id).running } 600 'the resumed run to finish' | Out-Null
$runs = @(Call 'history.list' @{ id = $cjob.id })
$resumed = $runs | Where-Object { $_.trigger -eq 'Resumed' } | Select-Object -First 1
$cut = $runs | Where-Object { $_.trigger -eq 'Manual' } | Select-Object -First 1
Check ($resumed.outcome -eq 'Success') "resumed run: $($resumed.outcome): $($resumed.message)"
Check ($cut.outcome -eq 'Failed' -and $cut.message -like '*watchdog*') "interrupted run recorded: $($cut.message)"
Check ((Get-ChildItem $slowCopy -Recurse -File).Count -eq 30000) 'every file copied by the resumed run'

Step 'Uninstall'
$p = Start-Process msiexec.exe -ArgumentList "/x `"$((Resolve-Path $Msi).Path)`" /qn" -Wait -PassThru
Check ($p.ExitCode -eq 0) "msiexec /x exit code $($p.ExitCode)"
Check (-not (Get-Service 'AGS-Copy' -ErrorAction SilentlyContinue)) 'AGS-Copy service removed'
Check (-not (Get-Service 'AGS-Copy-Watchdog' -ErrorAction SilentlyContinue)) 'watchdog service removed'
Check (Test-Path "$env:ProgramData\AGS-Copy\jobs.json") 'jobs kept in ProgramData'

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nAll end-to-end checks passed." -ForegroundColor Green
