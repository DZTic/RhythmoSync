using System;
using RhythmoSync.Media;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class ExportLayoutTests
{
    [Fact]
    public void Compute_WithoutFps_MaintainsExactPps()
    {
        var layout = ExportLayout.Compute(
            nativeWidth: 1920,
            croppedNativeHeight: 1080,
            bandStripHeight: 200,
            bandScale: 1.0,
            zoomLevel: 135.0,
            syncLineX: 300.0);

        Assert.Equal(1920, layout.ExportWidth);
        Assert.Equal(1080, layout.ExportHeight);
        Assert.Equal(135.0 * layout.LaneScale, layout.ExportPps);
    }

    [Theory]
    [InlineData(25.0, 135.0)]
    [InlineData(30.0, 140.0)]
    [InlineData(60.0, 200.0)]
    [InlineData(24.0, 100.0)]
    public void Compute_WithFps_HarmonizesPpsToIntegerPixelsPerFrame(double fps, double zoomLevel)
    {
        var layout = ExportLayout.Compute(
            nativeWidth: 1920,
            croppedNativeHeight: 1080,
            bandStripHeight: 200,
            bandScale: 1.0,
            zoomLevel: zoomLevel,
            syncLineX: 300.0,
            fps: fps);

        var pixelsPerFrame = layout.ExportPps / fps;
        var rounded = Math.Round(pixelsPerFrame);

        // Le déplacement par trame doit être strictement un entier pour éviter le judder 4px/5px
        Assert.Equal(rounded, pixelsPerFrame, precision: 6);
    }
}
