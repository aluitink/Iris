using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Resolves a remote account (WebFinger <c>resource</c>, e.g. <c>acct:bob@b.test</c>) to the actor IRI
/// it points to.
/// </summary>
/// <remarks>
/// The default implementation is <see cref="WebFingerClient"/>. The abstraction exists so higher layers
/// (e.g. the server's outbound account-resolution path) can depend on the contract and be tested with a
/// fake, without depending on the concrete HTTP client.
/// </remarks>
public interface IWebFingerResolver
{
    /// <summary>
    /// Resolves the actor IRI for the given account via WebFinger.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@user@example.com</c>) or a full
    /// <c>acct:</c> URI.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor IRI, or null if discovery failed or no <c>self</c> link was found.</returns>
    public Task<Iri?> ResolveActorAsync(string account, CancellationToken ct = default);
}
