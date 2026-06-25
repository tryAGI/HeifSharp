using FluentAssertions;
using Xunit;

namespace HeifSharp.Tests;

/// <summary>
/// Verifies the encoder produces output that meets Apple's HEIF/HEVC HW-decoder
/// constraints documented in https://support.apple.com/en-us/116944. Specifically:
/// - File starts with an ftyp box.
/// - Major brand or compatible-brands list contains "heic" and "mif1".
/// - Compatible-brands list contains "hvc1" (NOT just "hev1") so AV samples are tagged
///   for Apple's HW decoder path.
///
/// These tests are necessary but not sufficient — final validation requires
/// AirDropping out.heic to a real Apple Watch and confirming Photos opens it. macOS
/// Quick Look is too permissive to be a reliable proxy for watchOS HW decode.
/// </summary>
public class HeifAppleCompatTests
{
    [HevcEncoderRequiredFact]
    public void Output_HasFtypBoxWithHeicBrand()
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(128, 128);

        var heic = encoder.EncodeRgba(rgba, 128, 128);
        var ftyp = ParseFtyp(heic);

        ftyp.MajorBrand.Should().Be("heic", because: "Apple's iOS/watchOS/macOS HEIC pipeline keys off the heic major brand");
        ftyp.CompatibleBrands.Should().Contain("mif1");
    }

    [HevcEncoderRequiredFact]
    public void Output_UsesHvc1SampleEntryNotHev1()
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(128, 128);

        var heic = encoder.EncodeRgba(rgba, 128, 128);

        // The hvc1/hev1 4cc lives in the HEVCSampleEntry inside iprp/ipco/hvcC's parent
        // box (NOT in ftyp's compatible-brands list). Parsing the full ISOBMFF tree just
        // to reach it is out of scope here; instead we check that the literal "hvc1"
        // 4-character code appears in the file bytes and "hev1" does not. libheif always
        // emits hvc1 (parameter sets out-of-band in hvcC, not inline in samples), but
        // verifying it explicitly catches future regressions if libheif changes default.
        var hvc1 = System.Text.Encoding.ASCII.GetBytes("hvc1");
        var hev1 = System.Text.Encoding.ASCII.GetBytes("hev1");

        ContainsBytes(heic, hvc1).Should().BeTrue(
            because: "Apple HW decoders require HEVCSampleEntry tagged hvc1");
        ContainsBytes(heic, hev1).Should().BeFalse(
            because: "hev1 sample entries are rejected by Apple HW decoders");
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [HevcEncoderRequiredFact]
    public void Output_HasMetaAndMdatBoxes()
    {
        using var encoder = new HeifEncoder();
        var rgba = TestHelpers.GenerateGradientRgba(128, 128);

        var heic = encoder.EncodeRgba(rgba, 128, 128);
        var topLevelBoxNames = ListTopLevelBoxes(heic);

        topLevelBoxNames.Should().Contain("ftyp");
        topLevelBoxNames.Should().Contain("meta");
        topLevelBoxNames.Should().Contain("mdat");
    }

    // --- ISOBMFF mini parser (just enough for ftyp + top-level box names) ---

    private record FtypBox(string MajorBrand, uint MinorVersion, IReadOnlyList<string> CompatibleBrands);

    private static FtypBox ParseFtyp(byte[] data)
    {
        // Each box: [4B big-endian size][4B type][...payload]
        var box0Size = ReadBeU32(data, 0);
        var box0Type = ReadAscii4(data, 4);
        if (box0Type != "ftyp")
        {
            throw new InvalidDataException($"First box is {box0Type}, expected ftyp");
        }
        var major = ReadAscii4(data, 8);
        var minor = ReadBeU32(data, 12);
        var compat = new List<string>();
        for (int p = 16; p + 4 <= (int)box0Size && p + 4 <= data.Length; p += 4)
        {
            compat.Add(ReadAscii4(data, p));
        }
        return new FtypBox(major, minor, compat);
    }

    private static List<string> ListTopLevelBoxes(byte[] data)
    {
        var names = new List<string>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            var size = ReadBeU32(data, pos);
            var type = ReadAscii4(data, pos + 4);
            names.Add(type);
            if (size == 0)
            {
                // size==0 means "to end of file"
                break;
            }
            if (size == 1)
            {
                // 64-bit largesize follows
                if (pos + 16 > data.Length) break;
                var large = ReadBeU64(data, pos + 8);
                if (large > int.MaxValue || pos + (long)large > data.Length) break;
                pos += (int)large;
            }
            else
            {
                if (size < 8 || pos + (long)size > data.Length) break;
                pos += (int)size;
            }
        }
        return names;
    }

    private static uint ReadBeU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static ulong ReadBeU64(byte[] data, int offset)
    {
        ulong hi = ReadBeU32(data, offset);
        ulong lo = ReadBeU32(data, offset + 4);
        return (hi << 32) | lo;
    }

    private static string ReadAscii4(byte[] data, int offset) =>
        System.Text.Encoding.ASCII.GetString(data, offset, 4);
}
