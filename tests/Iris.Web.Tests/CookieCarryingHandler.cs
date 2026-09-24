using System.Net;
using System.Net.Http;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// A <see cref="DelegatingHandler"/> that stores and re-sends cookies (a minimal
/// <see cref="CookieContainer"/>-backed handler) so the test client can hold the session cookie the
/// <c>POST /login</c> endpoint issues.
/// </summary>
public sealed class CookieCarryingHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("No request URI.");
        var cookieHeader = _cookies.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        if (setCookie.Count > 0)
        {
            _cookies.SetCookies(uri, string.Join(";", setCookie));
        }

        return response;
    }
}
