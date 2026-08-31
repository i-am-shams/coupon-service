using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PizzaShop.Api.Endpoints;
using PizzaShop.Api.Middleware;
using PizzaShop.Coupons;
using PizzaShop.Infrastructure;
using PizzaShop.Infrastructure.Adapters;
using PizzaShop.Infrastructure.Repositories;
using PizzaShop.Ordering;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Application Insights. Registered before Serilog is configured, because the sink
    // below resolves the TelemetryConfiguration this call sets up. The connection string
    // arrives as the APPLICATIONINSIGHTS_CONNECTION_STRING app setting, which Bicep sets
    // from the component it creates — it is never in a file.
    builder.Services.AddApplicationInsightsTelemetry();

    // Serilog replaces the default logging pipeline. Application code uses ILogger<T>.
    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        // Traces, not events: log lines belong in the traces table where they can be
        // queried alongside the requests and dependencies App Insights collects itself.
        // Without this sink the gateway's telemetry arrives and the application's log
        // lines do not, which looks like a broken instrumentation key rather than a
        // missing sink.
        .WriteTo.ApplicationInsights(
            services.GetRequiredService<TelemetryConfiguration>(),
            TelemetryConverter.Traces));

    // EF Core — SQL Server, migrations assembly in Infrastructure.
    var connectionString = builder.Configuration.GetConnectionString("PizzaShop")
        ?? "Server=(localdb)\\mssqllocaldb;Database=PizzaShop;Trusted_Connection=True;";

    builder.Services.AddDbContext<PizzaShopDbContext>(options =>
        options.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsAssembly(typeof(PizzaShopDbContext).Assembly.FullName);

            // Azure SQL drops connections. That is expected behaviour, not a fault, and
            // without a retry strategy a single transient failure becomes a 500 for the
            // customer. Observed on the first real deployment — see docs/decisions.md.
            //
            // This makes CreateExecutionStrategy() return a retrying strategy, which then
            // REFUSES user-initiated transactions. OrderEndpoints wraps its transaction
            // accordingly; a new one anywhere else must do the same.
            sql.EnableRetryOnFailure();
        }));

    // Domain services.
    builder.Services.AddScoped<ICouponRepository, CouponRepository>();
    builder.Services.AddScoped<ICouponEvaluator, DatabaseCouponEvaluator>();
    builder.Services.AddScoped<IMenu, DatabaseMenu>();

    // Health checks: liveness checks nothing external; readiness checks the database.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<PizzaShopDbContext>("database", tags: ["ready"]);

    // approach.md §4: a fault carries a traceId matching the correlation ID. Without
    // this the default traceId is the Activity's own ID, which is a different value from
    // the one in the x-correlation-id header — so the ID a caller quotes from a failed
    // response would not be the ID in the logs.
    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = context =>
        {
            if (context.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemsKey, out var correlationId)
                && correlationId is string id)
            {
                context.ProblemDetails.Extensions["traceId"] = id;
            }
        };
    });

    var app = builder.Build();

    // Before the exception handler, so a fault that is turned into ProblemDetails still
    // has a correlation ID to report.
    app.UseCorrelationId();

    app.UseExceptionHandler();

    // Migrations run at startup — approach.md §7 decision.
    // Trade-off: can race across instances; migration bundles are the production answer.
    //
    // Tests are excluded by environment rather than by IsRelational(): the test host
    // uses SQLite, which *is* relational, and the migrations are SQL Server specific.
    // The test factory creates its schema directly and seeds its own data.
    if (!app.Environment.IsEnvironment("Test"))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        await db.Database.MigrateAsync();
        await PizzaShopDbSeeder.SeedAsync(db);
    }

    // Development only. Behind API Management the app is reached over the platform's own
    // HTTPS listener and there is no HTTPS port for the middleware to redirect to, so it
    // no-ops and logs "Failed to determine the https port for redirect" on every boot.
    // httpsOnly on the App Service enforces the actual guarantee. A warning that fires on
    // every healthy start teaches people to ignore start-up warnings.
    if (app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.MapMenuEndpoints();
    app.MapCouponEndpoints();
    app.MapOrderEndpoints();

    // Liveness: checks nothing external. A DB blip must not restart a healthy app.
    // The empty predicate is what makes that true — MapHealthChecks with no options
    // runs *every* registered check, database included.
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
    });

    // Readiness: checks the database.
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // A failed startup — a migration that throws, most likely — must exit non-zero.
    // Returning normally here reports success to the host while serving nothing, and
    // a deployment gate checking exit status would go green on a broken release.
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

// Makes Program accessible to WebApplicationFactory in test projects.
public partial class Program { }
