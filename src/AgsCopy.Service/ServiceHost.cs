using AgsCopy.Core;

namespace AgsCopy.Service;

/// <summary>The engine: job store, history, executor, scheduler and pipe server.</summary>
public static class ServiceHost
{
    /// <param name="windowsService">False for the portable build, which hosts the engine in the GUI process.</param>
    public static IHost Create(string[] args, bool windowsService)
    {
        DataDirectory.Ensure();
        // Copies block threads on disks and networks (shadow copies, share listings, robocopy output):
        // with only one thread per core to start from, the pipe would wait for the pool to grow and the
        // GUI would see a service that does not answer.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));

        var builder = Host.CreateApplicationBuilder(args);
        if (windowsService) builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);

        builder.Services.AddSingleton(_ => ServiceSettings.Load());
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton<HistoryStore>();
        builder.Services.AddSingleton<ReportStore>();
        builder.Services.AddSingleton<JobExecutor>();
        builder.Services.AddSingleton<Scheduler>();
        builder.Services.AddSingleton<RestoreManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobExecutor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
        builder.Services.AddHostedService<PipeServer>();
        return builder.Build();
    }
}
