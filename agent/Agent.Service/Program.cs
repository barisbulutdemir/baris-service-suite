using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Agent.Service;
using Agent.Service.Services;

namespace Agent.Service
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            var builder = Host.CreateApplicationBuilder(args);

            // Register Custom Services
            builder.Services.AddSingleton<RustDeskManager>();
            builder.Services.AddSingleton<TunnelHandler>();
            builder.Services.AddSingleton<SocketClient>();

            builder.Services.AddHostedService<Worker>();

            var host = builder.Build();

            // Start Hosted Service in background
            var cts = new CancellationTokenSource();
            var hostTask = Task.Run(() => host.StartAsync(cts.Token));

            var socketClient = host.Services.GetRequiredService<SocketClient>();

            // Run Windows Forms Application Context (System Tray)
            var trayContext = new AgentTrayContext(socketClient, cts, host, hostTask);
            Application.Run(trayContext);
        }
    }
}
