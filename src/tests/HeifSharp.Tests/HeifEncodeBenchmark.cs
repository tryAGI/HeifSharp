using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace HeifSharp.Tests;

/// <summary>
/// Informational micro-benchmark: encodes 30 sequential 720p frames and prints
/// per-frame wall-clock + average HEIC size. NOT a perf gate — single-machine numbers
/// vary too much to fail the build on. Useful when first standing up build-natives.sh
/// to confirm the encoder is roughly in the right zone before integration starts.
/// </summary>
public class HeifEncodeBenchmark
{
    private readonly ITestOutputHelper _output;

    public HeifEncodeBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    [HevcEncoderRequiredFact]
    public void Encode_30FramesAt720p_RecordWallClock()
    {
        const int width = 1280;
        const int height = 720;
        const int frames = 30;

        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(width, height);

        // Warm up plugin discovery + first-frame init cost.
        _ = encoder.EncodeRgba(rgba, width, height);

        var sw = Stopwatch.StartNew();
        long totalBytes = 0;
        for (int i = 0; i < frames; i++)
        {
            var heic = encoder.EncodeRgba(rgba, width, height);
            totalBytes += heic.Length;
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / frames;
        var avgKb = (totalBytes / (double)frames) / 1024.0;
        _output.WriteLine($"Encoded {frames} frames @ {width}x{height} in {sw.Elapsed.TotalMilliseconds:F1}ms total");
        _output.WriteLine($"  avg per frame: {avgMs:F2}ms, avg size: {avgKb:F1} KB");
        _output.WriteLine($"  effective rate: {1000.0 / avgMs:F1} fps");

        // Soft sanity check — each frame should produce non-empty output.
        totalBytes.Should().BeGreaterThan(frames * 64);
    }
}
