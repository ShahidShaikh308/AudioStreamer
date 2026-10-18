using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AudioStreamer.ConsoleClient");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await using var client = new PipeClient(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PipeClient>());
try
{
    var hello = await client.ConnectAsync(shutdown.Token);
    logger.LogInformation("Handshake complete. Service={ServiceVersion} Engine={EngineVersion} Schema={Major}.{Minor}",
        hello.ServiceVersion, hello.EngineVersion, hello.SelectedVersion.Major, hello.SelectedVersion.Minor);

    var pingEnvelope = await client.RequestAsync("Ping", new PingPayload(DateTimeOffset.UtcNow), shutdown.Token);
    var ping = IpcSerializer.DeserializePayload<IpcOperationResult<PingResult>>(pingEnvelope.Payload);
    logger.LogInformation("Ping result={Result} ServiceState={ServiceState}", ping.Result, ping.Data?.ServiceState);

    var versionEnvelope = await client.RequestAsync("GetVersion", new EmptyPayload(), shutdown.Token);
    var versions = IpcSerializer.DeserializePayload<IpcOperationResult<VersionSnapshot>>(versionEnvelope.Payload);
    logger.LogInformation("Versions: Service={Service} Engine={Engine} IPC={Ipc}",
        versions.Data?.ServiceVersion, versions.Data?.EngineVersion, versions.Data?.IpcVersion);

    var statusEnvelope = await client.RequestAsync("GetStatus", new EmptyPayload(), shutdown.Token);
    var status = IpcSerializer.DeserializePayload<IpcOperationResult<StatusSnapshot>>(statusEnvelope.Payload);
    logger.LogInformation("Node state={State} revision={Revision}", status.Data?.State, status.Data?.Revision);

    var diagnosticsEnvelope = await client.RequestAsync("GetDiagnostics", new EmptyPayload(), shutdown.Token);
    var diagnostics = IpcSerializer.DeserializePayload<IpcOperationResult<DiagnosticsSnapshot>>(diagnosticsEnvelope.Payload);
    logger.LogInformation("Diagnostics result={Result} state={State} health={Health} memory={Memory} counters={Counters}",
        diagnostics.Result, diagnostics.Data?.State, diagnostics.Data?.Health, diagnostics.Data?.WorkingSetBytes,
        diagnostics.Data?.Statistics?.Count);

    await client.RequestAsync("Subscribe", new SubscribeRequest(), shutdown.Token);
    var snapshotEnvelope = await client.RequestAsync("GetSnapshot", new EmptyPayload(), shutdown.Token);
    var snapshot = IpcSerializer.DeserializePayload<IpcOperationResult<NodeSnapshot>>(snapshotEnvelope.Payload);
    logger.LogInformation("Snapshot synchronized. Node={NodeId} Name={NodeName} EventWatermark={Watermark}",
        snapshot.Data?.NodeId, snapshot.Data?.NodeName, snapshot.Data?.EventWatermark);

    if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
    {
        logger.LogInformation("IPC smoke check passed.");
        return;
    }

    logger.LogInformation("Subscribed to service events. Press Ctrl+C to disconnect.");
    await foreach (var message in client.ReadEventsAsync(shutdown.Token).ConfigureAwait(false))
        logger.LogInformation("Event={MessageType} Sequence={Sequence} Epoch={Epoch}",
            message.MessageType, message.EventSequence, message.ConnectionEpoch);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    logger.LogInformation("Console client shutting down.");
}
catch (Exception exception)
{
    logger.LogError(exception, "Console client failed.");
    Environment.ExitCode = 1;
}
