using AgsCopy.Watchdog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = Watchdog.ServiceName);
builder.Services.AddHostedService<Watchdog>();
builder.Build().Run();
