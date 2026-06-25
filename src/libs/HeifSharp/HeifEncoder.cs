using System.Buffers;
using System.Runtime.InteropServices;
using HeifSharp.Native;

namespace HeifSharp;

/// <summary>
/// High-level HEIC encoder. Each instance owns one libheif context and one HEVC encoder,
/// and is reusable for encoding many frames sequentially. Not thread-safe — wrap in a
/// pool or per-thread instance for parallel encoding.
/// </summary>
public sealed class HeifEncoder : IDisposable
{
    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _encoder = IntPtr.Zero;
    private bool _disposed;

    public HeifEncoderConfig Config { get; }

    public HeifEncoder(HeifEncoderConfig? config = null)
    {
        Config = config ?? HeifEncoderConfig.AppleSafeDefaults();
        Config.Validate();

        // Register vendored plugins (e.g. libheif-plugin-x265) sitting next to libheif —
        // libheif 1.18+ no longer auto-discovers plugins outside its install prefix.
        NativeLibraryLoader.EnsurePluginsLoaded();

        _ctx = HeifNative.heif_context_alloc();
        if (_ctx == IntPtr.Zero)
        {
            throw new HeifException(HeifConstants.HeifErrorMemoryAllocationError, "heif_context_alloc returned null");
        }

        var err = HeifNative.heif_context_get_encoder_for_format(_ctx, HeifCompression.Hevc, out _encoder);
        if (!HeifNative.IsSuccess(err) || _encoder == IntPtr.Zero)
        {
            HeifNative.heif_context_free(_ctx);
            _ctx = IntPtr.Zero;
            throw new HeifException(
                err.Code,
                err.Subcode,
                "Could not get HEVC encoder. Ensure libheif-plugin-x265 (or kvazaar) is installed " +
                "and discoverable via the libheif plugin path. " +
                HeifNative.ErrorMessage(err));
        }

        ApplyConfig();
    }

    private void ApplyConfig()
    {
        // Critical params: fail loudly if these don't apply.
        Throw(HeifNative.heif_encoder_set_lossless(_encoder, Config.Lossless ? 1 : 0), "set_lossless");
        if (!Config.Lossless)
        {
            Throw(HeifNative.heif_encoder_set_lossy_quality(_encoder, Config.Quality), "set_lossy_quality");
        }
        // Encoder-specific params: not every libheif build ships the x265 plugin, and other
        // backends (kvazaar, AOM-for-AVIF) reject some keys. Mirroring OpusSharp's pattern,
        // we log-and-continue here — if a param is rejected, the encoder falls back to its
        // own defaults which still produce a valid file. Apple-compat tests will then catch
        // any output that's not actually decode-clean on watchOS.
        TryApplyString("preset", Config.Preset);
        TryApplyString("tune", Config.Tune);
        TryApplyString("profile", Config.Profile);
        TryApplyString("chroma", Config.Chroma);
    }

    private void TryApplyString(string name, string value)
    {
        var err = HeifNative.heif_encoder_set_parameter_string(_encoder, name, value);
        if (!HeifNative.IsSuccess(err))
        {
            System.Diagnostics.Debug.WriteLine(
                $"HeifEncoder: ignoring unsupported parameter {name}={value} ({HeifNative.ErrorMessage(err)})");
        }
    }

    /// <summary>
    /// Encode a single RGBA frame (8 bits per channel, top-down, no row padding) into a
    /// HEIC byte array. Width and height must both be even — Apple's HEVC HW decoder
    /// rejects odd dimensions.
    /// </summary>
    public byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ThrowIfDisposed();

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"width/height must be positive (got {width}x{height})");
        }
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException(
                $"width and height must be even for Apple-compatible HEIC (got {width}x{height})",
                nameof(width));
        }
        var expected = checked(width * height * 4);
        if (rgba.Length < expected)
        {
            throw new ArgumentException(
                $"rgba buffer is {rgba.Length} bytes; expected at least {expected} for {width}x{height} RGBA8",
                nameof(rgba));
        }

        var imgErr = HeifNative.heif_image_create(width, height, HeifColorspace.Rgb, HeifChroma.InterleavedRgba, out var image);
        if (!HeifNative.IsSuccess(imgErr) || image == IntPtr.Zero)
        {
            throw new HeifException(imgErr.Code, imgErr.Subcode, "heif_image_create failed: " + HeifNative.ErrorMessage(imgErr));
        }

        try
        {
            // RGBA8: single interleaved plane on Channel.Interleaved at 8 bits.
            var planeErr = HeifNative.heif_image_add_plane(image, HeifChannel.Interleaved, width, height, 8);
            if (!HeifNative.IsSuccess(planeErr))
            {
                throw new HeifException(planeErr.Code, planeErr.Subcode, "heif_image_add_plane failed: " + HeifNative.ErrorMessage(planeErr));
            }

            var planePtr = HeifNative.heif_image_get_plane(image, HeifChannel.Interleaved, out var stride);
            if (planePtr == IntPtr.Zero)
            {
                throw new HeifException(HeifConstants.HeifErrorEncodingError, "heif_image_get_plane returned null");
            }

            CopyRgbaIntoPlane(rgba, planePtr, stride, width, height);

            // Tag the image with NCLX BT.709 so Apple decoders pick the right colour matrix.
            ApplyNclxBt709(image);

            // Encode.
            var encErr = HeifNative.heif_context_encode_image(_ctx, image, _encoder, IntPtr.Zero, out var handle);
            if (!HeifNative.IsSuccess(encErr))
            {
                throw new HeifException(encErr.Code, encErr.Subcode, "heif_context_encode_image failed: " + HeifNative.ErrorMessage(encErr));
            }
            if (handle != IntPtr.Zero)
            {
                HeifNative.heif_image_handle_release(handle);
            }

            return WriteContextToBytes();
        }
        finally
        {
            HeifNative.heif_image_release(image);
        }
    }

    private static unsafe void CopyRgbaIntoPlane(ReadOnlySpan<byte> rgba, IntPtr planePtr, int stride, int width, int height)
    {
        var rowBytes = width * 4;
        if (stride == rowBytes)
        {
            // Single contiguous copy.
            fixed (byte* src = rgba)
            {
                Buffer.MemoryCopy(src, planePtr.ToPointer(), rgba.Length, (long)height * rowBytes);
            }
        }
        else
        {
            // libheif may pad rows; copy row-by-row.
            fixed (byte* src = rgba)
            {
                var dst = (byte*)planePtr;
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * stride, stride, rowBytes);
                }
            }
        }
    }

    private static void ApplyNclxBt709(IntPtr image)
    {
        var nclx = HeifNative.heif_nclx_color_profile_alloc();
        if (nclx == IntPtr.Zero)
        {
            // Non-fatal: the image still encodes, just without an explicit NCLX tag.
            return;
        }
        try
        {
            var profile = new HeifNative.HeifColorProfileNclx
            {
                Version = 1,
                ColorPrimaries = 1,        // BT.709
                TransferCharacteristics = 1, // BT.709
                MatrixCoefficients = 1,    // BT.709
                FullRangeFlag = 0,         // limited (TV) range — what Apple ships
            };
            Marshal.StructureToPtr(profile, nclx, fDeleteOld: false);
            HeifNative.heif_image_set_nclx_color_profile(image, nclx);
        }
        finally
        {
            HeifNative.heif_nclx_color_profile_free(nclx);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate HeifNative.HeifError WriteCallbackDelegate(IntPtr ctx, IntPtr data, UIntPtr size, IntPtr userdata);

    private static readonly WriteCallbackDelegate s_writeCallback = WriteCallback;
    private static readonly IntPtr s_writeCallbackPtr = Marshal.GetFunctionPointerForDelegate(s_writeCallback);

    private static HeifNative.HeifError WriteCallback(IntPtr ctx, IntPtr data, UIntPtr size, IntPtr userdata)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is MemoryStream stream)
            {
                var len = (int)size.ToUInt32();
                if (len > 0 && data != IntPtr.Zero)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(len);
                    try
                    {
                        Marshal.Copy(data, buffer, 0, len);
                        stream.Write(buffer, 0, len);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            return default; // zero-init = success
        }
        catch
        {
            return new HeifNative.HeifError { Code = HeifConstants.HeifErrorEncodingError, Subcode = 0, Message = IntPtr.Zero };
        }
    }

    private byte[] WriteContextToBytes()
    {
        using var stream = new MemoryStream();
        var handle = GCHandle.Alloc(stream);
        try
        {
            var writer = new HeifNative.HeifWriter
            {
                WriterApiVersion = 1,
                WriteCallback = s_writeCallbackPtr,
            };
            var err = HeifNative.heif_context_write(_ctx, ref writer, GCHandle.ToIntPtr(handle));
            if (!HeifNative.IsSuccess(err))
            {
                throw new HeifException(err.Code, err.Subcode, "heif_context_write failed: " + HeifNative.ErrorMessage(err));
            }
        }
        finally
        {
            handle.Free();
        }
        return stream.ToArray();
    }

    private static void Throw(HeifNative.HeifError err, string what)
    {
        if (HeifNative.IsSuccess(err)) return;
        throw new HeifException(err.Code, err.Subcode, $"libheif {what} failed: {HeifNative.ErrorMessage(err)}");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HeifEncoder));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (_encoder != IntPtr.Zero)
        {
            try { HeifNative.heif_encoder_release(_encoder); } catch { /* ignore */ }
            _encoder = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero)
        {
            try { HeifNative.heif_context_free(_ctx); } catch { /* ignore */ }
            _ctx = IntPtr.Zero;
        }
        _disposed = true;
    }

    ~HeifEncoder()
    {
        Dispose(false);
    }
}
