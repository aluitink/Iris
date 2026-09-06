using System.Security.Claims;
using Iris.Server.Data.Accounts;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Iris.Web.Accounts;

/// <summary>
/// Builds the cookie-auth <see cref="ClaimsIdentity"/> for a signed-in account. The claims schema is
/// fixed (the auth plan §5): the account id as <c>sub</c>, the username, the linked actor IRI (custom
/// claim <see cref="ActorClaims.ActorIri"/>), and the role. <see cref="IActorSessionAccessor"/> reads
/// the actor-IRI claim to bind the user's <see cref="Iris.Client.IActivityPubClient"/>.
/// </summary>
public static class ClaimsFactory
{
    /// <summary>
    /// Builds the claims identity for the given account.
    /// </summary>
    /// <param name="account">The signed-in account. Must not be null.</param>
    /// <returns>A <see cref="ClaimsIdentity"/> carrying the user id, username, actor IRI, and role.</returns>
    public static ClaimsIdentity CreateIdentity(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var identity = new ClaimsIdentity(
            authenticationType: CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, account.Username));
        identity.AddClaim(new Claim(ActorClaims.ActorIri, account.ActorId.Value));
        identity.AddClaim(new Claim(ClaimTypes.Role, account.Role.ToString()));
        return identity;
    }
}
