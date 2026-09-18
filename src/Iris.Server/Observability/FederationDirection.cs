namespace Iris.Server.Observability;

/// <summary>
/// The direction of a federation request relative to this instance (Phase 136.1).
/// </summary>
public enum FederationDirection
{
    /// <summary>
    /// The request left this instance (an outbound delivery to a remote peer's inbox).
    /// </summary>
    Outbound,

    /// <summary>
    /// The request arrived at this instance (an inbound inbox POST from a remote peer).
    /// </summary>
    Inbound,
}
