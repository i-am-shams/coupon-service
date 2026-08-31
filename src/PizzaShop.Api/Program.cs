using Microsoft.EntityFrameworkCore;
using PizzaShop.Api.Endpoints;
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

    // Serilog replaces the default logging pipeline. Application code uses ILogger<T>.
    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // EF Core — SQL Server, migrations assembly in Infrastructure.
    var connectionString = builder.Configuration.GetConnectionString("PizzaShop")
        ?? "Server=(localdb)\\mssqllocaldb;Database=PizzaShop;Trusted_Connection=True;";

    builder.Services.AddDbContext<PizzaShopDbContext>(options =>
        options.UseSqlServer(connectionString,
            sql => sql.MigrationsAssembly(typeof(PizzaShopDbContext).Assembly.FullName)));

    // Domain services.
    builder.Services.AddScoped<ICouponRepository, CouponRepository>();
    builder.Services.AddScoped<ICouponEvaluator, DatabaseCouponEvaluator>();
    builder.Services.AddScoped<IMenu, DatabaseMenu>();

    // Health checks: liveness checks nothing external; readiness checks the database.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<PizzaShopDbContext>("database", tags: ["ready"]);

    builder.Services.AddProblemDetails();

    var app = builder.Build();

    app.UseExceptionHandler();

    // Migrations run at startup — approach.md §7 decision.
    // Trade-off: can race across instances; migration bundles are the production answer.
    // IsRelational() guard skips this for in-memory databases used in tests.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
            await PizzaShopDbSeeder.SeedAsync(db);
        }
    }

    app.UseHttpsRedirection();

    app.MapMenuEndpoints();
    app.MapCouponEndpoints();
    app.MapOrderEndpoints();

    // Liveness: checks nothing external. A DB blip must not restart a healthy app.
    app.MapHealthChecks("/health");

    // Readiness: checks the database.
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Makes Program accessible to WebApplicationFactory in test projects.
public partial class Program { }
