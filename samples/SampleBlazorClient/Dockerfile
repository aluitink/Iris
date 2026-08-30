# SampleBlazorClient (Iris Blazor WebAssembly "server explorer") — multi-stage build.
#
# Build context is the repository root (so the SDK stage can resolve the ..\..\src project
# references); the build/publish commands are run from samples/. The Blazor WebAssembly app is a
# *static* site — the browser downloads index.html + _framework and runs the app client-side — so the
# runtime stage is a minimal ASP.NET Core static-file host (the same aspnet base image family
# SampleServer uses) that serves the published WASM output (index.html + _framework) from the WebRoot
# on port 8090.
#
# Two publishes (both in the SDK stage):
#   1. the Blazor WebAssembly site (samples/SampleBlazorClient, default build → /wasm-site) —
#      index.html + _framework + css, served as the host's WebRoot;
#   2. the static-file host (samples/IrisStaticHost → /app) — a tiny plain ASP.NET Core app whose only
#      job is UseDefaultFiles/UseStaticFiles/MapFallbackToFile on port 8090 (see IrisStaticHost).
# The two are combined in the runtime stage (the host's WebRoot is the WASM site's wwwroot folder).
#
# The explorer dials instances by host-published base URL (the InstanceBaseUrls surface, SAMPLE_PLAN
# §4.4) — it is a browser app, so it reaches the two Iris servers on their host-published ports
# (8081/8082) or the operator's own address. The iris-ui compose service is routable as `iris-ui` on
# the internal network (and published to the host on 8090) so the smoke test can assert UI
# reachability. The static host makes no outbound network calls of its own.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project + props files first so the restore layer is cached independently of source.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Iris.Core/Iris.Core.csproj src/Iris.Core/
COPY src/Iris.Client/Iris.Client.csproj src/Iris.Client/
COPY src/Iris.Client.Extensions/Iris.Client.Extensions.csproj src/Iris.Client.Extensions/
COPY samples/SampleBlazorClient/SampleBlazorClient.csproj samples/SampleBlazorClient/
COPY samples/IrisStaticHost/IrisStaticHost.csproj samples/IrisStaticHost/
RUN dotnet restore samples/SampleBlazorClient/SampleBlazorClient.csproj \
    && dotnet restore samples/IrisStaticHost/IrisStaticHost.csproj

# Copy the full source tree and publish both the WASM site (default build) and the static-file host.
# The default SampleBlazorClient build is the Blazor WebAssembly "server explorer" host (Deliverable
# B); the IrisStaticHost project is a plain ASP.NET Core app (the WASM SDK cannot produce one — it
# pins the browser-wasm/Mono target).
COPY . .
RUN dotnet publish samples/SampleBlazorClient/SampleBlazorClient.csproj -c Release -o /wasm-site \
    && dotnet publish samples/IrisStaticHost/IrisStaticHost.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The static host serves the WASM site from the WebRoot (the publish output's wwwroot folder, which
# holds index.html, _framework, and css) on port 8090. See the IrisStaticHost source (the runtime
# stage's entry point) for the minimal Kestrel + UseStaticFiles pipeline.
COPY --from=build /wasm-site/wwwroot /app/wwwroot
ENV ASPNETCORE_URLS=http://+:8090
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8090
ENTRYPOINT ["dotnet", "IrisStaticHost.dll"]
