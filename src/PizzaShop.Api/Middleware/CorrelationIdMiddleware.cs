using System.Diagnostics;
using Serilog.Context;

namespace PizzaShop.Api.Middleware;

/// <summary>
/// One request, one ID (approach.md §9).
/// <para>
/// API Management stamps <c>x-correlation-id</c> on every request, keeping the caller's
/// own value if they sent one. This middleware does three things with it: attaches it to
/// the Serilog log context so every line written while handling the request carries it,
/// mirrors it onto the current <see cref="Activity"/> so Application Insights shows the
/// gateway and the application as one operation rather than two unrelated traces, and
/// echoes it back so the caller can quote it.
/// </para>
/// <para>
/// The ID identifies a request, not a person, which is why it is safe to return.
/// </para>
/// </summary>
public static class CorrelationIdMiddleware
{
    public const string HeaderName = "x-correlation-id";

    /// <summary>Key under which the correlation ID is stashed for <c>ProblemDetails</c>.</summary>
    public const string ItemsKey = "CorrelationId";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

            // Falling back to the trace ID rather than to a fresh GUID: a direct caller
            // that bypassed the gateway still gets an ID that ties to the same
            // distributed trace, instead of one that ties to nothing.
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            }

            context.Items[ItemsKey] = correlationId;

            // A tag, not a replacement for the trace ID. Overwriting the Activity's own
            // identifiers would break the W3C trace context that App Insights uses to
            // stitch the gateway call to this one; adding a searchable property does not.
            Activity.Current?.SetTag(ItemsKey, correlationId);

            // Must be set before the response starts, so it goes on the way in rather
            // than after next().
            context.Response.Headers[HeaderName] = correlationId;

            using (LogContext.PushProperty(ItemsKey, correlationId))
            {
                await next();
            }
        });
    }
}
