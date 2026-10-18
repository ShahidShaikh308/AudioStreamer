using AudioStreamer.UI.Core.Navigation;
using Xunit;

namespace AudioStreamer.UI.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void Registry_contains_all_product_destinations_once()
    {
        Assert.Equal(8, UiPageRegistry.Pages.Count);
        Assert.Equal(Enum.GetValues<UiPage>().Order(), UiPageRegistry.Pages.Order());
        Assert.Equal(8, UiPageRegistry.Pages.Select(UiPageRegistry.Title).Distinct().Count());
    }

    [Theory]
    [InlineData(UiPage.Dashboard, "Home")]
    [InlineData(UiPage.Devices, "Devices")]
    [InlineData(UiPage.Sessions, "Sessions")]
    [InlineData(UiPage.AudioDevices, "Audio devices")]
    [InlineData(UiPage.Statistics, "Statistics")]
    [InlineData(UiPage.Diagnostics, "Diagnostics")]
    [InlineData(UiPage.Settings, "Settings")]
    [InlineData(UiPage.About, "About")]
    public void Pages_have_stable_titles(UiPage page, string title) => Assert.Equal(title, UiPageRegistry.Title(page));
}
