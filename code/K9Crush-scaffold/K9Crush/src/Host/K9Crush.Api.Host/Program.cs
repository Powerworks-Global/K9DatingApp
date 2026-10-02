using System.Reflection;
using JasperFx;
using Marten;
using Weasel.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using K9Crush.BuildingBlocks.Domain;
using K9Crush.BuildingBlocks.Persistence;
using K9Crush.BuildingBlocks.Web;
using K9Crush.Modules.Admin.Api;
using K9Crush.Modules.Identity.Api;
using K9Crush.Modules.Media.Api;
using K9Crush.Modules.Notifications.Api;
using K9Crush.Modules.ShelterAdoption.Api;
using Serilog;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// --- Logging -----------------------------------------------------------
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// --- Module discovery ----------------------------------------------------
// This is the one place in the whole solution that knows the full list of
// business modules. Adding a module means adding one line here and a
// ProjectReference in this csproj - nothing else changes.
var modules = new IModule[]
{
    new IdentityModule(),
    new ShelterAdoptionModule(),
    new NotificationsModule(),
    new AdminModule(),
    new MediaModule()
};

foreach (var module in modules)
{
    module.RegisterServices(builder.Services, builder.Configuration);
}

// --- Marten (one DocumentStore, one schema per module - ADR-003) --------
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres");

builder.Services.AddMarten(options =>
{
    options.Connection(connectionString);

    // Event store schema is ONE shared setting for the whole StoreOptions,
    // not one per module - Marten has a single mt_events/mt_streams table
    // pair per store, it doesn't partition the event log by schema. Every
    // module used to call options.Events.DatabaseSchemaName = SchemaName
    // from its own Configure(), which silently overwrote whichever
    // module's setting was applied last (Media, per this array's order) -
    // every module's events were landing in "media" regardless of which
    // module actually owned them (see marten_schema_isolation_bug memory
    // for how this was found live, and ADR-003's updated entry for the
    // resulting split: documents are schema-per-module, the event store
    // is one shared schema). "eventstore" is deliberately not any single
    // module's name, since every module's events live here.
    options.Events.DatabaseSchemaName = "eventstore";

    // ADR-037: explicit serialization defaults, since two of these are a
    // one-way door once real events/documents exist (Casing.Default and
    // System.Text.Json were already Marten's own defaults and didn't need
    // setting; EnumStorage.AsString did - the untouched default is
    // AsInteger, which silently makes stored data opaque/fragile to enum
    // member reordering).
    options.UseSystemTextJsonForSerialization(EnumStorage.AsString);

    options.ApplyModuleConfigurations(modules.Select(m => m.MartenConfiguration));

    // Confirmed via reflection against the installed Marten 9.20.1/JasperFx
    // 2.36.2 packages: StoreOptions.AutoCreateSchemaObjects still exists,
    // just retyped from the old Weasel.Core.AutoCreate enum to
    // JasperFx.AutoCreate (same 4 values - All/CreateOrUpdate/CreateOnly/
    // None) - the test fixtures (e.g. MediaPostgresFixture) already use
    // this same property/enum successfully. Explicit per environment,
    // matching ADR-033: Development gets schema auto-creation for fast
    // local iteration; anywhere else, Marten must never alter schema at
    // startup - a real migration step (not written yet) owns that instead.
    options.AutoCreateSchemaObjects = builder.Environment.IsDevelopment()
        ? AutoCreate.CreateOrUpdate
        : AutoCreate.None;
})
// Wires Marten's transactional outbox/inbox with Wolverine. No
// SubscribeToEvent<T> registrations needed right now - Discovery and
// Chat were the only modules using that same-process domain-event
// forwarding mechanism (EVENT -> AUTOMATION -> COMMAND -> EVENT without
// a hand-rolled polling loop), and both are removed. If a future
// automation needs it again, register it here - see WolverineFx.Marten's
// MartenIntegrationExpression.SubscribeToEvent<T>().
.IntegrateWithWolverine();

// --- Wolverine (mediator + RabbitMQ transport + Http endpoints) ---------
var rabbitConnectionString = builder.Configuration.GetConnectionString("RabbitMQ")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:RabbitMQ");

builder.Host.UseWolverine(opts =>
{
    // Handlers are discovered from every module assembly automatically -
    // no per-module registration call needed beyond referencing the
    // assembly (which Api.Host already does via ProjectReference).
    foreach (var module in modules)
    {
        opts.Discovery.IncludeAssembly(module.GetType().Assembly);
    }

    opts.UseRabbitMq(new Uri(rabbitConnectionString)).AutoProvision();

    // Integration events route through the shared topic exchange
    // described in the Solution Architecture doc, Section 5. Each
    // consumer module gets its own durable queue bound to the events it
    // handles - Wolverine infers routing from the message type by
    // convention here; override per-message-type as needed.
    opts.PublishAllMessages().ToRabbitExchange("k9crush.events");

    // This comment described the intent above, but nothing actually
    // implemented the receiving half until now: PublishAllMessages(...)
    // is publish-only, so every integration event was published into
    // k9crush.events and dropped - confirmed live (see IModule.cs's
    // IntegrationEventQueueName doc comment for the reproduction). Each
    // module that declares a queue name gets it bound to the exchange
    // here; Wolverine's own message-type dispatch then routes each
    // delivered event to whichever local Handle(TEvent) matches, same as
    // if it had arrived in-process.
    foreach (var module in modules)
    {
        if (module.IntegrationEventQueueName is { } queueName)
        {
            opts.ListenToRabbitQueue(queueName, queue => queue.BindExchange("k9crush.events"));
        }
    }

    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    opts.Policies.UseDurableInboxOnAllListeners();

    opts.Policies.OnException<Exception>()
        .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
        .Then.MoveToErrorQueue();
});

builder.Services.AddWolverineHttp();

// --- Redis: distributed cache + SignalR backplane ------------------------
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Redis");

builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString);

// --- API infrastructure --------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// --- Auth: validate bearer JWTs (ADR-005 Supabase, ADR-048 local dev) -----
// Api.Host is a pure resource server - it never issues or stores
// credentials. In every real environment Supabase Cloud owns
// registration/login/MFA/password reset; this only validates the bearer
// token Supabase already issued. In Development, Auth:Provider=Local
// replaces that with a local HS256 signing key so a developer can run the
// app without a Supabase project - see ADR-048 and Identity's
// Commands/DevSignIn.
//
// Confirmed live against a real Supabase project (2026-07-23): new
// projects issue session tokens signed with ES256 (asymmetric JWKS), not
// the legacy HS256 shared-secret mode this code originally assumed - a
// hardcoded SymmetricSecurityKey rejected every real login token with
// "the signature key was not found". Fixed by using Authority-based OIDC
// discovery instead: Supabase exposes a real
// /auth/v1/.well-known/openid-configuration document (confirmed via
// curl) whose jwks_uri ASP.NET Core's JwtBearer handler fetches, caches,
// and auto-rotates on its own - no manual key material in this config at
// all, and it transparently keeps working if the project's active
// signing key ever changes.
var authProvider = builder.Configuration["Auth:Provider"] ?? "Supabase";
var useLocalDevAuth = string.Equals(authProvider, "Local", StringComparison.OrdinalIgnoreCase);

// The local provider is a development-only stand-in with no password
// verification - refuse to run it anywhere else rather than trust config
// (ADR-048).
if (useLocalDevAuth && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"Auth:Provider=Local is only allowed in Development (current environment: {builder.Environment.EnvironmentName}).");
}

// Resolve the local signing key once, here, so a misconfigured dev
// environment fails fast at startup instead of on the first request.
string? localSigningKey = null;
string? localIssuer = null;
if (useLocalDevAuth)
{
    localSigningKey = builder.Configuration["Auth:Local:SigningKey"]
        ?? throw new InvalidOperationException("Missing Auth:Local:SigningKey (required when Auth:Provider=Local).");
    if (localSigningKey.Length < 32)
        throw new InvalidOperationException("Auth:Local:SigningKey must be at least 32 characters (HMAC-SHA256 minimum key size).");

    localIssuer = builder.Configuration["Auth:Local:Issuer"] ?? "k9crush-local";
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Since .NET 8, JwtBearer's default token handler stopped
        // auto-remapping short JWT claim names (sub, email, name, ...) to
        // their long-form ClaimTypes.* URIs - claims come through exactly
        // as the IdP names them instead. Every handler in this codebase
        // that reads ClaimTypes.NameIdentifier (e.g. AddDogListing,
        // ApplyToAdopt) was written assuming the older remapped behavior.
        // Restoring it centrally here means those handlers don't each
        // need to know the IdP's raw claim names - fix once, works
        // everywhere any future module reads the caller's identity. This
        // was originally fixed for Keycloak but applies identically to
        // Supabase, since both issue standard JWTs with short claim names.
        options.MapInboundClaims = true;

        if (useLocalDevAuth)
        {
            // No Authority/JWKS in local mode - a fixed symmetric key that
            // matches what LocalDevTokenIssuer signs with.
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = localIssuer!,
                ValidateAudience = true,
                // Same audience as Supabase (below) so nothing downstream
                // needs to know which provider issued the token.
                ValidAudience = "authenticated",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(localSigningKey!)),
                ValidateLifetime = true
            };
            return;
        }

        var supabaseUrl = builder.Configuration["Supabase:Url"]
            ?? throw new InvalidOperationException("Missing Supabase:Url");

        options.Authority = $"{supabaseUrl}/auth/v1";
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            // "authenticated" is Supabase's standard audience for user
            // session tokens - a fixed string, not project-specific.
            // Confirmed live against a real issued token.
            ValidAudience = "authenticated",
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true
        };
    });

// Local dev token issuer (ADR-048). Registered unconditionally so
// Identity's Commands/DevSignIn can resolve it; IssueToken throws if
// Auth:Local:SigningKey isn't set and the endpoint 404s unless
// Auth:Provider=Local, so it is inert outside local development.
builder.Services.AddSingleton<ILocalDevTokenIssuer, LocalDevTokenIssuer>();

// ADR-017 role checking (RoleRequirement/RoleAuthorizationHandler, both
// in BuildingBlocks.Web) - registered Scoped, not Singleton, since its
// IOwnerRoleLookup dependency is itself Scoped (backed by Marten's
// IQuerySession). See RoleRequirement.cs's doc comment.
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RoleAuthorizationHandler>();

// EmailVerifiedRequirement/EmailVerifiedAuthorizationHandler (BuildingBlocks.Web)
// replaces a plain RequireClaim("email_verified", "true") - confirmed live
// against a real Supabase token (2026-07-23) that there is no such
// top-level claim; it's nested inside the "user_metadata" claim's JSON
// as {"email_verified":true}. See that file's doc comment.
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, EmailVerifiedAuthorizationHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("VerifiedOwner", policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new EmailVerifiedRequirement()));

    // Reviewer-only actions on someone else's ShelterAccount (verify,
    // activate, flag/approve/reject) - see ShelterAdoption's Commands/*
    // handlers, all originally flagged as having no role check at all.
    options.AddPolicy("Admin", policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new EmailVerifiedRequirement())
            .AddRequirements(new RoleRequirement(OwnerRole.Admin)));

    // Actions on a shelter's own resources once it's been activated
    // (e.g. AddDogListing) - combined with an in-handler ownership check,
    // since a Shelter-role caller should still only manage their own
    // ShelterAccount's resources, not every shelter's.
    options.AddPolicy("Shelter", policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new EmailVerifiedRequirement())
            .AddRequirements(new RoleRequirement(OwnerRole.Shelter)));
});

// AspNetCore.HealthChecks.Rabbitmq 9.x changed AddRabbitMQ() to no longer
// create its own connection internally - it now resolves a RabbitMQ.Client
// IConnection from DI and expects the caller to register one. Confirmed
// against the package's own current NuGet README (this was already
// flagged as the highest-risk version pin in this file, and this is
// exactly the kind of break that risk was about). Registered as a
// singleton per the client's own guidance (connections are meant to be
// long-lived, not created per health check).
builder.Services.AddSingleton<RabbitMQ.Client.IConnection>(_ =>
{
    var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(rabbitConnectionString) };
    return factory.CreateConnectionAsync().GetAwaiter().GetResult();
});

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres")
    .AddRedis(redisConnectionString, name: "redis")
    .AddRabbitMQ(name: "rabbitmq");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ADR-031: FetchForWriting/FetchForExclusiveWriting are optimistic by
// default - SaveChangesAsync throws on a stale fetch. Confirmed LIVE
// (Phase 1's Media Testcontainers spike, two sessions racing a
// FetchForWriting+AppendOne+SaveChangesAsync against the same stream):
// the real exception is JasperFx.Events.EventStreamUnexpectedMaxEventIdException,
// whose base is JasperFx.ConcurrencyException - a completely separate
// hierarchy from Marten.Exceptions.ConcurrentUpdateException (base:
// Marten.Exceptions.MartenException), which is Marten's *document*-level
// optimistic-concurrency exception, not the event-stream one. Two earlier
// guesses at this type name were both wrong (ConcurrencyException, then
// ConcurrentUpdateException) before this was verified against a real
// concurrent-write race, not just reflection. Catching both hierarchies
// here since this codebase could plausibly hit either one someday (a
// versioned document Store() doesn't exist today, but nothing rules it
// out later) - mapped globally, once, since every event-sourced command
// handler across every module hits the same failure the same way.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex) when (ex is JasperFx.ConcurrencyException or Marten.Exceptions.ConcurrentUpdateException)
    {
        context.Response.Clear();
        await Microsoft.AspNetCore.Http.Results.Conflict(
            "This resource was modified by someone else since you last loaded it. Reload and try again."
        ).ExecuteAsync(context);
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapWolverineEndpoints(opts =>
{
    // Wolverine.Http endpoints bypass the message-bus pipeline entirely,
    // so WolverineFx.FluentValidation (which only hooks into
    // IMessageBus.InvokeAsync/SendAsync) never runs for [WolverineGet]/
    // [WolverinePost] handlers - confirmed via reflection against the
    // real 6.17.2 assemblies after an invalid RequestShelterAccount body
    // 500'd instead of 400ing. This is Wolverine.Http's own, separate
    // validation subsystem: it runs System.ComponentModel.DataAnnotations
    // (attributes + IValidatableObject) against the request DTO before
    // the handler runs, and short-circuits with a 400 ProblemDetails on
    // failure. Every command request record's validation now lives on
    // the record itself (attributes, or IValidatableObject for
    // cross-field/Guid-not-empty checks that plain attributes can't
    // express) instead of a separate AbstractValidator<T> class - see
    // AddDogListingRequest, ApplyToAdoptRequest, RequestShelterAccountRequest.
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
}); // maps every [WolverineGet]/[WolverinePost] slice across all modules

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

app.MapHealthChecks("/healthz/live");
app.MapHealthChecks("/healthz/ready");

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
