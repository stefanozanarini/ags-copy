using AgsCopy.Core;
using AgsCopy.Service;

DataDirectory.Ensure();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);

builder.Services.AddSingleton(_ => ServiceSettings.Load());
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobExecutor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
