namespace AudioStreamer.Engine.Configuration;

public sealed record EngineSettings(int SchemaVersion, long Revision, Guid NodeId, string NodeName)
{
    public const int CurrentSchemaVersion = 1;
    public static EngineSettings CreateDefault(string nodeName) => new(CurrentSchemaVersion, 1, Guid.NewGuid(), nodeName);
    public bool IsValid => SchemaVersion == CurrentSchemaVersion && Revision > 0 && NodeId != Guid.Empty &&
                           !string.IsNullOrWhiteSpace(NodeName) && NodeName.Length <= 80;
}

public interface IConfigurationStore
{
    ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken);
}
