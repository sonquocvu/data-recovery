using System.Windows;
using DataRecoveryStudio.Core;
using Microsoft.Win32;

namespace DataRecoveryStudio.App;

public static class ThemeManager
{
    private static ThemePreference _effectiveTheme = ThemePreference.Dark;

    public static ThemePreference EffectiveTheme => _effectiveTheme;
    public static event EventHandler<ThemePreference>? EffectiveThemeChanged;

    public static void Apply(ThemePreference preference)
    {
        var effective = preference == ThemePreference.FollowSystem ? GetSystemPreference() : preference;
        var themeFile = effective == ThemePreference.Light ? "LightTheme.xaml" : "DarkTheme.xaml";
        var source = new Uri(
            $"pack://application:,,,/DataRecoveryStudio;component/Themes/{themeFile}",
            UriKind.Absolute);
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = source };
        if (current is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(current)] = replacement;

        _effectiveTheme = effective;
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            NativeWindowAppearance.TryApply(window, effective == ThemePreference.Dark);
        }

        EffectiveThemeChanged?.Invoke(null, effective);
    }

    public static void Register(Window window)
    {
        void ApplyCurrent(object? sender = null, EventArgs? args = null) =>
            NativeWindowAppearance.TryApply(window, EffectiveTheme == ThemePreference.Dark);

        EventHandler<ThemePreference> themeChanged = (_, _) => ApplyCurrent();
        window.SourceInitialized += ApplyCurrent;
        EffectiveThemeChanged += themeChanged;
        window.Closed += (_, _) => EffectiveThemeChanged -= themeChanged;
    }

    private static ThemePreference GetSystemPreference()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value > 0 ? ThemePreference.Light : ThemePreference.Dark;
    }
}
