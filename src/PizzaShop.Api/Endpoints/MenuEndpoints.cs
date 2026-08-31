using Microsoft.AspNetCore.Mvc;
using PizzaShop.Infrastructure;

namespace PizzaShop.Api.Endpoints;

public static class MenuEndpoints
{
    public static IEndpointRouteBuilder MapMenuEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/menu", GetMenu)
           .WithName("GetMenu")
           .Produces<IReadOnlyList<MenuItemDto>>(200);

        return app;
    }

    private static IResult GetMenu(PizzaShopDbContext db)
    {
        var items = db.Pizzas
            .OrderBy(p => p.Id)
            .Select(p => new MenuItemDto(p.Id, p.Name, p.Description, p.Price))
            .ToList();

        return Results.Ok(items);
    }
}

public sealed record MenuItemDto(int Id, string Name, string Description, decimal Price);
