using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SystemIdentity"/>.
/// </summary>
public class SystemIdentityTests
{
    private static readonly Iri Actor = new("https://a.domain.local/u/alice");
    private static readonly Iri Key = new("https://a.domain.local/u/alice#main-key");

    [Fact]
    public void ExposesActorAndKeyIds()
    {
        IIdentity identity = new SystemIdentity(Actor, Key);

        Assert.Equal(Actor, identity.ActorId);
        Assert.Equal(Key, identity.KeyId);
    }

    [Fact]
    public void IsValueComparable()
    {
        var a = new SystemIdentity(Actor, Key);
        var b = new SystemIdentity(Actor, Key);
        var otherKey = new Iri("https://a.domain.local/u/bob#main-key");
        var otherActor = new Iri("https://b.domain.local/u/bob");

        Assert.Equal(a, b);
        Assert.NotEqual(a, new SystemIdentity(Actor, otherKey));
        Assert.NotEqual(a, new SystemIdentity(otherActor, Key));
    }
}
