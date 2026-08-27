using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class SettingsPersistenceTests
{
    [Fact]
    public async Task SavedThemeAndLanguage_RoundTripThroughJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var writer = new JsonSettingsStore(path);
            await writer.SaveAsync(new AppSettings(ThemePreference.Light, "vi-VN"), CancellationToken.None);

            var loaded = await new JsonSettingsStore(path).LoadAsync(CancellationToken.None);

            Assert.Equal(ThemePreference.Light, loaded.Theme);
            Assert.Equal("vi-VN", loaded.LanguageCode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MissingSettings_ReturnSafeDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio.Tests", Guid.NewGuid().ToString("N"), "missing.json");

        var loaded = await new JsonSettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettings.Default, loaded);
    }
}
