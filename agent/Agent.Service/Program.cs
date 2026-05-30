using Agent.Service;
using Agent.Service.Services;

var builder = Host.CreateApplicationBuilder(args);

// Register Windows Service Lifetime
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BarisTechnicalServiceAgent";
});

// Register Custom Services
builder.Services.AddSingleton<RustDeskManager>();
builder.Services.AddSingleton<TunnelHandler>();
builder.Services.AddSingleton<SocketClient>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
