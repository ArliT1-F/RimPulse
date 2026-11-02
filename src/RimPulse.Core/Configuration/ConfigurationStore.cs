using System.Text.Json;
using RimPulse.Core.Diagnostics;

namespace RimPulse.Core.Configuration;

public enum ConfigurationLoadStatus
{
    /// <summary>An existing configuration file was read successfully.</summary>
    Loaded,

    /// <summary>No configuration existed yet; defaults are in use.</summary>
    DefaultsCreated,

    /// <summary>The file was invalid JSON. It was moved aside and defaults are in use.</summary>
    RecoveredFromCorruptFile,

    /// <summary>The file existed but could not be read or understood; defaults are in use.</summary>
    DefaultsUsedAfterError,
}

public sealed record ConfigurationLoadResult(
    RimPulseConfiguration Configuration,
    ConfigurationLoadStatus Status,
    string Message);

/// <summary>
/// JSON configuration persistence. Loads tolerate a missing or corrupt file (a corrupt file is moved
/// aside rather than deleted) and saves are atomic so an interrupted write cannot destroy the config.
/// </summary>
public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _filePath;
    private readonly IDiagnosticLog _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigurationStore(string filePath, IDiagnosticLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A configuration file path is required.", nameof(filePath));
        }

        _filePath = filePath;
        _log = log ?? NullDiagnosticLog.Instance;
    }

    public string FilePath => _filePath;

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            _log.Info("Configuration", $"No configuration file at '{_filePath}'; starting with defaults.");
            return new ConfigurationLoadResult(
                new RimPulseConfiguration(),
                ConfigurationLoadStatus.DefaultsCreated,
                "No configuration file existed yet; defaults are in use.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Configuration", $"Could not read configuration file '{_filePath}'.", ex);
            return new ConfigurationLoadResult(
                new RimPulseConfiguration(),
                ConfigurationLoadStatus.DefaultsUsedAfterError,
                $"Configuration could not be read: {ex.Message}");
        }

        RimPulseConfiguration? configuration;

        if (string.IsNullOrWhiteSpace(json))
        {
            _log.Warning("Configuration", $"Configuration file '{_filePath}' is empty; defaults are in use.");
            return new ConfigurationLoadResult(
                new RimPulseConfiguration(),
                ConfigurationLoadStatus.DefaultsUsedAfterError,
                "The configuration file was empty; defaults are in use.");
        }

        try
        {
            configuration = JsonSerializer.Deserialize<RimPulseConfiguration>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            var backupPath = TryQuarantineCorruptFile();
            _log.Error(
                "Configuration",
                backupPath is null
                    ? $"Configuration file '{_filePath}' is not valid JSON; defaults are in use."
                    : $"Configuration file '{_filePath}' is not valid JSON; it was moved to '{backupPath}' and defaults are in use.",
                ex);

            return new ConfigurationLoadResult(
                new RimPulseConfiguration(),
                ConfigurationLoadStatus.RecoveredFromCorruptFile,
                backupPath is null
                    ? "The configuration file was corrupt; defaults are in use."
                    : $"The configuration file was corrupt and was moved to '{backupPath}'.");
        }

        if (configuration is null)
        {
            _log.Warning("Configuration", $"Configuration file '{_filePath}' contained no content; defaults are in use.");
            return new ConfigurationLoadResult(
                new RimPulseConfiguration(),
                ConfigurationLoadStatus.DefaultsUsedAfterError,
                "The configuration file was empty; defaults are in use.");
        }

        configuration.Normalize();

        if (configuration.SchemaVersion > RimPulseConfiguration.CurrentSchemaVersion)
        {
            _log.Warning(
                "Configuration",
                $"Configuration schema {configuration.SchemaVersion} is newer than the supported schema {RimPulseConfiguration.CurrentSchemaVersion}. Unknown settings are ignored.");
        }

        _log.Info("Configuration", $"Loaded configuration from '{_filePath}'.");
        return new ConfigurationLoadResult(configuration, ConfigurationLoadStatus.Loaded, "Configuration loaded.");
    }

    public async Task SaveAsync(RimPulseConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Serialize a stable snapshot under the same lock as the write. UI selection changes must not
            // mutate a list while an earlier save is serializing it.
            var snapshot = System.Text.Json.JsonSerializer.SerializeToNode(configuration, SerializerOptions)!
                .Deserialize<RimPulseConfiguration>(SerializerOptions)!;
            snapshot.SchemaVersion = RimPulseConfiguration.CurrentSchemaVersion;
            snapshot.Normalize();

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var temporaryPath = $"{_filePath}.tmp";

            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);

            _log.Debug("Configuration", $"Saved configuration to '{_filePath}'.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string? TryQuarantineCorruptFile()
    {
        var backupPath = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        try
        {
            File.Move(_filePath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Configuration", $"Could not move the corrupt configuration file aside: {ex.Message}");
            return null;
        }
    }
}
