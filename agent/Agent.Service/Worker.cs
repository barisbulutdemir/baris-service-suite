using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agent.Service.Services;

namespace Agent.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly SocketClient _socketClient;

    public Worker(ILogger<Worker> logger, SocketClient socketClient)
    {
        _logger = logger;
        _socketClient = socketClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Baris Technical Service Suite Agent...");
        try
        {
            await _socketClient.StartAsync(stoppingToken);
            
            // Keep service alive while connection client runs
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            _logger.LogCritical(ex, "An unhandled exception occurred in the agent service.");
        }
        finally
        {
            await _socketClient.StopAsync();
            _logger.LogInformation("Baris Technical Service Suite Agent stopped.");
        }
    }
}

