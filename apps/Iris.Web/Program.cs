using Iris.Web;

// The Iris production app entry point. The composition root (service + pipeline wiring) lives in
// WebAppFactory (a public, testable seam) so the same host can be booted in-process by the integration
// tests. This file only creates the builder, reads the advertised base from configuration, builds the
// app, and runs it. Slice 32.1 is the bare host: it boots the ActivityPub server (Iris.Server,
// unchanged) with in-memory persistence and a single seeded local actor, and serves a minimal Blazor
// landing page. Persistence, auth, and the product screens arrive in later slices (32.2/32.3/32.4).
var builder = WebApplication.CreateBuilder(args);
var advertisedBase = builder.Configuration["Iris:AdvertiseBase"];
var app = WebAppFactory.CreateWebApplication(builder, advertisedBase);
app.Run();
