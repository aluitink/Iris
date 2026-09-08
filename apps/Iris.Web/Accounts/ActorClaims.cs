namespace Iris.Web.Accounts;

/// <summary>
/// The custom claim keys for the local-account auth cookie.
/// </summary>
public static class ActorClaims
{
    /// <summary>
    /// The claim key for the linked actor's IRI (the account's federated identity).
    /// </summary>
    public const string ActorIri = "actor_iri";
}
