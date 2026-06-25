using FluentAssertions;
using Xunit;

namespace HeifSharp.Tests;

public class HeifEncoderTests
{
    [NativeRequiredFact]
    public void Construct_AppleSafeDefaults_Succeeds()
    {
        using var encoder = new HeifEncoder();
        encoder.Config.Quality.Should().Be(50);
        encoder.Config.Profile.Should().Be("main");
        encoder.Config.Chroma.Should().Be("420");
    }

    [Fact]
    public void Construct_RejectsInvalidQuality()
    {
        Action act = () => _ = new HeifEncoder(new HeifEncoderConfig { Quality = 200 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [HevcEncoderRequiredFact]
    public void EncodeRgba_RejectsOddDimensions()
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(31, 32);
        Action act = () => encoder.EncodeRgba(rgba, 31, 32);
        act.Should().Throw<ArgumentException>();
    }

    [HevcEncoderRequiredFact]
    public void EncodeRgba_ProducesNonEmptyHeicBytes()
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(64, 64);

        var heic = encoder.EncodeRgba(rgba, 64, 64);

        heic.Should().NotBeNull();
        heic.Length.Should().BeGreaterThan(64); // a real HEIC has ftyp + meta + mdat
    }

    [HevcEncoderRequiredTheory]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    [InlineData(320, 240)]
    [InlineData(720, 720)]
    public void EncodeRgba_VariousSizes_ProducesValidHeic(int width, int height)
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(width, height);

        var heic = encoder.EncodeRgba(rgba, width, height);

        heic.Length.Should().BeGreaterThan(64);
        // All HEIC files start with an "ftyp" box: 4 bytes size + "ftyp".
        heic.Length.Should().BeGreaterThan(8);
        System.Text.Encoding.ASCII.GetString(heic, 4, 4).Should().Be("ftyp");
    }
}
