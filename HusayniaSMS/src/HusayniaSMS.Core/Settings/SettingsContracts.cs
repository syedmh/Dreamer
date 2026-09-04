namespace HusayniaSMS.Core.Settings;

public enum TwilioSenderMode
{
    FromPhoneNumber = 0,
    MessagingServiceSid = 1
}

public sealed record SetupInput(
    string AccountSid,
    TwilioSenderMode SenderMode,
    string SenderValue,
    string? NewAuthToken,
    bool PreserveSavedToken);

public sealed record FieldError(string Field, string Code, string Message);

public sealed record SetupValidationResult(
    bool IsValid,
    IReadOnlyList<FieldError> Errors);

public interface ISetupValidator
{
    SetupValidationResult Validate(SetupInput input, bool savedTokenExists);
}

public sealed record SecretProtectResult(bool Succeeded, string? ProtectedBase64);
public sealed record SecretUnprotectResult(bool Succeeded, string? Plaintext);

public interface ISecretProtector
{
    SecretProtectResult Protect(string plaintext);
    SecretUnprotectResult TryUnprotect(string protectedBase64);
}

public sealed record PersistedSettingsV1(
    int SchemaVersion,
    string AccountSid,
    string SenderNumber,
    string ProtectedAuthToken,
    string? LastCsvPath = null,
    TwilioSenderMode? SenderMode = null);

public enum SettingsFileStatus
{
    Loaded,
    Missing,
    Corrupt,
    UnsupportedVersion,
    AccessDenied,
    IoFailure
}

public sealed record SettingsFileLoadResult(
    SettingsFileStatus Status,
    PersistedSettingsV1? Settings,
    string? SafeMessage);

public enum SettingsFileSaveStatus
{
    Saved,
    AccessDenied,
    IoFailure
}

public sealed record SettingsFileSaveResult(
    SettingsFileSaveStatus Status,
    string? SafeMessage);

public interface ILocalSettingsStore
{
    Task<SettingsFileLoadResult> LoadAsync(CancellationToken cancellationToken);
    Task<SettingsFileSaveResult> SaveAsync(
        PersistedSettingsV1 settings,
        CancellationToken cancellationToken);
}

public enum TokenState
{
    Missing,
    Available,
    Unavailable
}

public sealed record SettingsDescriptor(
    string AccountSid,
    TwilioSenderMode SenderMode,
    string SenderValue,
    TokenState TokenState,
    string? LastCsvPath = null);

public enum SaveSettingsStatus
{
    Saved,
    ValidationFailed,
    ProtectionFailed,
    StorageFailed
}

public sealed record SaveSettingsResult(
    SaveSettingsStatus Status,
    IReadOnlyList<FieldError> Errors,
    string? SafeMessage);

public enum SaveCsvPathStatus
{
    Saved,
    StorageFailed
}

public sealed record SaveCsvPathResult(
    SaveCsvPathStatus Status,
    string? SafeMessage);

public enum CredentialLoadStatus
{
    Ready,
    Incomplete,
    SecretUnavailable,
    StorageFailure
}

public sealed record CredentialLoadResult(
    CredentialLoadStatus Status,
    TwilioCredentials? Credentials,
    IReadOnlyList<FieldError> Errors,
    string? SafeMessage);

public sealed class TwilioCredentials
{
    public TwilioCredentials(
        string accountSid,
        TwilioSenderMode senderMode,
        string senderValue,
        string authToken)
    {
        AccountSid = accountSid;
        SenderMode = senderMode;
        SenderValue = senderValue;
        AuthToken = authToken;
    }

    public string AccountSid { get; }
    public TwilioSenderMode SenderMode { get; }
    public string SenderValue { get; }
    public string AuthToken { get; }
    public override string ToString() => "[TwilioCredentials REDACTED]";
}

public interface ISettingsService
{
    Task<SettingsDescriptor> LoadDescriptorAsync(CancellationToken cancellationToken);
    Task<SaveSettingsResult> SaveAsync(
        SetupInput input,
        CancellationToken cancellationToken);
    Task<SaveCsvPathResult> SaveLastCsvPathAsync(
        string path,
        CancellationToken cancellationToken);
    Task<CredentialLoadResult> LoadCredentialsAsync(
        CancellationToken cancellationToken);
}
