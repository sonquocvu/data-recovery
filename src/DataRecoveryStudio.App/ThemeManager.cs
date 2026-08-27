using System.Windows;
using DataRecoveryStudio.Core;
using Microsoft.Win32;

namespace DataRecoveryStudio.App;

public static class ThemeManager
{
    public static void Apply(ThemePreference preference)
    {
        var effective = preference == ThemePreference.FollowSystem ? GetSystemPreference() : preference;
        var source = effective == ThemePreference.Light
            ? new Uri("Themes/LightTheme.xaml", UriKind.Relative)
            : new Uri("Themes/DarkTheme.xaml", UriKind.Relative);
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = source };
        if (current is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(current)] = replacement;
    }

    private static ThemePreference GetSystemPreference()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value > 0 ? ThemePreference.Light : ThemePreference.Dark;
    }
}
