using Avalonia.Styling;
using SukiUI;
using SukiUI.Models;
using System;
using System.Collections.Generic;
using Avalonia.Media;
using ZLinq;

namespace NetSonar.Avalonia;

public partial class App
{
    public static SukiTheme Theme { get; private set; } = null!;
    public static List<SukiColorTheme> ThemeColors { get; private set; } = null!;

    private static void SetupTheme()
    {
        Theme = SukiTheme.GetInstance();
        ChangeBaseTheme(AppSettings.Theme);
        ChangeColorTheme();
    }

    public static void ChangeBaseTheme(ApplicationTheme theme)
    {
        AppSettings.Theme = theme;
        ThemeColors = new List<SukiColorTheme>(Theme.ColorThemes);
        ThemeColors.AddRange([
            new SukiColorTheme("Pink", new Color(255, 255, 20, 147), new Color(255, 255, 192, 203)),
            new SukiColorTheme("White", new Color(255, 255, 255, 255), new Color(255, 0, 0, 0)),
            new SukiColorTheme("Black", new Color(255, 0, 0, 0), new Color(255, 255, 255, 255))
        ]);
        switch (theme)
        {
            case ApplicationTheme.Default:
                Theme.ChangeBaseTheme(ThemeVariant.Default);
                break;
            case ApplicationTheme.Light:
                Theme.ChangeBaseTheme(ThemeVariant.Light);
                break;
            case ApplicationTheme.Dark:
                Theme.ChangeBaseTheme(ThemeVariant.Dark);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(theme), theme, null);
        }

        ChangeColorTheme();
    }

    public static void ChangeColorTheme(SukiColorTheme color)
    {
        Theme.ChangeColorTheme(color);
        AppSettings.ThemeColor = color.DisplayName;
    }

    public static void ChangeColorTheme(string? displayName = null)
    {
        displayName ??= AppSettings.ThemeColor;
        if (string.IsNullOrWhiteSpace(displayName)) return;
        var colorTheme = ThemeColors
            .AsValueEnumerable()
            .FirstOrDefault(theme => theme.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (colorTheme is null) return;
        Theme.ChangeColorTheme(colorTheme);
        AppSettings.ThemeColor = displayName;
    }
}