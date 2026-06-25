namespace HeifSharp;

/// <summary>
/// Exception thrown by libheif operations. Wraps both heif_error code/subcode pairs and
/// loader-level failures.
/// </summary>
public sealed class HeifException : Exception
{
    public int ErrorCode { get; }
    public int SubCode { get; }

    public HeifException(int errorCode, string message)
        : base($"libheif error {errorCode}: {message}")
    {
        ErrorCode = errorCode;
        SubCode = 0;
    }

    public HeifException(int errorCode, int subCode, string message)
        : base($"libheif error {errorCode}/{subCode}: {message}")
    {
        ErrorCode = errorCode;
        SubCode = subCode;
    }
}
