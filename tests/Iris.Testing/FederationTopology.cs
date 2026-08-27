namespace Iris.Testing;

/// <summary>
/// An N-instance federation topology: multiple in-process <see cref="TestServerInstance"/>
/// instances with distinct <c>*.domain.local</c> hostnames, designed for relay/fan-out and
/// instance-to-instance federation tests. Start with 2 (A and B); scale to N.
/// </summary>
public sealed class FederationTopology : IDisposable
{
    private readonly List<TestServerInstance> _instances = [];

    private FederationTopology()
    {
    }

    /// <summary>
    /// The instances in creation order.
    /// </summary>
    public IReadOnlyList<TestServerInstance> Instances => _instances;

    /// <summary>
    /// Gets the first instance (hostname <c>a.domain.local</c>).
    /// </summary>
    public TestServerInstance InstanceA => _instances[0];

    /// <summary>
    /// Gets the second instance (hostname <c>b.domain.local</c>), when at least two exist.
    /// </summary>
    public TestServerInstance InstanceB => _instances[1];

    /// <summary>
    /// Creates a topology of <paramref name="count"/> in-process server instances with
    /// hostnames <c>a.domain.local</c>, <c>b.domain.local</c>, <c>c.domain.local</c>, ….
    /// </summary>
    /// <param name="count">The number of instances to create (minimum 1).</param>
    /// <returns>A running <see cref="FederationTopology"/>.</returns>
    public static FederationTopology Create(int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Must be at least 1.");
        }

        var topology = new FederationTopology();
        for (var i = 0; i < count; i++)
        {
            var hostname = $"{Letter(i)}{TestServerFactory.HostnameSuffix}";
            topology._instances.Add(
                TestServerFactory.CreateInstance(hostname));
        }

        return topology;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var instance in _instances)
        {
            instance.Dispose();
        }

        _instances.Clear();
    }

    /// <summary>
    /// Maps a zero-based index to a lowercase letter (0→a, 1→b, … 25→z, 26→aa, …).
    /// </summary>
    private static string Letter(int index)
    {
        var result = string.Empty;
        var n = index;
        do
        {
            result = (char)('a' + (n % 26)) + result;
            n /= 26;
        }
        while (n > 0);

        return result;
    }
}
