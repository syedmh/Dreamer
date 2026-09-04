namespace HusayniaSMS.WinForms.Infrastructure.Settings;

public sealed class LocalSettingsPathProvider
{
    public LocalSettingsPathProvider(bool safeDemo, string? localApplicationDataRoot = null)
    {
        var root = localApplicationDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SettingsDirectory = Path.Combine(
            root,
            "HusayniaSMS",
            safeDemo ? "SafeDemo" : string.Empty);
        SettingsPath = Path.Combine(SettingsDirectory, "settings.v1.json");
    }

    public string SettingsDirectory { get; }
    public string SettingsPath { get; }
}
