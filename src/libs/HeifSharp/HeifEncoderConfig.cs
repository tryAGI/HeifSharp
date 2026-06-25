namespace HeifSharp;

/// <summary>
/// Configuration for <see cref="HeifEncoder"/>. Defaults are chosen to produce
/// hardware-decodable output on Apple HEIC consumers (iOS / iPadOS / macOS / watchOS / tvOS):
/// HEVC Main profile, 4:2:0 chroma, 8-bit, hvc1 brand, NCLX BT.709 color profile.
/// Override at your own risk — divergence from these defaults will cause silent
/// rejections by Apple's HW decoder even though the bitstream parses on Linux.
/// </summary>
public sealed record HeifEncoderConfig
{
    /// <summary>
    /// Lossy quality (0=worst, 100=best). 50 is the libheif default. Stills at 30 fps
    /// targeting ~12 KB / frame at 720p typically land between 35 and 55 depending on
    /// scene complexity.
    /// </summary>
    public int Quality { get; init; } = 50;

    /// <summary>
    /// Lossless mode disables quality clamping and bypasses the 4:2:0 chroma constraint.
    /// Apple decoders do NOT consistently support lossless HEIC — leave this false unless
    /// you are explicitly targeting non-Apple consumers.
    /// </summary>
    public bool Lossless { get; init; } = false;

    /// <summary>
    /// x265 preset string. "superfast" is the right pick for 30 fps still encoding on a
    /// single ARM64 vCPU. Lower presets ("ultrafast") trade quality at a given quality
    /// setting; higher ("veryfast", "faster") get diminishing returns for stills.
    /// </summary>
    public string Preset { get; init; } = "superfast";

    /// <summary>
    /// x265 tune string. "stillimage" is the right pick for 30 fps avatar frames; it tells
    /// the encoder to skip motion-prediction logic that doesn't apply to all-intra stills.
    /// </summary>
    public string Tune { get; init; } = "stillimage";

    /// <summary>
    /// HEVC profile string. "main" is the only profile guaranteed to be HW-decoded by
    /// Apple consumers. Do not use "main10" / "main444" for Apple targets.
    /// </summary>
    public string Profile { get; init; } = "main";

    /// <summary>
    /// Chroma subsampling. "420" is the only setting Apple consumers HW-decode reliably.
    /// </summary>
    public string Chroma { get; init; } = "420";

    /// <summary>
    /// Apple-recommended defaults for realtime avatar frames at ~720p.
    /// </summary>
    public static HeifEncoderConfig AppleSafeDefaults() => new();

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any value is obviously
    /// invalid before the encoder rejects it at runtime.
    /// </summary>
    public void Validate()
    {
        if (Quality < 0 || Quality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Quality), Quality, "Quality must be in [0, 100]");
        }
        if (string.IsNullOrWhiteSpace(Preset)) throw new ArgumentException("Preset is empty", nameof(Preset));
        if (string.IsNullOrWhiteSpace(Tune)) throw new ArgumentException("Tune is empty", nameof(Tune));
        if (string.IsNullOrWhiteSpace(Profile)) throw new ArgumentException("Profile is empty", nameof(Profile));
        if (string.IsNullOrWhiteSpace(Chroma)) throw new ArgumentException("Chroma is empty", nameof(Chroma));
    }
}
