using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Songify.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Songify.App;

/// <summary>
/// Applies the chosen theme through WPF UI. Re-applies when Windows settings change, so
/// "Match Windows" follows light/dark switches and every choice picks up a new accent color.
/// Windows High Contrast always wins over an explicit Light/Dark choice.
/// </summary>
public sealed class ThemeSwitcher : IDisposable
{
    private AppTheme _choice;
    private (AppTheme Choice, SystemTheme System, Color Accent)? _applied;

    public ThemeSwitcher() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public void Apply(AppTheme choice)
    {
        _choice = choice;
        SystemThemeManager.UpdateSystemThemeCache();
        var inputs = (choice, SystemThemeManager.GetCachedSystemTheme(), ApplicationAccentColorManager.GetColorizationColor());
        if (_applied == inputs) return; // most settings broadcasts change nothing we show
        _applied = inputs;

        ApplicationTheme? explicitTheme = choice switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => null, // Match Windows, or an unknown value from a hand-edited settings file
        };
        if (explicitTheme is null || SystemParameters.HighContrast)
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
        else
            ApplicationThemeManager.Apply(explicitTheme.Value, WindowBackdropType.Mica, updateAccent: true);
    }

    // SystemEvents calls back on the subscribing (UI) thread; BeginInvoke returns to it quickly
    // and lets Windows finish writing the new setting before we read it.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(_choice));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
