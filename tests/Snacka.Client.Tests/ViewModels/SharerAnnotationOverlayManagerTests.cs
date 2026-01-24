using Snacka.Client.ViewModels;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class SharerAnnotationOverlayManagerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // Act & Assert - should not throw
        var manager = new SharerAnnotationOverlayManager();
        manager.Dispose();
    }

    #endregion

    #region Hide Tests

    [Fact]
    public void Hide_WhenNoOverlayShown_DoesNotThrow()
    {
        // Arrange
        var manager = new SharerAnnotationOverlayManager();

        // Act & Assert - should not throw
        manager.Hide();
        manager.Dispose();
    }

    [Fact]
    public void Hide_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var manager = new SharerAnnotationOverlayManager();

        // Act & Assert - should not throw
        manager.Hide();
        manager.Hide();
        manager.Hide();
        manager.Dispose();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_WhenNoOverlayShown_DoesNotThrow()
    {
        // Arrange
        var manager = new SharerAnnotationOverlayManager();

        // Act & Assert - should not throw
        manager.Dispose();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var manager = new SharerAnnotationOverlayManager();

        // Act & Assert - should not throw
        manager.Dispose();
        manager.Dispose();
    }

    #endregion

    #region CloseRequested Event Tests

    [Fact]
    public void CloseRequested_CanSubscribeToEvent()
    {
        // Arrange
        var manager = new SharerAnnotationOverlayManager();
        var eventRaised = false;

        // Act
        manager.CloseRequested += () => eventRaised = true;

        // Assert - event is subscribed (we can't trigger it without showing overlay)
        Assert.False(eventRaised); // Just verifies subscription doesn't throw
        manager.Dispose();
    }

    #endregion
}
