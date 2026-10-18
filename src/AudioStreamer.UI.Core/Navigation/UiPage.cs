namespace AudioStreamer.UI.Core.Navigation;

public enum UiPage { Dashboard, Devices, Sessions, AudioDevices, Statistics, Diagnostics, Settings, About }

public static class UiPageRegistry
{
    public static IReadOnlyList<UiPage> Pages { get; } = Enum.GetValues<UiPage>();

    public static string Title(UiPage page) => page switch
    {
        UiPage.Dashboard => "Home",
        UiPage.Devices => "Devices",
        UiPage.Sessions => "Sessions",
        UiPage.AudioDevices => "Audio devices",
        UiPage.Statistics => "Statistics",
        UiPage.Diagnostics => "Diagnostics",
        UiPage.Settings => "Settings",
        UiPage.About => "About",
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };
}
