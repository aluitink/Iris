namespace Iris.Server.Observability;

/// <summary>
/// A single observed federation request/response captured by the
/// <see cref="IFederationTraceCollector"/> (Phase 136.1).
/// </summary>
/// <remarks>
/// This is the unit of a federation trace: one inbound (inbox POST) or outbound (delivery) HTTP
/// request with the metadata an operator needs to diagnose interop without re-reading scattered
/// log lines. A trace for a scenario is the set of entries captured while that scenario runs (the
/// collector's <see cref="InMemoryFederationTraceCollector.Snapshot"/>), which can be exported as a
/// single shared JSON artifact (the Phase 136.1 exit criterion).
///
/// <strong>Direction semantics.</strong> <see cref="Direction"/> is always <see cref="FederationDirection.Outbound"/>
/// or <see cref="FederationDirection.Inbound"/> from <em>this instance's</em> point of view. For an outbound
/// delivery, <see cref="PeerIri"/> is the recipient's inbox and <see cref="ActorIri"/> is the local actor the
/// delivery was signed as. For an inbound inbox POST, <see cref="PeerIri"/> is the recipient (the local
/// inbox that received it) and <see cref="ActorIri"/> is the verified remote actor (the signer).
///
/// <strong>No secrets.</strong> The capture deliberately records only metadata (IRIs, the activity type,
/// the HTTP status) — never the request/response body and never the <c>Signature</c> header (which
/// embeds a base64 signature value). This keeps the artifact safe to share and keeps memory bounded.
/// </remarks>
/// <param name="Timestamp">When the request completed (UTC).</param>
/// <param name="Direction">Whether the request left this instance (outbound) or arrived at it (inbound).</param>
/// <param name="Method">The HTTP method (e.g. "POST").</param>
/// <param name="Url">The request URL (the recipient inbox for outbound; the local inbox path for inbound).</param>
/// <param name="Status">The HTTP status code of the response (outbound) or the status the endpoint returned (inbound).</param>
/// <param name="PeerIri">The peer IRI: the recipient inbox (outbound) or the local recipient inbox (inbound).</param>
/// <param name="ActorIri">The actor the request is attributed to: the signing/local actor (outbound) or the
/// verified remote signer (inbound). Null when unavailable (e.g. an unsigned inbound request).</param>
/// <param name="ActivityType">The ActivityStreams activity type (e.g. "Follow", "Create"), or null when the
/// payload could not be determined (e.g. an unrecognizable inbound body).</param>
public sealed record FederationTraceEntry(
    DateTimeOffset Timestamp,
    FederationDirection Direction,
    string Method,
    string Url,
    int Status,
    string PeerIri,
    string? ActorIri,
    string? ActivityType);
