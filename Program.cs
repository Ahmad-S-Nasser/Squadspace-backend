using RafeeqyNotes.Api.Config;
using Coon.Payment;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Api.Hubs;
// using RafeeqyNotes.Api.Helpers; // Global namespace used for helpers
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Controllers
builder.Services.AddControllers().AddJsonOptions(opts =>
{
    opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    opts.JsonSerializerOptions.Converters.Add(new WhiteboardElementConverter());
});

// Capture startup errors to display them safely
string? startupError = null;

try
{
    // Register MongoDB class maps
    MongoMappings.RegisterClassMaps();

    // Bind configuration settings
    var mongoSettings = builder.Configuration.GetSection("MongoDbSettings").Get<MongoDbSettings>()
                        ?? throw new InvalidOperationException("MongoDbSettings not configured.");
    var elasticSettings = builder.Configuration.GetSection("ElasticSearchSettings").Get<ElasticSearchSettings>()
                        ?? throw new InvalidOperationException("ElasticSearchSettings not configured.");
    var emailSettings = builder.Configuration.GetSection("Smtp").Get<SmtpSettings>()
                        ?? throw new InvalidOperationException("SmtpSettings not configured.");
    var gitSettings = builder.Configuration.GetSection("GitHub").Get<GitSettings>()
                        ?? throw new InvalidOperationException("GitSettings not configured.");

    // Same reasoning as the JWT key below: a credential committed to appsettings.json is
    // published in deploy\api and lives in source control forever. These are supplied from
    // the environment, and the app refuses to start without them rather than running in a
    // state where mail silently fails or GitHub OAuth hands out tokens under a stale client.
    //
    // In development, use `dotnet user-secrets` rather than editing appsettings files.
    RequireSecret(emailSettings.Password, "Smtp:Password", "Smtp__Password");
    RequireSecret(gitSettings.ClientId, "GitHub:ClientId", "GitHub__ClientId");
    RequireSecret(gitSettings.ClientSecret, "GitHub:ClientSecret", "GitHub__ClientSecret");

    static void RequireSecret(string value, string configKey, string envVar)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{configKey} is not configured. Set the {envVar} environment variable " +
                "(or use dotnet user-secrets in development). See deploy\\DEPLOY.md.");
        }
    }

    var jwtSettings = builder.Configuration.GetSection("JwtSettings");

    // No fallback key. A hardcoded default that ships in source is a signing key an
    // attacker can use to mint valid tokens for any user; failing to start is strictly
    // better. Supply it via the JwtSettings__Key environment variable or user-secrets.
    var jwtKey = jwtSettings["Key"];
    if (string.IsNullOrWhiteSpace(jwtKey))
    {
        throw new InvalidOperationException(
            "JwtSettings:Key is not configured. Set the JwtSettings__Key environment variable " +
            "(or use dotnet user-secrets in development).");
    }
    var key = Encoding.UTF8.GetBytes(jwtKey);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];

                // If the request is for our hub...
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs") || path.StartsWithSegments("/collaborationHub")))
                {
                    // Read the token out of the query string
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization();

    // Register Settings as Singletons for DI
    builder.Services.AddSingleton(mongoSettings);
    builder.Services.AddSingleton(elasticSettings);
    builder.Services.AddSingleton(emailSettings);
    builder.Services.AddSingleton(gitSettings);

    // Services
    builder.Services.AddSingleton<ElasticSearchService>();

    // Registered before the repositories that take it. Reads Mongo collections directly
    // rather than the repository interfaces, so it cannot form a cycle with them.
    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IEntityGraphService,
                                  RafeeqyNotes.Api.Services.EntityGraphService>();
    builder.Services.AddSingleton<INoteRepository, NoteRepository>();
    builder.Services.AddSingleton<IBoardRepository, BoardRepository>();
    builder.Services.AddSingleton<IContributorRepository, ContributorRepository>();
    builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
    builder.Services.AddSingleton<IProjectScheduleRepository, ProjectScheduleRepository>();
    builder.Services.AddSingleton<INoteTaskRepository, NoteTaskRepository>();
    builder.Services.AddSingleton<IMeetingRepository, MeetingRepository>();
    builder.Services.AddSingleton<IWhiteboardRepository, WhiteboardRepository>();
    builder.Services.AddSingleton<IChatRepository, ChatRepository>();
    builder.Services.AddSingleton<IAnalyticsRepository, AnalyticsRepository>();
    builder.Services.AddSingleton<ISprintRepository, SprintRepository>();
    builder.Services.AddSingleton<IOrganizationRepository, OrganizationRepository>();
    builder.Services.AddSingleton<INotificationRepository, NotificationRepository>();
    builder.Services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
    builder.Services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
    builder.Services.AddSingleton<IGitHubService, GitHubService>();
    builder.Services.AddSingleton<IOrganizationInvitationRepository, OrganizationInvitationRepository>();
    builder.Services.AddSingleton<ISubscriptionPlanRepository, SubscriptionPlanRepository>();
    builder.Services.AddSingleton<IOrganizationSubscriptionRepository, OrganizationSubscriptionRepository>();
    builder.Services.AddSingleton<IAddOnRepository, AddOnRepository>();
    builder.Services.AddSingleton<IPaymentOrderRepository, PaymentOrderRepository>();
    builder.Services.AddSingleton<IFeedbackRepository, FeedbackRepository>();

    // Payments. AddPayments reads Payments:Provider and resolves a gateway from configuration;
    // with nothing configured it registers a null object that reports IsConfigured false and
    // fails closed on every verification, so the app still starts and add-on requests fall back
    // to the approval route rather than the endpoint disappearing.
    builder.Services.AddPayments(builder.Configuration);
    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IAddOnPurchaseService,
                                  RafeeqyNotes.Api.Services.AddOnPurchaseService>();
    builder.Services.AddSingleton<IOrgWorkspaceConfigRepository, OrgWorkspaceConfigRepository>();
    builder.Services.AddSingleton<ISavedReportRepository, SavedReportRepository>();
    builder.Services.AddSingleton<IImportJobRepository, ImportJobRepository>();

    // Single-use nonces for the GitHub OAuth `state` parameter.
    builder.Services.AddSingleton<IGitHubOAuthStateService, GitHubOAuthStateService>();

    // SSO. Single-use state and nonce (login CSRF and id_token replay), a one-time ticket
    // so the session token never rides in a URL, and real id_token validation.
    builder.Services.AddSingleton<ISsoStateService, SsoStateService>();
    builder.Services.AddSingleton<ISsoTicketService, SsoTicketService>();
    builder.Services.AddSingleton<IOidcTokenValidator, OidcTokenValidator>();
    builder.Services.AddSingleton<ISsoLoginService, SsoLoginService>();
    builder.Services.AddSingleton<IOrgSsoConfigRepository, OrgSsoConfigRepository>();

    // Automation. One engine for both tracks - scheduled manager workflows and
    // event-driven task rules - because they share a trigger vocabulary, a run history
    // and a meter.
    builder.Services.AddSingleton<IAutomationRepository, AutomationRepository>();
    builder.Services.AddSingleton<IAutomationEngine, AutomationEngine>();

    // The first background worker in this codebase. Several earlier features are shaped
    // around its absence - see AutomationHostedService.
    builder.Services.AddHostedService<AutomationHostedService>();

    // Notifies meeting attendees shortly before start - see MeetingReminderHostedService.
    builder.Services.AddHostedService<MeetingReminderHostedService>();

    // Entitlements: what an organization's plan actually permits. Singleton to match every
    // other registration here, and safe as one because it holds no per-request state - the
    // per-organization answers live in IMemoryCache with a short TTL.
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IIcsBuilder,
                                  RafeeqyNotes.Api.Services.IcsBuilder>();

    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IProjectExportService,
                                  RafeeqyNotes.Api.Services.ProjectExportService>();
    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IImportService,
                                  RafeeqyNotes.Api.Services.ImportService>();

    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IReportService,
                                  RafeeqyNotes.Api.Services.ReportService>();

    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.ITaskTimerService,
                                  RafeeqyNotes.Api.Services.TaskTimerService>();

    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.ITaskStatusService,
                                  RafeeqyNotes.Api.Services.TaskStatusService>();

    builder.Services.AddSingleton<RafeeqyNotes.Api.Services.IEntitlementService,
                                  RafeeqyNotes.Api.Services.EntitlementService>();
}
catch (Exception ex)
{
    // Record only. The message and stack trace must never reach a response body —
    // this block is where connection strings and the JWT signing key are bound.
    startupError = "The server is not correctly configured and cannot handle requests.";
    Console.WriteLine($"Startup Configuration Error: {ex.Message}\n{ex.StackTrace}");
}

builder.Services.AddHttpClient();

// Standard CORS Policy
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Production origins. Note both the apex and www forms of the marketing site:
        // it is served from both squadspace.net and www.squadspace.net.
        var origins = new List<string>
        {
            "https://app.squadspace.net",
            "https://squadspace.net",
            "https://www.squadspace.net",
        };

        // Dev-server origins are NOT shipped to production: combined with
        // AllowCredentials, any process listening on those localhost ports could make
        // authenticated calls on a user's behalf.
        if (builder.Environment.IsDevelopment())
        {
            origins.AddRange(new[]
            {
                "http://localhost:5173",
                "http://localhost:5254",
                "http://localhost:8080",
            });
        }

        policy.WithOrigins(origins.ToArray())
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Rafeeqy Notes API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter 'Bearer' [space] and then your JWT token."
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddSignalR(options =>
{
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.EnableDetailedErrors = true;
});

var app = builder.Build();

// Route OrgScope fallback warnings into the normal log stream. See OrgScopeDiagnostics:
// a run of real traffic with that counter at zero is what licenses deleting the embedded
// parent snapshots.
RafeeqyNotes.Api.Helpers.OrgScopeDiagnostics.Use(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OrgScope"));

// Seed / refresh the subscription plan catalogue.
//
// Upserts by PlanId rather than only running against an empty collection. The previous
// version was gated on CountAsync() == 0, so any plan added to the catalogue never reached a
// deployment that already had the original four - and a naive re-run was not possible either,
// because PlanId carries a unique index and re-inserting throws.
try
{
    var planRepo = app.Services.GetRequiredService<ISubscriptionPlanRepository>();

    foreach (var plan in RafeeqyNotes.Api.Config.PlanCatalogue.All())
    {
        await planRepo.UpsertByPlanIdAsync(plan);
    }

    Console.WriteLine($"Subscription plan catalogue synced ({RafeeqyNotes.Api.Config.PlanCatalogue.All().Count} plans).");

    // Add-ons, same idempotent-upsert rule as the plans: code is the source of truth and the
    // catalogue moves with a deploy.
    var addOnRepo = app.Services.GetRequiredService<IAddOnRepository>();
    foreach (var addOn in RafeeqyNotes.Api.Config.AddOnCatalogue.All())
    {
        await addOnRepo.UpsertCatalogueEntryAsync(addOn);
    }

    Console.WriteLine($"Add-on catalogue synced ({RafeeqyNotes.Api.Config.AddOnCatalogue.All().Count} add-ons).");
}
catch (Exception ex)
{
    Console.WriteLine($"Error seeding subscription plans: {ex.Message}");
}

// Schema migrations and indexes.
//
// Deliberately here, after builder.Build(), rather than in a repository constructor: the DI
// block above is inside a try whose catch sets startupError, and the middleware below then
// returns a fixed 500 for EVERY request. An index conflict thrown from a constructor is a
// total outage. Failing here degrades import idempotency and query speed instead, and says so
// in stdout.
try
{
    var migrationSettings = builder.Configuration.GetSection("MongoDbSettings").Get<MongoDbSettings>();
    if (migrationSettings == null)
    {
        Console.WriteLine("[migration] skipped: MongoDbSettings not configured.");
    }
    else
    {
        await RafeeqyNotes.Api.Migrations.MigrationRunner.RunAsync(migrationSettings,
            new RafeeqyNotes.Api.Migrations.IMigration[]
            {
                new RafeeqyNotes.Api.Migrations.M001_BackfillParentIds(),
                new RafeeqyNotes.Api.Migrations.M002_ResolveStaleParentIds(),
                new RafeeqyNotes.Api.Migrations.M003_ResolveOrgFromProject(),
                new RafeeqyNotes.Api.Migrations.M004_NormalizeTaskStatus(),
                new RafeeqyNotes.Api.Migrations.M005_FlattenTaskDependencies(),
            });

        await RafeeqyNotes.Api.Migrations.SchemaIndexes.EnsureAsync(migrationSettings);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[migration] runner failed: {ex.Message}");
}

// Enable CORS with the defined default policy
app.UseCors();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Startup Error Middleware - Returns 500 if startup failed, after CORS is handled.
// The body is a fixed string: the underlying exception (which comes from binding
// connection strings and the JWT key) is logged, never returned.
app.Use(async (context, next) =>
{
    if (!string.IsNullOrEmpty(startupError))
    {
         context.Response.StatusCode = 500;
         await context.Response.WriteAsync(startupError);
         return;
    }

    await next();
});

// Swagger is Development-only. Served at the site root with no gating, it published a
// browsable index of all 151 endpoints to anyone who loaded the homepage.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Rafeeqy Notes API v1");
        c.RoutePrefix = string.Empty; // Serve Swagger UI at root
    });
}

app.MapControllers();
app.MapHub<CollaborationHub>("/collaborationHub");
app.MapHub<WhiteboardHub>("/hubs/whiteboard");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<NotesHub>("/hubs/notes");
app.MapHub<TasksHub>("/hubs/tasks");
app.MapHub<NotificationHub>("/hubs/notification");
app.MapHub<PresenceHub>("/hubs/presence");
app.MapHub<MeetingCallHub>("/hubs/meetingCall");

app.Run();