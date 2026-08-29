namespace Iris.Client.Pipeline;

/// <summary>
/// The Basic-auth credentials a client uses to authenticate to the proxy-fallback endpoint.
/// </summary>
/// <remarks>
/// Phase 6's proxy identifies the acting actor from the request's Basic auth (the host app's
/// <c>IActorCredentialValidator</c>) and signs the forwarded request with that actor's key. The
/// client supplies these credentials (typically the acting user's username + password) so the proxy
/// knows whose key to sign with.
/// </remarks>
public sealed record ProxyCredentials(string Username, string Password)
{
    /// <summary>The Basic-auth username (the acting actor's local username).</summary>
    public string Username { get; } = Username ?? throw new ArgumentNullException(nameof(Username));

    /// <summary>The Basic-auth password for <see cref="Username"/>.</summary>
    public string Password { get; } = Password ?? throw new ArgumentNullException(nameof(Password));
}
