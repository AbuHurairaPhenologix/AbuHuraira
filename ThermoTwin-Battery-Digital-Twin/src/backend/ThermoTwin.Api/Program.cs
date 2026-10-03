using System.Reflection;
using Microsoft.OpenApi;
using Serilog;
using ThermoTwin.Api.Hubs;
using ThermoTwin.Api.Infrastructure;
using ThermoTwin.Application;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Infrastructure;
using ThermoTwin.SimulationWorker;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

    builder.Services
        .AddControllers()
        .AddJsonOptions(options => JsonDefaults.Apply(options.JsonSerializerOptions));

    builder.Services
        .AddSignalR()
        .AddJsonProtocol(options => JsonDefaults.Apply(options.PayloadSerializerOptions));

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "ThermoTwin.NET API",
            Version = "v1",
            Description = "Battery thermal digital twin: PDE simulation, inverse heat-source estimation, prediction and cooling optimisation.",
        });
        var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
        if (File.Exists(xml))
        {
            options.IncludeXmlComments(xml);
        }
    });

    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"];
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddSimulationWorker(builder.Configuration);
    builder.Services.AddSingleton<ITwinNotifier, SignalRTwinNotifier>();

    var app = builder.Build();

    await app.Services.InitializeDatabaseAsync();

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(options =>
        options.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500
            ? Serilog.Events.LogEventLevel.Error
            : Serilog.Events.LogEventLevel.Debug);
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "ThermoTwin.NET v1");
        options.DocumentTitle = "ThermoTwin.NET API";
    });
    app.UseCors();

    app.MapControllers();
    app.MapHub<TwinHub>("/hubs/twin");
    app.MapHealthChecks("/health");
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ThermoTwin API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker used by integration tests.</summary>
public partial class Program;
