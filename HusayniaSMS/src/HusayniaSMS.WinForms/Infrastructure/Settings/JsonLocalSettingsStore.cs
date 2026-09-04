using System.Text.Json;
using System.Text.Json.Serialization;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Infrastructure.Settings;

public sealed class JsonLocalSettingsStore : ILocalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private readonly string _settingsPath;

    public JsonLocalSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
    }

    public async Task<SettingsFileLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new(SettingsFileStatus.Missing, null, "Twilio setup has not been saved.");
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<PersistedSettingsV1>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                return new(SettingsFileStatus.Corrupt, null,
                    "Saved local settings are corrupt.");
            }

            if (settings.SchemaVersion != 1)
            {
                return new(SettingsFileStatus.UnsupportedVersion, null,
                    "Saved Twilio setup uses an unsupported version.");
            }

            var setupFields = new[]
            {
                settings.AccountSid,
                settings.SenderNumber,
                settings.ProtectedAuthToken
            };
            if (setupFields.Any(value => value is null))
            {
                return new(SettingsFileStatus.Corrupt, null,
                    "Saved local settings are corrupt.");
            }

            var hasAnySetupField = setupFields.Any(value => !string.IsNullOrWhiteSpace(value));
            var hasCompleteSetup = setupFields.All(value => !string.IsNullOrWhiteSpace(value));
            if ((hasAnySetupField && !hasCompleteSetup) ||
                (settings.SenderMode is not null &&
                    (!hasCompleteSetup || !Enum.IsDefined(settings.SenderMode.Value))) ||
                (settings.LastCsvPath is not null &&
                    string.IsNullOrWhiteSpace(settings.LastCsvPath)))
            {
                return new(SettingsFileStatus.Corrupt, null,
                    "Saved local settings are corrupt.");
            }

            return new(SettingsFileStatus.Loaded, settings, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new(SettingsFileStatus.AccessDenied, null,
                "Access to saved Twilio setup was denied.");
        }
        catch (JsonException)
        {
            return new(SettingsFileStatus.Corrupt, null,
                "Saved Twilio setup is corrupt. Re-enter and save it.");
        }
        catch (IOException)
        {
            return new(SettingsFileStatus.IoFailure, null,
                "Saved Twilio setup could not be read.");
        }
    }

    public async Task<SettingsFileSaveResult> SaveAsync(
        PersistedSettingsV1 settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return new(SettingsFileSaveStatus.Saved, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new(SettingsFileSaveStatus.AccessDenied,
                "Access to the local settings folder was denied.");
        }
        catch (IOException)
        {
            return new(SettingsFileSaveStatus.IoFailure,
                "Twilio setup could not be saved.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; the authoritative settings file is unchanged.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only; the authoritative settings file is unchanged.
            }
        }
    }
}
