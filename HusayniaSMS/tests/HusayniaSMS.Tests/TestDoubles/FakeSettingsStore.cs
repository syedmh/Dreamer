using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeSettingsStore : ILocalSettingsStore
{
    public SettingsFileLoadResult LoadResult { get; set; } =
        new(SettingsFileStatus.Missing, null, null);
    public SettingsFileSaveResult SaveResult { get; set; } =
        new(SettingsFileSaveStatus.Saved, null);
    public PersistedSettingsV1? SavedSettings { get; private set; }
    public int SaveCount { get; private set; }

    public Task<SettingsFileLoadResult> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LoadResult);

    public Task<SettingsFileSaveResult> SaveAsync(
        PersistedSettingsV1 settings,
        CancellationToken cancellationToken)
    {
        SaveCount++;
        SavedSettings = settings;
        if (SaveResult.Status == SettingsFileSaveStatus.Saved)
        {
            LoadResult = new(SettingsFileStatus.Loaded, settings, null);
        }

        return Task.FromResult(SaveResult);
    }
}
