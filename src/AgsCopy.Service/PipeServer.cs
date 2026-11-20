using System.ComponentModel;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgsCopy.Core;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace AgsCopy.Service;

/// <summary>Serves the GUI and the tray over the named pipe.</summary>
public sealed class PipeServer(
    JobStore store,
    HistoryStore history,
    ReportStore reports,
    ServiceSettings settings,
    JobExecutor executor,
    Scheduler scheduler,
    ILogger<PipeServer> log) : BackgroundService
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    Pipe.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception e)
            {
                log.LogError(e, "Cannot create the pipe");
                await Task.Delay(5000, ct);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            _ = Task.Run(() => HandleAsync(server, ct), ct);
        }
    }

    /// <summary>
    /// Everyone logged on interactively may connect (the tray runs unelevated); what each caller
    /// may do is decided per request.
    /// </summary>
    private static PipeSecurity Security()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return s;
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using var _ = server;
        var response = new PipeResponse();
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 65536, leaveOpen: true);
            var line = await reader.ReadLineAsync(ct);
            if (line == null) return;
            var request = JsonSerializer.Deserialize<PipeRequest>(line, Json.Options) ?? throw new ServiceException("Invalid request");

            if (!Commands.IsReadOnly(request.Command))
            {
                if (!ClientIsAdministrator(server)) throw new ServiceException(Loc.Tl(request.Lang, "err.admin"));
                RememberLanguage(request.Lang);
            }

            var payload = await DispatchAsync(request, ct);
            response.Ok = true;
            response.Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, Json.Options);
        }
        catch (ServiceException e)
        {
            response.Error = e.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            log.LogError(e, "Request failed");
            response.Error = e.Message;
        }

        try
        {
            var writer = new StreamWriter(server, new UTF8Encoding(false), 65536, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json.Options));
            await writer.FlushAsync(ct);
            server.WaitForPipeDrain();
        }
        catch (IOException)
        {
            // The client went away.
        }
    }

    private async Task<object?> DispatchAsync(PipeRequest req, CancellationToken ct)
    {
        var lang = req.Lang;
        T P<T>() => req.Payload is { } p ? p.Deserialize<T>(Json.Options) ?? throw new ServiceException("Missing payload") : throw new ServiceException("Missing payload");
        CopyJob Job(Guid id) => store.Get(id) ?? throw new ServiceException(Loc.Tl(lang, "err.nojob"));

        switch (req.Command)
        {
            case Commands.Status:
                return Status();

            case Commands.ListJobs:
                return store.All().Select(JobStore.Sanitize).ToList();

            case Commands.History:
                return history.List(P<IdRequest>().Id, 100);

            case Commands.GetJob:
                return JobStore.Sanitize(Job(P<IdRequest>().Id));

            case Commands.SaveJob:
            {
                var job = P<CopyJob>();
                var errors = JobValidator.Validate(job, lang);
                if (errors.Count > 0) throw new ServiceException(string.Join(Environment.NewLine, errors));
                return JobStore.Sanitize(store.Save(job, lang));
            }

            case Commands.DeleteJob:
            {
                var id = P<IdRequest>().Id;
                executor.Cancel(id);
                store.Delete(id);
                history.DeleteJob(id);
                reports.DeleteJob(id);
                return null;
            }

            case Commands.Preview:
            {
                var job = P<CopyJob>();
                return await executor.PreviewAsync(job, store.Get(job.Id), lang, ct);
            }

            case Commands.RunJob:
                if (!executor.TryStart(Job(P<IdRequest>().Id), RunTrigger.Manual))
                    throw new ServiceException(Loc.Tl(lang, "err.running"));
                return null;

            case Commands.CancelJob:
                executor.Cancel(P<IdRequest>().Id);
                return null;

            case Commands.ReadLog:
            {
                var r = P<LogRequest>();
                return executor.ReadLog(r.JobId, r.RunId, lang);
            }

            case Commands.TestPath:
            {
                var r = P<PathTestRequest>();
                return executor.TestPath(r, store.Get(r.JobId), lang);
            }

            case Commands.RunReport:
            {
                var r = P<LogRequest>();
                return new ReportResponse { Html = reports.ReadRun(r.JobId, r.RunId) };
            }

            case Commands.ReportBundle:
                return new ReportBundleResponse { Files = reports.Bundle() };

            case Commands.PauseScheduler:
                scheduler.Paused = P<BoolRequest>().Value;
                return null;

            default:
                throw new ServiceException("Unknown command: " + req.Command);
        }
    }

    /// <summary>Reports are written in the language the administrators use.</summary>
    private void RememberLanguage(string? lang)
    {
        if (lang == null || !Loc.Languages.Contains(lang) || lang == settings.Language) return;
        lock (settings)
        {
            settings.Language = lang;
            settings.Save();
        }
        reports.RebuildIndex();
    }

    private ServiceStatus Status()
    {
        var status = new ServiceStatus { Version = Version, SchedulerPaused = scheduler.Paused };
        foreach (var job in store.All())
        {
            var running = executor.Running(job.Id);
            status.Jobs.Add(new JobStatus
            {
                Id = job.Id,
                Name = job.Name,
                Enabled = job.Enabled,
                Mode = job.Mode,
                Source = job.Source.Path,
                Destination = job.Destination.Path,
                Schedule = job.Schedule,
                Running = running != null,
                RunStartedUtc = running?.Record.StartUtc,
                ProgressFiles = running?.Files ?? 0,
                ProgressBytes = running?.Bytes ?? 0,
                NextRunLocal = scheduler.NextRun(job),
                LastRun = running == null ? history.Last(job.Id) : history.List(job.Id, 2).Skip(1).FirstOrDefault(),
            });
        }
        return status;
    }

    /// <summary>
    /// Whether the process at the other end runs elevated as an administrator. Looking at its
    /// process token avoids impersonating the client.
    /// </summary>
    private bool ClientIsAdministrator(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid)) throw new Win32Exception();
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process.IsInvalid) throw new Win32Exception();
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token)) throw new Win32Exception();
            using (token)
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Cannot identify the pipe client");
            return false;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
