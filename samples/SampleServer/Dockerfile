# SampleServer (Iris ActivityPub server) — multi-stage build.
#
# Build context is the repository root (so the SDK stage can resolve the ..\..\src project
# references); the build/publish commands are run from samples/SampleServer. The same image is
# reused for every SampleServer instance in the compose stack (iris-a, iris-b) — the only difference
# between instances is the Iris__HostName / Iris__Port / Iris__Actor environment, which sets the
# advertised base URI (the actor/community IRIs) and the bind URL.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project + props files first so the restore layer is cached independently of source.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Iris.Core/Iris.Core.csproj src/Iris.Core/
COPY src/Iris.Server/Iris.Server.csproj src/Iris.Server/
COPY src/Iris.Server.InMemory/Iris.Server.InMemory.csproj src/Iris.Server.InMemory/
COPY samples/SampleServer/SampleServer.csproj samples/SampleServer/
RUN dotnet restore samples/SampleServer/SampleServer.csproj

# Copy the full source tree and publish (trimming off; the sample is a small self-contained web app).
COPY . .
RUN dotnet publish samples/SampleServer/SampleServer.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# The sample binds via UseUrls from Iris__HostName / Iris__Port (see CreateWebHostBuilder); EXPOSE is
# documentation only (the bind host is the routable compose hostname, not 0.0.0.0).
EXPOSE 8080
# Default env; the compose file overrides Iris__HostName / Iris__Port / Iris__Actor per instance.
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SampleServer.dll"]
