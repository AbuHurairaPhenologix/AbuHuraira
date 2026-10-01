using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AnomalyDetection.Api.Auth;
using AnomalyDetection.Api.Demo;
using AnomalyDetection.Api.Middleware;
using AnomalyDetection.Application;
using AnomalyDetection.Infrastructure;
using AnomalyDetection.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON console logs (scopes carry the correlation ID).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- Authentication / authorization (report §4.8) ----
builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
builder.Services.AddOptions<IngestionOptions>().BindConfiguration(IngestionOptions.SectionName);
builder.Services.AddSingleton<DevelopmentTokenService>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    if (auth.IsOidc)
    {
        // Production: tokens from an external OpenID Connect authority (SSO / workload identity).
        o.Authority = auth.Authority;
        o.Audience = auth.Audience;
        o.TokenValidationParameters = new TokenValidationParameters { NameClaimType = "name", RoleClaimType = auth.RoleClaimType };
    }
    else
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth.Issuer,
            ValidAudience = auth.Audience,
            IssuerSigningKey = DevelopmentTokenService.SigningKey(auth),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    }
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Engineer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Engineer, Roles.Administrator))
    .AddPolicy(Policies.Administrator, p => p.RequireAuthenticatedUser().RequireRole(Roles.Administrator));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// ---- Demo workload (scenarios are DEV-only) ----
builder.Services.AddOptions<DemoOptions>().BindConfiguration(DemoOptions.SectionName).PostConfigure(o =>
{
    if (!builder.Environment.IsDevelopment() && !builder.Configuration.GetValue<bool>("Demo:AllowScenariosOutsideDevelopment"))
    {
        o.EnableScenarios = false;
    }
});
builder.Services.AddSingleton<DemoScenarioState>();
builder.Services.AddSingleton<DemoWorkload>();

// ---- API ----
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Anomaly Detection API", Version = "v1", Description = "ML-based anomaly detection for distributed web application logs." });
    o.SwaggerDoc("demo", new OpenApiInfo { Title = "Demo workload", Version = "v1" });
    o.DocInclusionPredicate((doc, api) => (api.GroupName ?? "v1") == doc);
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Token from POST /api/v1/auth/token",
    });
    o.AddSecurityDefinition("IngestionKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = IngestionApiKeyFilter.HeaderName,
    });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
    });
});

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AnomalyDetectionDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 10)
        {
            app.Logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying migrations.", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Anomaly Detection API v1");
    o.SwaggerEndpoint("/swagger/demo/swagger.json", "Demo workload");
});
app.UseCors();
app.UseRouting();
app.UseMiddleware<OperationalEventMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Container probes: liveness (process up) and readiness (database reachable).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Name == "postgresql",
    ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 200, [HealthStatus.Unhealthy] = 503 },
}).AllowAnonymous();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory integration tests.</summary>
public partial class Program;
