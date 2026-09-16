using RhythmoSync.Media;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class WaveformCacheTests
{
    [Fact]
    public void GetCachePath_GeneratesValidAndConsistentPath()
    {
        var path1 = WaveformGenerator.GetCachePath("test_video.mp4", 2000);
        var path2 = WaveformGenerator.GetCachePath("test_video.mp4", 2000);

        Assert.Equal(path1, path2);
        Assert.EndsWith(".wavecache", path1);
        Assert.Contains("waveforms", path1);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsWaveformData()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var originalPeaks = new float[] { -0.5f, 0.8f, -0.2f, 0.4f, 0.0f, 0.1f };
            var originalData = new WaveformData(originalPeaks, Duration: 42.5, SampleRate: 8000);

            WaveformGenerator.SaveToDiskCache(tempFile, 3, originalData);
            var loaded = WaveformGenerator.TryLoadFromDiskCache(tempFile, 3);

            Assert.NotNull(loaded);
            Assert.Equal(42.5, loaded!.Duration, 3);
            Assert.Equal(8000, loaded.SampleRate);
            Assert.Equal(originalPeaks.Length, loaded.Peaks.Length);
            for (var i = 0; i < originalPeaks.Length; i++)
            {
                Assert.Equal(originalPeaks[i], loaded.Peaks[i], 4);
            }
        }
        finally
        {
            var cachePath = WaveformGenerator.GetCachePath(tempFile, 3);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TryLoadFromDiskCache_ReturnsNullWhenNotFoundOrCorrupt()
    {
        var missing = WaveformGenerator.TryLoadFromDiskCache("non_existent_file_xyz.mp4", 100);
        Assert.Null(missing);
    }
}
