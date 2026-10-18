using AudioStreamer.AudioAgent.Transport;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

using var instanceGate = new Semaphore(initialCount: 1, maximumCount: 1,
    $@"Local\AudioStreamer.AudioAgent.v2.{Process.GetCurrentProcess().SessionId}");
if (!instanceGate.WaitOne(0))
{
    // The existing per-session agent remains authoritative; a duplicate exits without user interaction.
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddEventSourceLogger();
builder.Services.AddSingleton<AgentEventPublisher>();
builder.Services.AddSingleton<IAudioPlatformEventPublisher>(services => services.GetRequiredService<AgentEventPublisher>());
builder.Services.AddAgentWindowsAudio();
builder.Services.AddSingleton<IAgentAudioCommandHandler, AgentAudioCommandHandler>();
builder.Services.AddAudioAgentServer(new AgentTransportOptions(ServiceIdentity: "NT SERVICE\\AudioStreamer"));
await builder.Build().RunAsync();
