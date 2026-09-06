using System.Security.Claims;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace Iris.Web.Accounts;

/// <summary>
/// The name of the custom claim that carries the signed-in user's linked actor IRI.
/// </summary>
public static class ActorClaims
{
    /// <summary>
    /// The claim key for the linked actor's IRI (the account's federated identity).
    /// </summary>
    public const string ActorIri = "actor_iri";
}

/// <summary>
/// The scoped, per-circuit accessor that binds a signed-in user's browser session to their local
/// ActivityPub actor. Reads the user's claims (the <see cref="ActorClaims.ActorIri"/> custom claim and
/// the user id) from the <see cref="AuthenticationStateProvider"/> and exposes an
/// <see cref="IActivityPubClient"/> bound to that actor's identity.
/// </summary>
/// <remarks>
/// This is the one sanctioned path from the UI down into ActivityPub state: every post / follow /
/// like / moderation action / media upload goes through <see cref="Client"/> (an
/// <see cref="IActivityPubClient"/> making signed requests against <c>Iris.Server</c>'s
/// <c>/ap/v1/...</c> routes), exactly like any other authenticated action. Components gate on
/// <c>AuthorizeView</c> and never reach around the client into the store interfaces directly.
///
/// The signing key is loaded in-process from the <see cref="Iris.Core.Identity.IKeyStore"/> (no
/// self-hosted-loopback HTTP hop to fetch a key the app already has local access to). When the user
/// is signed out, <see cref="IsSignedIn"/> is false and <see cref="Client"/> is null — components
/// render the signed-out experience instead.
/// </remarks>
public interface IActorSessionAccessor
{
    /// <summary>
    /// Whether a user is currently signed in.
    /// </summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// The signed-in user's id (the <c>sub</c> claim), or null when signed out.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// The signed-in user's linked actor IRI, or null when signed out.
    /// </summary>
    Iri? ActorId { get; }

    /// <summary>
    /// The signed-in user's role (<see cref="Iris.Server.Data.Accounts.UserRole"/>), or null when signed out.
    /// </summary>
    string? Role { get; }

    /// <summary>
    /// An <see cref="IActivityPubClient"/> bound to the signed-in user's actor (lazily created, cached
    /// for the circuit's lifetime). Null when signed out.
    /// </summary>
    IActivityPubClient? Client { get; }
}

/// <summary>
/// The default <see cref="IActorSessionAccessor"/>. Reads the current <see cref="AuthenticationState"/>
/// (the cookie-auth claims minted by registration/login) and, when signed in, builds a cached
/// <see cref="IActivityPubClient"/> bound to the user's actor via the <see cref="IActivityPubClientFactory"/>
/// (already registered by <c>AddActivityPubServer</c>).
/// </summary>
public sealed class ActorSessionAccessor : IActorSessionAccessor
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly IActivityPubClientFactory _clientFactory;
    private AuthenticationState? _state;
    private IActivityPubClient? _client;

    /// <summary>
    /// Initializes the accessor.
    /// </summary>
    /// <param name="authentication">The Blazor <see cref="AuthenticationStateProvider"/> (one per circuit).</param>
    /// <param name="clientFactory">The ActivityPub client factory.</param>
    public ActorSessionAccessor(AuthenticationStateProvider authentication, IActivityPubClientFactory clientFactory)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    private static bool IsAuthenticated(AuthenticationState? state)
        => state is not null && state.User.Identity is { IsAuthenticated: true };

    /// <inheritdoc/>
    public bool IsSignedIn
    {
        get
        {
            _state ??= _authentication.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return IsAuthenticated(_state);
        }
    }

    /// <inheritdoc/>
    public Guid? UserId
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            var sub = _state!.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    /// <inheritdoc/>
    public Iri? ActorId
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            var value = _state!.User.FindFirstValue(ActorClaims.ActorIri);
            return value is not null && Iri.TryParse(value, out var iri) ? (Iri?)iri : null;
        }
    }

    /// <inheritdoc/>
    public string? Role
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            return _state!.User.FindFirstValue(ClaimTypes.Role);
        }
    }

    /// <inheritdoc/>
    public IActivityPubClient? Client
    {
        get
        {
            if (!IsSignedIn)
            {
                return null;
            }

            if (_client is not null)
            {
                return _client;
            }

            var actorId = ActorId;
            if (actorId is null)
            {
                return null;
            }

            _client = _clientFactory.Create(new ActivityPubClientOptions { ActorId = actorId }, new HttpClientHandler());
            return _client;
        }
    }
}
