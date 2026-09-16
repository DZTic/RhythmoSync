using System.Globalization;
using RhythmoSync.Media;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class VideoExporterTests
{
    [Fact]
    public void CreateDecoderProcessInfo_IncludesSetPtsAtStartOfVfFilter()
    {
        var settings = new ExportSettings
        {
            FfmpegPath = "ffmpeg",
            VideoPath = "sample.mp4",
            OutputPath = "output.mp4",
            VideoWidth = 1920,
            Fps = 25.0,
            Bitrate = 5_000_000,
            CropTop = 0,
            CropBottom = 1080,
            ExportWidth = 1920,
            ExportHeight = 1080,
            VideoRenderHeight = 800,
            BandRenderHeight = 280,
            StartTime = 5.0,
            EndTime = 15.0,
            SyncLineX = 300,
            SyncOffsetEffective = 0.0,
            Pps = 150.0,
            IncludeAudio = true,
            OriginalAudioGain = 1.0,
            ExternalAudioTracks = [],
            Takes = []
        };

        var psi = VideoExporter.CreateDecoderProcessInfo(settings, 10.0, 1080, CultureInfo.InvariantCulture);

        var args = string.Join(" ", psi.ArgumentList);
        Assert.Contains("-vf", args);
        Assert.Contains("setpts=PTS-STARTPTS", args);
        
        // setpts doit être placé avant le filtre fps pour réinitialiser les timestamps
        var vfIndex = psi.ArgumentList.IndexOf("-vf");
        Assert.True(vfIndex >= 0 && vfIndex + 1 < psi.ArgumentList.Count);
        var vfValue = psi.ArgumentList[vfIndex + 1];
        var setptsPos = vfValue.IndexOf("setpts=PTS-STARTPTS");
        var fpsPos = vfValue.IndexOf("fps=");
        Assert.True(setptsPos >= 0);
        Assert.True(fpsPos > setptsPos);
    }

    [Theory]
    [InlineData(1.0, "asetpts=PTS-STARTPTS")]
    [InlineData(0.5, "asetpts=PTS-STARTPTS,volume=0.5")]
    public void BuildAudioFilter_IncludesAsetpts(double gain, string expected)
    {
        var filter = VideoExporter.BuildAudioFilter(gain, CultureInfo.InvariantCulture);
        Assert.Equal(expected, filter);
    }
}
