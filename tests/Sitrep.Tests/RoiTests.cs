using Sitrep.Core;

namespace Sitrep.Tests;

public sealed class RoiTests
{
    [Fact]
    public void BuildsCursorRelativeDefaultSize()
    {
        var roi = RoiBuilder.BuildCursorRelative(1000, 700);
        Assert.Equal(360, roi.Width);
        Assert.Equal(200, roi.Height);
        Assert.Equal(960, roi.X);
        Assert.Equal(524, roi.Y);
    }

    [Fact]
    public void IntersectsWithBounds()
    {
        var roi = new CaptureRegion(900, 640, 300, 180);
        var bounds = new CaptureRegion(0, 0, 2048, 1152);
        var hit = RoiBuilder.Intersect(roi, bounds);
        Assert.NotNull(hit);
        Assert.Equal(roi, hit!.Value);
    }

    [Fact]
    public void ClippedReturnsNullWhenOutside()
    {
        var roi = new CaptureRegion(5000, 5000, 100, 100);
        var bounds = new CaptureRegion(0, 0, 2048, 1152);
        Assert.Null(RoiBuilder.Intersect(roi, bounds));
    }

    [Fact]
    public void DetectsOverlayOverlap()
    {
        var roi = new CaptureRegion(100, 100, 200, 200);
        var overlay = new CaptureRegion(150, 150, 300, 110);
        Assert.True(RoiBuilder.Overlaps(roi, overlay));
        Assert.False(RoiBuilder.Overlaps(roi, new CaptureRegion(500, 500, 10, 10)));
    }

    [Fact]
    public void MapRegionMatchesReference1440pCapture()
    {
        // Calibrated against the user's 2560x1440 screenshots: an 880 px square centered horizontally.
        Assert.Equal(new CaptureRegion(840, 280, 880, 880), RoiBuilder.GetCenteredMapRegion(2560, 1440));
    }

    [Theory]
    [InlineData(1920, 1080, 660, 630, 210)]
    [InlineData(3440, 1440, 880, 1280, 280)]
    public void MapRegionScalesWithClientHeightAndStaysCentered(int w, int h, int size, int x, int y)
    {
        Assert.Equal(new CaptureRegion(x, y, size, size), RoiBuilder.GetCenteredMapRegion(w, h));
    }

    [Fact]
    public void MapRegionIsEmptyForDegenerateClient()
    {
        Assert.True(RoiBuilder.GetCenteredMapRegion(0, 1440).IsEmpty);
        Assert.True(RoiBuilder.GetCenteredMapRegion(2560, -1).IsEmpty);
        Assert.False(RoiBuilder.IsPointInsideMap(100, 100, 0, 0));
    }

    [Theory]
    [InlineData(840, 280, true)]      // top-left corner is inside
    [InlineData(1719, 1159, true)]    // bottom-right inclusive pixel
    [InlineData(1720, 700, false)]    // one past the right edge
    [InlineData(1280, 1160, false)]   // one past the bottom edge
    [InlineData(839, 700, false)]     // left gutter
    [InlineData(1280, 100, false)]    // top HUD
    public void MapGuardUsesHalfOpenBounds(int x, int y, bool inside)
    {
        Assert.Equal(inside, RoiBuilder.IsPointInsideMap(x, y, 2560, 1440));
    }
}
