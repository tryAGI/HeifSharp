namespace HeifSharp.Native;

/// <summary>
/// libheif native constants and enums (matches libheif/heif.h on the pinned version
/// in scripts/VERSIONS.md). Values are stable across libheif &gt;= 1.17.
/// </summary>
internal static class HeifConstants
{
    // heif_error_code
    public const int HeifErrorOk = 0;
    public const int HeifErrorInputDoesNotExist = 1;
    public const int HeifErrorInvalidInput = 2;
    public const int HeifErrorUnsupportedFiletype = 3;
    public const int HeifErrorUnsupportedFeature = 4;
    public const int HeifErrorUsageError = 5;
    public const int HeifErrorMemoryAllocationError = 6;
    public const int HeifErrorDecoderPluginError = 7;
    public const int HeifErrorEncoderPluginError = 8;
    public const int HeifErrorEncodingError = 9;
    public const int HeifErrorColorProfileDoesNotExist = 10;
}

/// <summary>
/// heif_compression_format from heif.h.
/// </summary>
public enum HeifCompression
{
    Undefined = 0,
    Hevc = 1,
    Avc = 2,
    Jpeg = 3,
    Av1 = 4,
    Vvc = 5,
    Evc = 6,
    Jpeg2000 = 7,
    Uncompressed = 8,
    Mask = 9,
}

/// <summary>
/// heif_chroma from heif.h.
/// </summary>
public enum HeifChroma
{
    Undefined = 99,
    Monochrome = 0,
    Chroma420 = 1,
    Chroma422 = 2,
    Chroma444 = 3,
    InterleavedRgb = 10,
    InterleavedRgba = 11,
    InterleavedRrggbbBe = 12,
    InterleavedRrggbbaaBe = 13,
    InterleavedRrggbbLe = 14,
    InterleavedRrggbbaaLe = 15,
}

/// <summary>
/// heif_colorspace from heif.h.
/// </summary>
public enum HeifColorspace
{
    Undefined = 99,
    Yuv = 0,
    Rgb = 1,
    Monochrome = 2,
}

/// <summary>
/// heif_channel from heif.h.
/// </summary>
public enum HeifChannel
{
    Y = 0,
    Cb = 1,
    Cr = 2,
    R = 3,
    G = 4,
    B = 5,
    Alpha = 6,
    Interleaved = 10,
}
