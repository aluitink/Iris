using Iris.Core;
using Iris.Server.Caching;
using Iris.Server.Inbox;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 33.1 test: structured logging + diagnostics. The server logs actionable diagnostics without a
/// dedicated logging package: the <c>InboxProcessor</c> logs every inbound activity (type, id, actor,
/// recipient) and its dispatch outcome (ok / re-delivery skip / no handler / handler failure), the
/// activity-handler base logs each handler's outcome, and the <c>HttpSignatureValidator</c> logs the
/// specific reason a signature was rejected (malformed header / unparseable keyId / unresolvable key /
/// cryptographic failure). The delivery worker was already fully logged (per-attempt, per-dead-letter).
/// </summary>
/// <remarks>
/// Log capture is done by constructing the production components directly with a capturing
/// <see cref="ILoggerProvider"/> (the host factory's <c>ConfigureLogging(ClearProviders)</c> overrides any
/// service-level provider a test would inject, so the in-process host cannot capture logs). Two scenarios
/// are exercised against real production code: (1) an <c>InboxProcessor</c> wired with the real
/// <c>LikeActivityHandler</c> dispatches a Like and emits its inbox-received / dispatched-ok /
/// handler-ok log entries; (2) an <c>HttpSignatureValidator</c> given a malformed <c>Signature</c> header
/// returns invalid and emits the rejection-reason log entry. The full signature-validated inbound path
/// (a valid signed activity accepted end-to-end) is already covered by the existing integration tests;
/// this file pins the log output those paths now produce.
/// </remarks>
public sealed class StructuredLoggingIntegrationTests
{
    private const string AHost = "log-a.domain.local";
    private const string Alice = "alice";

    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _alice;

    public StructuredLoggingIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var (_, aliceIri, _) = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _alice = aliceIri;
    }

    // --- The inbox processor logs the activity it receives and its dispatch outcome ------------
    //
    // The InboxProcessor is the single owner of "receive an activity"; it must log what it received
    // (type, id, actor, recipient) and the outcome of dispatching it to the handler. Constructed here
    // with the real LikeActivityHandler + a capturing logger so the log entries are observable.

    [Fact]
    public async Task InboxProcessor_LogsReceivedActivity_AndDispatchOutcome()
    {
        var logProvider = new CapturingLogProvider();
        var likeHandler = new LikeActivityHandler(_persistence, new DefaultLocalActorResolver(_persistence), logProvider.CreateLogger<LikeActivityHandler>());
        InboxProcessor processor = new(_persistence, [likeHandler], logProvider.CreateLogger<InboxProcessor>());

        var like = new Like
        {
            Id = $"https://{AHost}/activities/like-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_alice.Value) }],
            Object = [new Link { Href = new Uri($"https://{AHost}/objects/liked-{Guid.NewGuid():N}") }],
        };
        await processor.ProcessAsync(new InboxDelivery(_alice, like));

        // The processor logged the activity's arrival and the successful dispatch to the handler.
        Assert.Contains(logProvider.Entries, e =>
            e.LogLevel == LogLevel.Information
            && e.Message.Contains("Inbox received Like"));
        Assert.Contains(logProvider.Entries, e =>
            e.LogLevel == LogLevel.Information
            && e.Message.Contains("Inbox dispatched Like")
            && e.Message.Contains("LikeActivityHandler"));
        // The handler base logged its own ok outcome.
        Assert.Contains(logProvider.Entries, e =>
            e.LogLevel == LogLevel.Information
            && e.Message.Contains("Handler LikeActivityHandler processed"));
    }

    // --- The signature validator logs the specific reason a malformed signature is rejected ----
    //
    // A production 401 is only actionable if the log says WHY the signature failed. The validator logs
    // the malformed-header reason (distinct from unknown-key / cryptographic-failure reasons).

    [Fact]
    public async Task Validator_LogsRejectionReason_WhenSignatureHeaderIsMalformed()
    {
        var logProvider = new CapturingLogProvider();
        var validator = new HttpSignatureValidator(
            new InMemoryInboundKeyResolver(),
            new HttpSignatureVerifier(new InMemoryKeyStore()),
            logger: logProvider.CreateLogger<HttpSignatureValidator>());

        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = "POST",
                Path = $"/ap/v1/u/{Alice}/inbox",
                Body = new MemoryStream(),
            },
        };
        context.Request.Headers.Host = AHost;
        context.Request.Headers[Signatures.SignatureHeaderName] = "malformed-not-a-signature";

        var result = await validator.ValidateAsync(context);

        // The validator rejected the (unparseable) signature and logged the specific reason.
        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.Contains(logProvider.Entries, e =>
            e.LogLevel == LogLevel.Warning
            && e.Message.Contains("Signature rejected: malformed Signature header"));
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// A no-op inbound key resolver (the malformed-header branch is reached before key resolution, so
    /// this stub is never invoked; it exists only to satisfy the validator's constructor).
    /// </summary>
    private sealed class InMemoryInboundKeyResolver : IInboundKeyResolver
    {
        public Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default)
            => Task.FromResult<ISigningKey?>(null);
    }

    /// <summary>
    /// Captures log entries emitted through a <see cref="ILogger"/> built by this provider. Thread-safe.
    /// </summary>
    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get { lock (_gate) { return _entries.ToList(); } }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger<object>(this, categoryName);

        public ILogger<T> CreateLogger<T>(string? categoryName = null) => new CapturingLogger<T>(this, categoryName ?? typeof(T).Name);

        private void Record(LogLevel level, string category, string message, Exception? exception)
        {
            lock (_gate)
            {
                _entries.Add(new CapturedLogEntry(level, category, message, exception));
            }
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger<T>(CapturingLogProvider owner, string categoryName) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Record(logLevel, categoryName, formatter(state, exception), exception);
            }
        }
    }

    private sealed record CapturedLogEntry(LogLevel LogLevel, string Category, string Message, Exception? Exception);
}
