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
            // Clear old log file on application startup to keep logs fresh and relevant
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarisServiceSuite");
                string path = System.IO.Path.Combine(folder, "debug_log.txt");
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch { }

            ApplicationConfiguration.Initialize();

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppDomain.CurrentDomain.BaseDirectory
            });

            // Register Custom Services
            builder.Services.AddSingleton<TunnelHandler>();
            builder.Services.AddSingleton<ScreenStreamer>();
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
