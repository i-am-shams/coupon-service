// Composition root. Endpoints, EF Core, logging and health checks arrive in phase B.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseHttpsRedirection();

app.Run();
