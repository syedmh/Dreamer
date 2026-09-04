namespace HusayniaSMS.Core.Settings;

public sealed class SettingsService(
    ILocalSettingsStore store,
    ISecretProtector protector,
    ISetupValidator validator) : ISettingsService
{
    private static readonly IReadOnlyList<FieldError> NoErrors = Array.Empty<FieldError>();

    public async Task<SettingsDescriptor> LoadDescriptorAsync(CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Status == SettingsFileStatus.Missing)
        {
            return new(
                string.Empty,
                TwilioSenderMode.FromPhoneNumber,
                string.Empty,
                TokenState.Missing,
                null);
        }

        if (loaded.Status != SettingsFileStatus.Loaded || loaded.Settings is null)
        {
            return new(
                string.Empty,
                TwilioSenderMode.FromPhoneNumber,
                string.Empty,
                TokenState.Unavailable,
                null);
        }

        if (!HasSavedSetup(loaded.Settings))
        {
            return new(
                string.Empty,
                TwilioSenderMode.FromPhoneNumber,
                string.Empty,
                TokenState.Missing,
                loaded.Settings.LastCsvPath);
        }

        var unprotected = protector.TryUnprotect(loaded.Settings.ProtectedAuthToken);
        var tokenState = unprotected.Succeeded && !string.IsNullOrWhiteSpace(unprotected.Plaintext)
            ? TokenState.Available
            : TokenState.Unavailable;
        return new(
            loaded.Settings.AccountSid,
            ResolveSenderMode(loaded.Settings),
            loaded.Settings.SenderNumber,
            tokenState,
            loaded.Settings.LastCsvPath);
    }

    public async Task<SaveSettingsResult> SaveAsync(
        SetupInput input,
        CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Status is SettingsFileStatus.IoFailure or SettingsFileStatus.AccessDenied)
        {
            var safeMessage = string.IsNullOrWhiteSpace(loaded.SafeMessage)
                ? "Saved Twilio setup could not be loaded."
                : loaded.SafeMessage.Trim();
            return new(
                SaveSettingsStatus.StorageFailed,
                NoErrors,
                $"{safeMessage} Settings were not changed.");
        }

        string? existingPlaintext = null;
        string? protectedToken = null;

        if (loaded.Status == SettingsFileStatus.Loaded &&
            loaded.Settings is not null &&
            HasSavedSetup(loaded.Settings))
        {
            var unprotected = protector.TryUnprotect(loaded.Settings.ProtectedAuthToken);
            if (unprotected.Succeeded && !string.IsNullOrWhiteSpace(unprotected.Plaintext))
            {
                existingPlaintext = unprotected.Plaintext;
                protectedToken = loaded.Settings.ProtectedAuthToken;
            }
        }

        var validation = validator.Validate(input, existingPlaintext is not null);
        if (!validation.IsValid)
        {
            return new(SaveSettingsStatus.ValidationFailed, validation.Errors, null);
        }

        if (!input.PreserveSavedToken)
        {
            var token = input.NewAuthToken!.Trim();
            var protectedResult = protector.Protect(token);
            if (!protectedResult.Succeeded || string.IsNullOrWhiteSpace(protectedResult.ProtectedBase64))
            {
                return new(SaveSettingsStatus.ProtectionFailed, NoErrors,
                    "The auth token could not be protected. Settings were not changed.");
            }

            protectedToken = protectedResult.ProtectedBase64;
        }

        var settings = new PersistedSettingsV1(
            1,
            input.AccountSid.Trim(),
            input.SenderValue.Trim(),
            protectedToken!,
            loaded.Settings?.LastCsvPath,
            input.SenderMode);
        var saved = await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return saved.Status == SettingsFileSaveStatus.Saved
            ? new(SaveSettingsStatus.Saved, NoErrors, "Twilio setup was saved securely.")
            : new(SaveSettingsStatus.StorageFailed, NoErrors,
                saved.SafeMessage ?? "Twilio setup could not be saved.");
    }

    public async Task<SaveCsvPathResult> SaveLastCsvPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        PersistedSettingsV1 settings;
        if (loaded.Status == SettingsFileStatus.Missing)
        {
            settings = new(1, string.Empty, string.Empty, string.Empty, path);
        }
        else if (loaded.Status == SettingsFileStatus.Loaded && loaded.Settings is not null)
        {
            settings = loaded.Settings with { LastCsvPath = path };
        }
        else
        {
            return new(
                SaveCsvPathStatus.StorageFailed,
                loaded.SafeMessage ?? "The CSV path could not be saved.");
        }

        var saved = await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return saved.Status == SettingsFileSaveStatus.Saved
            ? new(SaveCsvPathStatus.Saved, "The CSV path was saved for Refresh.")
            : new(
                SaveCsvPathStatus.StorageFailed,
                saved.SafeMessage ?? "The CSV path could not be saved.");
    }

    public async Task<CredentialLoadResult> LoadCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Status == SettingsFileStatus.Missing)
        {
            return new(CredentialLoadStatus.Incomplete, null,
                new[] { new FieldError("AuthToken", "SetupIncomplete", "Twilio setup is incomplete.") },
                "Complete Twilio setup before sending.");
        }

        if (loaded.Status is SettingsFileStatus.Corrupt or SettingsFileStatus.UnsupportedVersion)
        {
            return new(CredentialLoadStatus.SecretUnavailable, null,
                new[] { new FieldError("AuthToken", "AuthTokenUnavailable", "Re-enter the auth token.") },
                "Saved Twilio setup is unavailable. Re-enter and save the auth token.");
        }

        if (loaded.Status != SettingsFileStatus.Loaded || loaded.Settings is null)
        {
            return new(CredentialLoadStatus.StorageFailure, null, NoErrors,
                loaded.SafeMessage ?? "Saved Twilio setup could not be loaded.");
        }

        if (!HasSavedSetup(loaded.Settings))
        {
            return new(CredentialLoadStatus.Incomplete, null,
                new[] { new FieldError("AuthToken", "SetupIncomplete", "Twilio setup is incomplete.") },
                "Complete Twilio setup before sending.");
        }

        var unprotected = protector.TryUnprotect(loaded.Settings.ProtectedAuthToken);
        if (!unprotected.Succeeded || string.IsNullOrWhiteSpace(unprotected.Plaintext))
        {
            return new(CredentialLoadStatus.SecretUnavailable, null,
                new[] { new FieldError("AuthToken", "AuthTokenUnavailable", "Re-enter the auth token.") },
                "The saved auth token is unavailable. Re-enter and save it.");
        }

        var input = new SetupInput(
            loaded.Settings.AccountSid,
            ResolveSenderMode(loaded.Settings),
            loaded.Settings.SenderNumber,
            null,
            PreserveSavedToken: true);
        var validation = validator.Validate(input, savedTokenExists: true);
        if (!validation.IsValid)
        {
            return new(CredentialLoadStatus.Incomplete, null, validation.Errors,
                "Saved Twilio setup is incomplete or invalid.");
        }

        return new(CredentialLoadStatus.Ready,
            new TwilioCredentials(
                loaded.Settings.AccountSid.Trim(),
                ResolveSenderMode(loaded.Settings),
                loaded.Settings.SenderNumber.Trim(),
                unprotected.Plaintext.Trim()),
            NoErrors,
            null);
    }

    private static bool HasSavedSetup(PersistedSettingsV1 settings) =>
        !string.IsNullOrWhiteSpace(settings.AccountSid) ||
        !string.IsNullOrWhiteSpace(settings.SenderNumber) ||
        !string.IsNullOrWhiteSpace(settings.ProtectedAuthToken);

    private static TwilioSenderMode ResolveSenderMode(PersistedSettingsV1 settings) =>
        settings.SenderMode ?? TwilioSenderMode.FromPhoneNumber;
}
