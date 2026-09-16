using System.Net;
using System.Net.Http.Headers;
using Iris.Client;

namespace Iris.Testing;

/// <summary>
/// A test helper that drains an <see cref="IDeliveryQueue"/> synchronously on the calling thread,
/// delivering each job inline via a signed <see cref="IActivityPubClient"/>. Eliminates dependence
/// on the background <c>DeliveryWorker</c> pump task being scheduled, which is the root cause of
/// the flaky round-trip integration tests under full-suite load.
/// </summary>
/// <remarks>
/// The driver loops <c>TryDequeueAsync</c> with a short timeout (50 ms per attempt). When a job is
/// dequeued it is delivered inline. When the timeout expires (queue empty) or the queue returns
/// <c>null</c> (complete and empty), the drain stops. The driver is a best-effort drain: if the
/// background worker races and dequeues a job first, the driver simply finds an empty queue and
/// returns. It does not block waiting for new jobs to arrive.
///
/// Usage:
/// <code>
/// var driver = new DeterministicDeliveryDriver(queue, signedClient);
/// await driver.DrainAsync();
/// // ... or, for a round-trip that involves two hosts:
/// await driverA.DrainAsync();
/// await driverB.DrainAsync();
/// await driverA.DrainAsync(); // second wave (accepts flowing back)
/// await driverB.DrainAsync();
/// </code>
/// </remarks>
public sealed class DeterministicDeliveryDriver
{
    private readonly IDeliveryQueue _queue;
    private readonly IActivityPubClient _client;

    /// <summary>
    /// Creates a new driver that will drain <paramref name="queue"/> using a signed client.
    /// </summary>
    /// <param name="queue">The delivery queue to drain.</param>
    /// <param name="client">The signed ActivityPub client used to POST each job.</param>
    public DeterministicDeliveryDriver(IDeliveryQueue queue, IActivityPubClient client)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// The number of jobs delivered by the most recent <see cref="DrainAsync"/> call.
    /// </summary>
    public int DeliveredCount { get; private set; }

    /// <summary>
    /// The number of jobs that failed (non-2xx) during the most recent <see cref="DrainAsync"/> call.
    /// </summary>
    public int FailedCount { get; private set; }

    /// <summary>
    /// Drains the queue: repeatedly dequeues and delivers jobs inline until the queue is empty
    /// (a 50 ms timeout on <c>TryDequeueAsync</c> signals no more pending work) or
    /// <paramref name="maxJobs"/> is reached.
    /// </summary>
    /// <param name="maxJobs">Maximum number of jobs to deliver in this drain (default 200).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DrainAsync(int maxJobs = 200, CancellationToken ct = default)
    {
        DeliveredCount = 0;
        FailedCount = 0;

        for (var i = 0; i < maxJobs; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            DeliveryJob? job;
            try
            {
                job = await _queue.TryDequeueAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 50 ms timeout — queue is empty for now.
                break;
            }

            if (job is null)
            {
                // Queue is complete and empty.
                break;
            }

            await DeliverInlineAsync(job, ct);
        }
    }

    private async Task DeliverInlineAsync(DeliveryJob job, CancellationToken ct)
    {
        try
        {
            var json = ActivityJson.Serialize(job.Activity);
            var body = System.Text.Encoding.UTF8.GetBytes(json);

            using var request = new HttpRequestMessage(HttpMethod.Post, job.InboxIri.Value)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

            if (job.ActorIri is { } actorIri)
            {
                request.Headers.Add("X-Iris-Actor", actorIri.Value);
            }

            using var response = await _client.SendAsync(request, ct);
            if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                DeliveredCount++;
            }
            else
            {
                FailedCount++;
            }
        }
        catch
        {
            FailedCount++;
        }
    }
}
