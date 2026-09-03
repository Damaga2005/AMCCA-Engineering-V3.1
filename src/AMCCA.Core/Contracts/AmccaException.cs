using System;

namespace AMCCA.Core.Contracts;

public class AmccaException : Exception
{
    public string ErrorCode { get; }
    public ErrorCategory Category { get; }
    public bool Retryable { get; }

    public AmccaException(string errorCode, ErrorCategory category, string message, bool retryable = false, Exception? innerException = null)
        : base($"[{errorCode}] {message}", innerException)
    {
        ErrorCode = errorCode;
        Category = category;
        Retryable = retryable;
    }
}
