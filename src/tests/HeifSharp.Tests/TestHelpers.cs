namespace HeifSharp.Tests;

/// <summary>
/// Helpers for generating deterministic test frames.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Generates a deterministic gradient + checkerboard RGBA8 image. Useful as a
    /// stand-in for an avatar frame: predictable bytes, non-trivial frequency content
    /// so the encoder can't trivially flatten it.
    /// </summary>
    public static byte[] GenerateGradientRgba(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                // R: horizontal gradient. G: vertical gradient. B: checkerboard. A: opaque.
                buffer[i + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                buffer[i + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                buffer[i + 2] = (byte)((((x >> 4) ^ (y >> 4)) & 1) == 0 ? 32 : 224);
                buffer[i + 3] = 0xFF;
            }
        }
        return buffer;
    }
}
