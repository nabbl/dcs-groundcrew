using System.Collections.Concurrent;
using System.Globalization;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;
using Grpc.Core;
using Grpc.Net.Client;
using Groundcrew.DcsGrpc.V0.Common;
using HookContract = Groundcrew.DcsGrpc.V0.Hook;
using MetadataContract = Groundcrew.DcsGrpc.V0.Metadata;
using MissionContract = Groundcrew.DcsGrpc.V0.Mission;
using NetContract = Groundcrew.DcsGrpc.V0.Net;

namespace DcsDashboard.Api.Services;

public sealed record DcsGrpcLiveSnapshot(
    bool Connected,
    string? Version,
    string? MissionName,
    string? MissionFilename,
    bool? Paused,
    double? MissionTime,
    double? Fps,
    IReadOnlyList<Player> Players,
    IReadOnlyList<ChatMessage> Chat,
    DateTimeOffset? LastUpdated,
    string? Error);

public sealed class DcsGrpcLiveService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private readonly SettingsStore _settings;
    private readonly ILogger<DcsGrpcLiveService> _logger;
    private readonly SemaphoreSlim _clientsGate = new(1, 1);
    private readonly object _eventGate = new();
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _firstSeen = new();
    private readonly ConcurrentDictionary<string, string> _slotTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ChatMessage> _chat = new();
    private ClientBundle? _clients;
    private string? _lastMissionFilename;
    private DcsGrpcLiveSnapshot _snapshot = Empty("Waiting for DCS-gRPC.");

    public DcsGrpcLiveService(SettingsStore settings, ILogger<DcsGrpcLiveService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public DcsGrpcLiveSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    public async Task SendChatAsync(string message, CancellationToken cancellationToken = default)
    {
        var text = message.Trim();
        if (text.Length is < 1 or > 500) throw new ArgumentException("Chat messages must contain between 1 and 500 characters.");
        var clients = await GetClientsAsync();
        await clients.Net.SendChatAsync(
            new NetContract.SendChatRequest { Message = text, Coalition = Coalition.All },
            deadline: DateTime.UtcNow.AddSeconds(3),
            cancellationToken: cancellationToken).ResponseAsync;
        AddChat("ADMIN", text, false);
    }

    public async Task KickPlayerAsync(uint playerId, string? reason, CancellationToken cancellationToken = default)
    {
        var clients = await GetClientsAsync();
        await clients.Net.KickPlayerAsync(
            new NetContract.KickPlayerRequest { Id = playerId, Message = ModerationReason(reason, "Removed by a Groundcrew administrator.") },
            deadline: DateTime.UtcNow.AddSeconds(3),
            cancellationToken: cancellationToken).ResponseAsync;
    }

    public async Task MoveToSpectatorsAsync(uint playerId, CancellationToken cancellationToken = default)
    {
        var clients = await GetClientsAsync();
        await clients.Net.ForcePlayerSlotAsync(
            new NetContract.ForcePlayerSlotRequest { PlayerId = playerId, Coalition = Coalition.Neutral, SlotId = "" },
            deadline: DateTime.UtcNow.AddSeconds(3),
            cancellationToken: cancellationToken).ResponseAsync;
    }

    public async Task BanPlayerAsync(uint playerId, uint durationSeconds, string? reason, CancellationToken cancellationToken = default)
    {
        if (durationSeconds is < 60 or > 31_536_000) throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Ban duration must be between one minute and one year.");
        var clients = await GetClientsAsync();
        var response = await clients.Hook.BanPlayerAsync(
            new HookContract.BanPlayerRequest { Id = playerId, Period = durationSeconds, Reason = ModerationReason(reason, "Banned by a Groundcrew administrator.") },
            deadline: DateTime.UtcNow.AddSeconds(3),
            cancellationToken: cancellationToken).ResponseAsync;
        if (!response.Banned) throw new InvalidOperationException("DCS declined the ban request.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = PollLoopAsync(stoppingToken);
        var events = EventLoopAsync(stoppingToken);
        await Task.WhenAll(poll, events);
    }

    private async Task PollLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var current = GetSnapshot();
                Publish(current with { Connected = false, Error = Describe(exception) });
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync();
        var health = await clients.Metadata.GetHealthAsync(
            new MetadataContract.GetHealthRequest(),
            deadline: DateTime.UtcNow.AddSeconds(2),
            cancellationToken: cancellationToken).ResponseAsync;
        if (!health.Alive) throw new InvalidOperationException("DCS-gRPC reported that it is not healthy.");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        var versionTask = BestEffortAsync(() => clients.Metadata.GetVersionAsync(new MetadataContract.GetVersionRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        var missionNameTask = BestEffortAsync(() => clients.Hook.GetMissionNameAsync(new HookContract.GetMissionNameRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        var missionFilenameTask = BestEffortAsync(() => clients.Hook.GetMissionFilenameAsync(new HookContract.GetMissionFilenameRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        var pausedTask = BestEffortAsync(() => clients.Hook.GetPausedAsync(new HookContract.GetPausedRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        var realTimeTask = BestEffortAsync(() => clients.Hook.GetRealTimeAsync(new HookContract.GetRealTimeRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        var playersTask = BestEffortAsync(() => clients.Net.GetPlayersAsync(new NetContract.GetPlayersRequest(), deadline: deadline, cancellationToken: cancellationToken).ResponseAsync);
        await Task.WhenAll(versionTask, missionNameTask, missionFilenameTask, pausedTask, realTimeTask, playersTask);

        var previous = GetSnapshot();
        var missionFilename = Clean(missionFilenameTask.Result?.Name) ?? previous.MissionFilename;
        if (!string.Equals(missionFilename, _lastMissionFilename, StringComparison.OrdinalIgnoreCase))
        {
            _lastMissionFilename = missionFilename;
            _slotTypes.Clear();
        }

        var players = playersTask.Result is null
            ? previous.Players
            : await BuildPlayersAsync(playersTask.Result, clients.Hook, cancellationToken);
        var latest = GetSnapshot();
        Publish(new DcsGrpcLiveSnapshot(
            true,
            Clean(versionTask.Result?.Version) ?? previous.Version,
            Clean(missionNameTask.Result?.Name) ?? previous.MissionName,
            missionFilename,
            pausedTask.Result?.Paused ?? previous.Paused,
            realTimeTask.Result?.Time ?? previous.MissionTime,
            latest.Fps,
            players,
            ReadChat(),
            DateTimeOffset.UtcNow,
            null));
    }

    private async Task<IReadOnlyList<Player>> BuildPlayersAsync(NetContract.GetPlayersResponse response, HookContract.HookService.HookServiceClient hook, CancellationToken cancellationToken)
    {
        var playerInfos = response.Players
            .Where(player => !(player.Id == 1 && string.IsNullOrWhiteSpace(player.RemoteAddress) && string.IsNullOrWhiteSpace(player.Ucid)))
            .ToList();
        var activeIds = playerInfos.Select(player => player.Id).ToHashSet();
        foreach (var id in _firstSeen.Keys.Where(id => !activeIds.Contains(id))) _firstSeen.TryRemove(id, out _);

        var unknownSlots = playerInfos.Select(player => player.Slot)
            .Where(slot => !string.IsNullOrWhiteSpace(slot) && !_slotTypes.ContainsKey(slot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var lookups = unknownSlots.Select(async slot =>
        {
            var result = await BestEffortAsync(() => hook.GetUnitTypeAsync(
                new HookContract.GetUnitTypeRequest { Id = slot },
                deadline: DateTime.UtcNow.AddSeconds(2),
                cancellationToken: cancellationToken).ResponseAsync);
            var type = Clean(result?.Type);
            if (type is not null) _slotTypes[slot] = type;
        });
        await Task.WhenAll(lookups);

        return playerInfos.Select(player => new Player(
            player.Id.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(player.Name) ? $"Player {player.Id}" : player.Name,
            CoalitionName(player.Coalition),
            SlotName(player.Slot),
            (int)Math.Min(player.Ping, int.MaxValue),
            _firstSeen.GetOrAdd(player.Id, _ => DateTimeOffset.UtcNow))).ToList();
    }

    private async Task EventLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var clients = await GetClientsAsync();
                using var call = clients.Mission.StreamEvents(new MissionContract.StreamEventsRequest(), cancellationToken: stoppingToken);
                while (await call.ResponseStream.MoveNext(stoppingToken)) HandleEvent(call.ResponseStream.Current);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
            {
                _logger.LogDebug("DCS-gRPC event stream is unavailable: {Status}", exception.Status.Detail);
            }
            catch (Exception exception) { _logger.LogWarning(exception, "DCS-gRPC event stream stopped unexpectedly."); }

            try { await Task.Delay(RetryInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void HandleEvent(MissionContract.StreamEventsResponse message)
    {
        switch (message.EventCase)
        {
            case MissionContract.StreamEventsResponse.EventOneofCase.SimulationFps:
                var current = GetSnapshot();
                Publish(current with { Fps = message.SimulationFps.Average, MissionTime = message.Time, LastUpdated = DateTimeOffset.UtcNow });
                break;
            case MissionContract.StreamEventsResponse.EventOneofCase.PlayerSendChat:
                var player = GetSnapshot().Players.FirstOrDefault(item => item.Id == message.PlayerSendChat.PlayerId.ToString(CultureInfo.InvariantCulture));
                AddChat(player?.Name ?? $"Player {message.PlayerSendChat.PlayerId}", message.PlayerSendChat.Message, false);
                break;
            case MissionContract.StreamEventsResponse.EventOneofCase.Connect:
                _firstSeen[message.Connect.Id] = DateTimeOffset.UtcNow;
                AddChat("SERVER", $"{message.Connect.Name} connected.", true);
                break;
            case MissionContract.StreamEventsResponse.EventOneofCase.Disconnect:
                var disconnected = GetSnapshot().Players.FirstOrDefault(item => item.Id == message.Disconnect.Id.ToString(CultureInfo.InvariantCulture));
                _firstSeen.TryRemove(message.Disconnect.Id, out _);
                AddChat("SERVER", $"{disconnected?.Name ?? $"Player {message.Disconnect.Id}"} disconnected.", true);
                break;
            case MissionContract.StreamEventsResponse.EventOneofCase.MissionStart:
                AddChat("SERVER", "Mission started.", true);
                break;
            case MissionContract.StreamEventsResponse.EventOneofCase.MissionEnd:
                AddChat("SERVER", "Mission ended.", true);
                break;
        }
    }

    private void AddChat(string author, string message, bool system)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_eventGate)
        {
            _chat.Enqueue(new ChatMessage(Guid.NewGuid().ToString("N"), author, message, DateTimeOffset.Now.ToString("HH:mm", CultureInfo.CurrentCulture), system));
            while (_chat.Count > 200) _chat.Dequeue();
            var current = GetSnapshot();
            Publish(current with { Chat = _chat.ToList(), LastUpdated = DateTimeOffset.UtcNow });
        }
    }

    private IReadOnlyList<ChatMessage> ReadChat()
    {
        lock (_eventGate) return _chat.ToList();
    }

    private async Task<ClientBundle> GetClientsAsync()
    {
        var settings = await _settings.GetAsync();
        var integration = settings.Integrations.FirstOrDefault(item => string.Equals(item.Id, "grpc", StringComparison.OrdinalIgnoreCase));
        var host = string.IsNullOrWhiteSpace(integration?.Host) ? "127.0.0.1" : integration.Host.Trim();
        if (host is "0.0.0.0" or "::") host = "127.0.0.1";
        var port = integration?.Port is > 0 and <= 65535 ? integration.Port.Value : 50051;
        var endpoint = new UriBuilder(Uri.UriSchemeHttp, host, port).Uri;
        if (_clients is not null && _clients.Endpoint == endpoint) return _clients;

        await _clientsGate.WaitAsync();
        try
        {
            if (_clients is not null && _clients.Endpoint == endpoint) return _clients;
            var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxReceiveMessageSize = 16 * 1024 * 1024 });
            var next = new ClientBundle(
                endpoint,
                channel,
                new MetadataContract.MetadataService.MetadataServiceClient(channel),
                new HookContract.HookService.HookServiceClient(channel),
                new NetContract.NetService.NetServiceClient(channel),
                new MissionContract.MissionService.MissionServiceClient(channel));
            var previous = _clients;
            _clients = next;
            previous?.Channel.Dispose();
            return next;
        }
        finally { _clientsGate.Release(); }
    }

    private string SlotName(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return "Spectator";
        return _slotTypes.TryGetValue(slot, out var type) ? $"{type} · Slot {slot}" : $"Slot {slot}";
    }

    private static string CoalitionName(Coalition coalition) => coalition switch
    {
        Coalition.Blue => "Blue",
        Coalition.Red => "Red",
        _ => "Spectator"
    };

    private static async Task<T?> BestEffortAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return await action(); }
        catch { return null; }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ModerationReason(string? value, string fallback)
    {
        var reason = Clean(value) ?? fallback;
        return reason.Length <= 240 ? reason : reason[..240];
    }
    private void Publish(DcsGrpcLiveSnapshot value) => Volatile.Write(ref _snapshot, value);
    private static DcsGrpcLiveSnapshot Empty(string error) => new(false, null, null, null, null, null, null, Array.Empty<Player>(), Array.Empty<ChatMessage>(), null, error);
    private static string Describe(Exception exception)
    {
        var message = exception is RpcException rpc && !string.IsNullOrWhiteSpace(rpc.Status.Detail) ? rpc.Status.Detail : exception.Message;
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 220 ? message : $"{message[..217]}...";
    }

    public override void Dispose()
    {
        _clients?.Channel.Dispose();
        _clientsGate.Dispose();
        base.Dispose();
    }

    private sealed record ClientBundle(
        Uri Endpoint,
        GrpcChannel Channel,
        MetadataContract.MetadataService.MetadataServiceClient Metadata,
        HookContract.HookService.HookServiceClient Hook,
        NetContract.NetService.NetServiceClient Net,
        MissionContract.MissionService.MissionServiceClient Mission);
}
