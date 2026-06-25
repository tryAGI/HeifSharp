using System.Runtime.InteropServices;

namespace HeifSharp.Native;

/// <summary>
/// P/Invoke bindings for the native libheif library.
/// libheif's parameter API is fully typed (no varargs) so a shim library is not required.
/// </summary>
internal static class HeifNative
{
    private const string LibraryName = "heif";

    static HeifNative()
    {
        try
        {
            NativeLibraryLoader.Initialize();
        }
        catch (Exception ex)
        {
            // Don't throw in static constructor; let the first P/Invoke surface the error.
            System.Diagnostics.Debug.WriteLine($"Failed to initialize libheif loader: {ex.Message}");
        }
    }

    // libheif returns heif_error by value as a 3-field struct: { code, subcode, char* message }.
    // Marshalling structs by value across platforms is fragile; the C# wrapper instead uses the
    // result-via-out-param helpers that libheif provides for fallible calls. For functions that
    // return heif_error directly, callers receive only the integer code; the optional human
    // message is fetched via heif_get_version_number_text-style helpers when needed.
    [StructLayout(LayoutKind.Sequential)]
    public struct HeifError
    {
        public int Code;
        public int Subcode;
        public IntPtr Message; // const char*
    }

    // -------- Plugin loading (libheif >=1.18 requires explicit registration) --------

    /// <summary>
    /// Loads all plugins from a directory. libheif 1.18+ no longer auto-discovers plugins
    /// via compiled-in paths reliably; explicit registration is required when shipping
    /// a vendored libheif outside its install prefix.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern HeifError heif_load_plugins(
        [MarshalAs(UnmanagedType.LPStr)] string directory,
        IntPtr outPlugins,    // const heif_plugin_info*** — pass IntPtr.Zero
        IntPtr outNPlugins,   // int* — pass IntPtr.Zero
        int maxPlugins);      // 0 = no limit

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern HeifError heif_load_plugin(
        [MarshalAs(UnmanagedType.LPStr)] string filename,
        IntPtr outPlugin); // const heif_plugin_info** — pass IntPtr.Zero

    // -------- Version --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr heif_get_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint heif_get_version_number();

    // -------- Context --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr heif_context_alloc();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void heif_context_free(IntPtr ctx);

    // -------- Encoder lookup / lifecycle --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_context_get_encoder_for_format(
        IntPtr ctx,
        HeifCompression format,
        out IntPtr encoderOut);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void heif_encoder_release(IntPtr encoder);

    // -------- Encoder parameters (typed setters; no varargs) --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_encoder_set_lossy_quality(IntPtr encoder, int quality);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_encoder_set_lossless(IntPtr encoder, int enable);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern HeifError heif_encoder_set_parameter_string(
        IntPtr encoder,
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        [MarshalAs(UnmanagedType.LPStr)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern HeifError heif_encoder_set_parameter_integer(
        IntPtr encoder,
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        int value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern HeifError heif_encoder_set_parameter_boolean(
        IntPtr encoder,
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        int value);

    // -------- Image creation --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_image_create(
        int width,
        int height,
        HeifColorspace colorspace,
        HeifChroma chroma,
        out IntPtr imageOut);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void heif_image_release(IntPtr image);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_image_add_plane(
        IntPtr image,
        HeifChannel channel,
        int width,
        int height,
        int bitDepth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr heif_image_get_plane(
        IntPtr image,
        HeifChannel channel,
        out int outStride);

    // NCLX color profile (BT.709 etc.)
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_image_set_nclx_color_profile(
        IntPtr image,
        IntPtr nclxProfile);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr heif_nclx_color_profile_alloc();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void heif_nclx_color_profile_free(IntPtr profile);

    // libheif &gt;=1.18 introduces typed setters; older versions use direct field writes
    // through the struct layout. We use the field-write style here because the
    // struct shape has been stable since 1.10.
    [StructLayout(LayoutKind.Sequential)]
    public struct HeifColorProfileNclx
    {
        public byte Version;
        public ushort ColorPrimaries;       // 1 = BT.709
        public ushort TransferCharacteristics; // 1 = BT.709
        public ushort MatrixCoefficients;    // 1 = BT.709
        public byte FullRangeFlag;          // 0 = limited
    }

    // -------- Encoding --------

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_context_encode_image(
        IntPtr ctx,
        IntPtr image,
        IntPtr encoder,
        IntPtr encodingOptions, // NULL for defaults
        out IntPtr imageHandleOut);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void heif_image_handle_release(IntPtr handle);

    // -------- Output (write to memory) --------
    //
    // libheif's writer API requires registering a writer callback. We implement a
    // minimal in-memory writer in HeifMemoryWriter using GCHandle-pinned delegates
    // so the wrapper can return byte[] without temp files.
    //
    // The native struct heif_writer has a single function pointer:
    //   heif_error (*write)(struct heif_context*, const void* data, size_t size, void* userdata);
    // followed by an int writer_api_version = 1.

    [StructLayout(LayoutKind.Sequential)]
    public struct HeifWriter
    {
        public int WriterApiVersion; // = 1
        public IntPtr WriteCallback; // delegate*<IntPtr ctx, IntPtr data, UIntPtr size, IntPtr userdata, HeifError>
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HeifError heif_context_write(
        IntPtr ctx,
        ref HeifWriter writer,
        IntPtr userdata);

    // -------- Helpers --------

    public static bool IsSuccess(HeifError err) => err.Code == HeifConstants.HeifErrorOk;

    public static string ErrorMessage(HeifError err)
    {
        try
        {
            if (err.Message == IntPtr.Zero)
            {
                return $"heif_error code={err.Code} subcode={err.Subcode}";
            }
            var msg = Marshal.PtrToStringAnsi(err.Message);
            return string.IsNullOrEmpty(msg)
                ? $"heif_error code={err.Code} subcode={err.Subcode}"
                : $"heif_error code={err.Code} subcode={err.Subcode}: {msg}";
        }
        catch
        {
            return $"heif_error code={err.Code} subcode={err.Subcode}";
        }
    }

    public static string GetVersion()
    {
        try
        {
            var ptr = heif_get_version();
            return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "unknown" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
