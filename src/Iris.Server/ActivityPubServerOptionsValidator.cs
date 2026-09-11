using Iris.Core;
using Microsoft.Extensions.Options;

namespace Iris.Server;

/// <summary>
/// Startup validation for <see cref="ActivityPubServerOptions"/>: makes a misconfigured production
/// deployment fail fast with an actionable error (at host start) instead of surfacing later as a
/// runtime 500 or a silently-misrouted federation request.
/// </summary>
/// <remarks>
/// Validated invariants (Phase 83.2):
/// <list type="bullet">
/// <item><see cref="ActivityPubServerOptions.BaseUri"/> — when set, must be an absolute http(s) IRI
/// (it is used to build absolute IRIs for local actors and in WebFinger; a relative or
/// non-http(s) base would produce malformed actor IRIs that no remote instance can fetch).</item>
/// <item><see cref="ActivityPubServerOptions.InstanceActorId"/> — when set, must be an absolute http(s)
/// IRI (it is the actor the instance signs outbound federation as; a malformed IRI would break
/// every outbound request's signature).</item>
/// <item><see cref="ActivityPubServerOptions.SharedInboxIri"/> — when set, must be an absolute http(s)
/// IRI (it is advertised as the shared-inbox endpoint; a malformed IRI would reject every inbound
/// delivery).</item>
/// </list>
/// Null is allowed for every option (a host may configure a subset); validation only fires when a value
/// is present and is malformed. This keeps the validator non-breaking for hosts that rely on the
/// defaults or configure only some options.
/// </remarks>
public sealed class ActivityPubServerOptionsValidator : IValidateOptions<ActivityPubServerOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ActivityPubServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.BaseUri is { } baseUri && !IsValidHttpIri(baseUri))
        {
            errors.Add($"Iris:BaseUri must be an absolute http(s) IRI (got '{baseUri.Value}'). " +
                "It is used to build absolute IRIs for local actors and in WebFinger.");
        }

        if (options.InstanceActorId is { } actorId && !IsValidHttpIri(actorId))
        {
            errors.Add($"Iris:InstanceActorId must be an absolute http(s) IRI (got '{actorId.Value}'). " +
                "It is the actor the instance signs outbound federation requests as.");
        }

        if (options.SharedInboxIri is { } inbox && !IsValidHttpIri(inbox))
        {
            errors.Add($"Iris:SharedInboxIri must be an absolute http(s) IRI (got '{inbox.Value}'). " +
                "It is advertised as the instance's shared-inbox endpoint.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// An IRI is a valid federation base/actor/inbox IRI when it is absolute and uses the http or
    /// https scheme (the only schemes a remote instance can fetch). A fragment is permitted (an actor
    /// IRI may carry one); the scheme + absoluteness are the invariants that matter here.
    /// </summary>
    private static bool IsValidHttpIri(Iri iri) =>
        iri.IsAbsolute
        && (iri.Uri.Scheme == Uri.UriSchemeHttp || iri.Uri.Scheme == Uri.UriSchemeHttps);
}
