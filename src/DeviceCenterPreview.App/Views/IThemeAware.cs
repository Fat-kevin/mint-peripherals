namespace DeviceCenterPreview.App.Views;

/// <summary>
/// Pages that build brushes in code (rather than XAML ThemeResource) refresh
/// those brushes when the window theme changes.
/// </summary>
public interface IThemeAware
{
    void OnAppThemeChanged();
}
