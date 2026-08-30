# SampleBlazorClient (Iris Blazor WebAssembly "server explorer") — multi-stage build.
#
# Build context is the repository root (so the SDK stage can resolve the ..\..\src project
# references); the build/publish commands are run from samples/SampleBlazorClient. Unlike
# SampleServer (a Kestrel host), the Blazor WebAssembly app is a *static* site: the browser downloads
# the app from the host, so the runtime stage is a plain static file server (nginx) that serves the
# published _framework + wwwroot content. No .NET runtime is needed at serve time.
#
# The explorer dials instances by host-published base URL (the InstanceBaseUrls surface, SAMPLE_PLAN
# §4.4) — it is a browser app, so it reaches the two Iris servers on their host-published ports
# (8081/8082) or the operator's own address. The iris-ui compose service is routable as `iris-ui` on
# the internal network (and published to the host on 8090) so the smoke test can assert UI reachability.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project + props files first so the restore layer is cached independently of source.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Iris.Core/Iris.Core.csproj src/Iris.Core/
COPY src/Iris.Client/Iris.Client.csproj src/Iris.Client/
COPY src/Iris.Client.Extensions/Iris.Client.Extensions.csproj src/Iris.Client.Extensions/
COPY samples/SampleBlazorClient/SampleBlazorClient.csproj samples/SampleBlazorClient/
RUN dotnet restore samples/SampleBlazorClient/SampleBlazorClient.csproj

# Copy the full source tree and publish the WASM app. The default build is the Blazor WebAssembly
# "server explorer" host (Deliverable B); -p:ConsoleSmoke is not set, so ConsoleSmoke.cs is excluded
# and Program.cs (the WASM host) is the entry point.
COPY . .
RUN dotnet publish samples/SampleBlazorClient/SampleBlazorClient.csproj -c Release -o /app/publish

FROM nginx:1.27-alpine AS runtime
# The Blazor WebAssembly app is static content: the _framework (app binaries + blazor.webassembly.js)
# and wwwroot (index.html, css) are served from nginx's document root. `dotnet publish` lays the static
# content out under a `wwwroot/` subdirectory (plus an IIS `web.config` we do not need), so the
# document root is the publish output's `wwwroot` folder.
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8090
# nginx's default config listens on 80; we remap it to 8090 (the explorer's advertised port) so the
# compose service's EXPOSE/health check and the host mapping (8090 -> 8090) line up.
RUN sed -i 's/listen       80;/listen       8090;/' /etc/nginx/conf.d/default.conf
ENTRYPOINT ["nginx", "-g", "daemon off;"]
