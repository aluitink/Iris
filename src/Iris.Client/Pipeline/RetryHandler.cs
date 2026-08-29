using System.Net;
using Iris.Core;

namespace Iris.Client.Pipeline;

/// <summary>
/// A <see cref="DelegatingHandler"/> that retries <em>idempotent</em> ActivityPub requests
/// (GET/HEAD/OPTIONS) on transient failures — 429/5xx responses and network-level
/// <see cref="HttpRequestException"/> — using bounded exponential backoff with jitter, and
/// honoring <c>Retry-After</c> when the server sends it.
/// </summary>
/// <remarks>
/// Non-idempotent requests (POST/PUT/DELETE) are <strong>never</strong> retried: replaying an
/// activity delivery could double-post, so <see cref="IActivityPubClient.DeliverAsync"/> and similar
/// flows pass through unchanged. The retry budget defaults to 3 total attempts (1 + 2 retries).
///
/// The delay between attempts is exponential (base 250 ms, doubling) with up to 100% additive
/// jitter. The delay is performed through an injectable <c>Func&lt;TimeSpan, Task&gt;</c> and the
/// jitter through an injectable <see cref="Random"/>, so unit tests run instantly and
/// deterministically without sleeping.
/// </remarks>
public sealed class RetryHandler : DelegatingHandler
{
    /// <summary>The default maximum total attempts (1 initial + 2 retries).</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>The default base delay between retries (doubles each attempt).</summary>
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(250);

    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;

    /// <summary>
    /// Initializes a new <see cref="RetryHandler"/> with default policy and a real delay.
    /// </summary>
    public RetryHandler()
        : this(null, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="RetryHandler"/> over an explicit inner handler.
    /// </summary>
    /// <param name="innerHandler">The inner handler to forward to.</param>
    public RetryHandler(HttpMessageHandler innerHandler)
        : this(null, innerHandler, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="RetryHandler"/> with a retry budget over an explicit inner
    /// handler.
    /// </summary>
    /// <param name="maxAttempts">Total attempts allowed (minimum 1).</param>
    /// <param name="innerHandler">The inner handler to forward to.</param>
    public RetryHandler(int maxAttempts, HttpMessageHandler innerHandler)
        : this(maxAttempts, innerHandler, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="RetryHandler"/> with a configurable policy.
    /// </summary>
    /// <param name="maxAttempts">Total attempts allowed, or null for <see cref="DefaultMaxAttempts"/>.</param>
    /// <param name="innerHandler">The inner handler to forward to, or null for the default
    /// <see cref="HttpClientHandler"/>.</param>
    /// <param name="delay">The function that waits between attempts. Defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Injectable so tests never actually
    /// sleep.</param>
    /// <param name="random">The source of jitter, or null for a fresh <see cref="Random"/>.</param>
    public RetryHandler(
        int? maxAttempts,
        HttpMessageHandler? innerHandler,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Random? random)
    {
        _maxAttempts = Math.Max(1, maxAttempts ?? DefaultMaxAttempts);
        _baseDelay = DefaultBaseDelay;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _random = random ?? new Random();
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>The total number of attempts (including the first) this handler will make.</summary>
    public int MaxAttempts => _maxAttempts;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Non-idempotent requests are never replayed.
        if (!IsIdempotent(request.Method))
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        var response = default(HttpResponseMessage?);
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage current;
            try
            {
                current = await base.SendAsync(RequestForAttempt(request, attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxAttempts)
            {
                // Transient network failure: back off and retry.
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _maxAttempts)
            {
                // A request timeout surfaces as TaskCanceledException without our token being
                // cancelled; treat it as transient and retry.
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
                continue;
            }

            if (response is not null)
            {
                response.Dispose();
            }

            response = current;

            if (attempt < _maxAttempts && IsTransient(current))
            {
                var delay = GetDelay(current, attempt);
                await _delay(delay, ct).ConfigureAwait(false);
            }
            else
            {
                return response;
            }
        }

        // Exhausted the budget: return the last (still failing) response.
        return response!;
    }

    private static bool IsIdempotent(HttpMethod method)
        => string.Equals(method.Method, HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase)
            || string.Equals(method.Method, HttpMethod.Head.Method, StringComparison.OrdinalIgnoreCase)
            || string.Equals(method.Method, HttpMethod.Options.Method, StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private TimeSpan GetDelay(HttpResponseMessage response, int attempt)
    {
        // Honor Retry-After (delta-seconds form) when present and positive; otherwise fall back
        // to the exponential backoff.
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        return ComputeBackoff(attempt);
    }

    private Task BackoffAsync(int attempt, CancellationToken ct)
        => _delay(ComputeBackoff(attempt), ct);

    private TimeSpan ComputeBackoff(int attempt)
    {
        // Exponential backoff: base * 2^(attempt-1), with up to 100% additive jitter.
        var exponential = _baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = _random.NextDouble() * exponential;
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }

    /// <summary>
    /// Returns the request to send for a given attempt. The first attempt uses the original
    /// request (its content stream is intact); subsequent attempts clone it so a consumed content
    /// stream can be re-sent.
    /// </summary>
    private HttpRequestMessage RequestForAttempt(HttpRequestMessage original, int attempt)
    {
        if (attempt == 1 || original.Content is null)
        {
            return original;
        }

        return Clone(original);
    }

    /// <summary>
    /// Produces an equivalent request whose content is re-buffered, so a consumed content stream
    /// can be sent again on a retry.
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var bytes = original.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var content = new ByteArrayContent(bytes);
            if (original.Content.Headers.ContentType is not null)
            {
                content.Headers.ContentType = original.Content.Headers.ContentType;
            }

            clone.Content = content;
        }

        return clone;
    }
}
