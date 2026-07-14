using DcsDashboard.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DcsDashboard.Api.Services;

public sealed class SnapshotBroadcastService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHubContext<DashboardHub> _hub;
    public SnapshotBroadcastService(IServiceProvider services, IHubContext<DashboardHub> hub) { _services = services; _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = _services.CreateScope();
                var snapshot = await scope.ServiceProvider.GetRequiredService<SnapshotService>().GetAsync();
                await _hub.Clients.All.SendAsync("snapshot", snapshot, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
