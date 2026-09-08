# Iris.Web (the production social platform app) — multi-stage build.
#
# Build context is the repository root (so the SDK stage can resolve the src/ project references);
# the build/publish commands are run from apps/Iris.Web. The compose file (apps/Iris.Web/
# docker-compose.yml) sets `build.context` to `../..` and `dockerfile` to `apps/Iris.Web/Dockerfile`.
#
# The runtime stage is the app container (API + UI in one process). It binds in-container port 8080
# (ASPNETCORE_URLS below); the compose file maps host port 8088 -> container 8080 so the production
# app takes over the existing https://iris.luit.ink reverse-proxy target with no reconfiguration
# (see docs/plans/production-app-overview.md §2 and production-app-deployment.md §1).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project + props files first so the restore layer is cached independently of source.
# The full transitive closure of apps/Iris.Web's ProjectReferences (all six src/ projects + the app).
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Iris.Core/Iris.Core.csproj src/Iris.Core/
COPY src/Iris.Client/Iris.Client.csproj src/Iris.Client/
COPY src/Iris.Client.Extensions/Iris.Client.Extensions.csproj src/Iris.Client.Extensions/
COPY src/Iris.Server/Iris.Server.csproj src/Iris.Server/
COPY src/Iris.Server.InMemory/Iris.Server.InMemory.csproj src/Iris.Server.InMemory/
COPY src/Iris.Server.Data/Iris.Server.Data.csproj src/Iris.Server.Data/
COPY apps/Iris.Web/Iris.Web.csproj apps/Iris.Web/
COPY apps/Iris.Web.Client/Iris.Web.Client.csproj apps/Iris.Web.Client/
RUN dotnet restore apps/Iris.Web/Iris.Web.csproj && dotnet restore apps/Iris.Web.Client/Iris.Web.Client.csproj

# Copy the full source tree and publish. UseAppHost=false -> a portable apphost-less DLL (the runtime
# stage's shared framework provides the host), matching the sample Dockerfiles.
COPY . .
RUN dotnet build apps/Iris.Web.Client/Iris.Web.Client.csproj -c Release && dotnet publish apps/Iris.Web/Iris.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Create a non-root user to run the app (50.1 security hardening). The Data Protection keys
# volume is mounted at /home/iris/.aspnet/DataProtection-Keys (see docker-compose.yml).
RUN groupadd --gid 1001 iris && useradd --uid 1001 --gid iris --home-dir /home/iris --shell /usr/sbin/nologin iris
WORKDIR /app
COPY --from=build /app/publish .
RUN chown -R iris:iris /app
# Startup script: runs briefly as root to fix the DP keys volume ownership (Docker named
# volumes are created root-owned), then drops to the iris user for the app process.
RUN printf '#!/bin/sh\nchown -R 1001:1001 /home/iris/.aspnet 2>/dev/null || true\nexec setpriv --reuid=1001 --regid=1001 --clear-groups dotnet Iris.Web.dll\n' > /entrypoint.sh && chmod +x /entrypoint.sh
USER root
# The app binds in-container port 8080; the compose file publishes it on host port 8088 (the
# iris.luit.ink reverse-proxy target). The advertised public base URI is set via the
# Iris__AdvertiseBase env var in the compose file (not here) — it is the operator's public hostname.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["/entrypoint.sh"]
