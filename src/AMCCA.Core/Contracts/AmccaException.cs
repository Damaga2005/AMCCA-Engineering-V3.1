using System;

namespace AMCCA.Core.Contracts;

public class AmccaException : Exception
{
    public string ErrorCode { get; }
    public ErrorCategory Category { get; }
    public bool Retryable { get; }

    /// <summary>
    /// How long the caller should wait before retrying, when the source knows (e.g. an HTTP 429
    /// <c>Retry-After</c> header). Null when unknown — the retry strategy then uses its own backoff.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public AmccaException(
        string errorCode,
        ErrorCategory category,
        string message,
        bool retryable = false,
        Exception? innerException = null,
        TimeSpan? retryAfter = null)
        : base($"[{errorCode}] {message}", innerException)
    {
        ErrorCode = errorCode;
        Category = category;
        Retryable = retryable;
        RetryAfter = retryAfter;
    }
}
