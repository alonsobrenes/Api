// EPApi/Services/RateLimitException.cs
using System;

namespace EPApi.Services
{
    public sealed class RateLimitException : Exception
    {
        public int? RetryAfterSeconds { get; }
        public RateLimitException(string message, int? retryAfterSeconds = null) : base(message)
        {
            RetryAfterSeconds = retryAfterSeconds;
        }
    }
}
