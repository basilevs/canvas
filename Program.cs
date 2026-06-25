using System.Text.Json.Serialization;
using System.Globalization;
using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Middleware;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using MongoDB.Driver;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var connectionString = configuration["MongoDB_ConnectionString"]
        ?? throw new InvalidOperationException(
            $"MongoDB connection string is not configured. Available configuration keys: {string.Join(", ", configuration.AsEnumerable().Select(entry => entry.Key))}.");
    return new MongoClient(connectionString);
});

/// <summary>
/// Instead of calling service.AddHostedService<T> you call this make sure that you can also access the hosted service by interface TImplementation
/// https://stackoverflow.com/a/64689263/619465
/// </summary>
/// <param name="services">The service collection</param>
static void AddInjectableHostedService<TService, TImplementation>(IServiceCollection services)
    where TService : class
    where TImplementation : class, IHostedService, TService
{
    services.AddSingleton<TImplementation>();
    services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<TImplementation>());
    services.AddSingleton<TService>(provider => provider.GetRequiredService<TImplementation>());
}

AddInjectableHostedService<IBoardRepository, BoardRepository>(builder.Services);
AddInjectableHostedService<IMongoDbContext, MongoDbContext>(builder.Services);
AddInjectableHostedService<IUserProfileRepository, UserProfileRepository>(builder.Services);
builder.Services.AddSingleton<IStrokeEventRepository, StrokeEventRepository>();
builder.Services.AddSingleton<ICancellationTokenProvider>(sp => ICancellationTokenProvider.Wrap(sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseMiddleware<UserIdentityMiddleware>();
app.UseMiddleware<HubOriginValidationMiddleware>();
app.UseStaticFiles();

app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg", permanent: true));

app.MapGet("/api/boards/{name}/history/{pageNumber:int}", async Task<Results<Ok<HistoryPageResponse>, StatusCodeHttpResult, BadRequest, NotFound>> (
    string name,
    int pageNumber,
    HttpContext httpContext,
    IBoardRepository boardRepository,
    IStrokeEventRepository strokeEventRepository,
    CancellationToken cancellationToken) =>
{
    if (!BoardNameNormalizer.TryNormalizeBoardName(name, out var boardId) || pageNumber < 1)
    {
        return TypedResults.BadRequest();
    }

    var board = await boardRepository.GetBoardAsync(boardId, cancellationToken);
    if (board is null)
    {
        return TypedResults.NotFound();
    }

    // Resolve the page's totals and most-recent-event timestamp without loading
    // its documents, so a conditional GET that revalidates to 304 never transfers
    // the page off the database.
    var pageInfo = await strokeEventRepository.GetEventsPageInfoAsync(
        boardId,
        pageNumber,
        StrokeEventRepository.DefaultPageSize,
        cancellationToken);

    // A board with no events returns 404 for page 1 ("no history"); any page
    // beyond the last is likewise absent.
    if (pageInfo is null)
    {
        return TypedResults.NotFound();
    }

    // Last-Modified is the page's most-recent event truncated to whole seconds,
    // formatted as an RFC1123 HTTP-date so a client's echoed If-Modified-Since
    // can be compared by exact string equality.
    var lastModified = TruncateToSeconds(pageInfo.LastEventTimestamp)
        .ToString("R", CultureInfo.InvariantCulture);
    var isCompletePage = pageNumber < pageInfo.TotalPages;

    httpContext.Response.Headers.LastModified = lastModified;
    // Complete pages are immutable for a year but omit `immutable` so a manual
    // reload still revalidates; the growing final page revalidates every use.
    //
    // No `public`: this endpoint is currently anonymous, so a 200 GET carrying a
    // validator/max-age is already shared-cacheable (RFC 9111 §3) and `public`
    // would be redundant. `public` only relaxes the rule barring shared caches
    // from storing responses to Authorization-bearing requests (RFC 9111
    // §5.2.2.9) — once the history-access-moderation feature adds per-user
    // authorization, `public` here would let a CDN cross-serve one user's
    // history to another. Omit it now so that gap can't be introduced by
    // forgetting to remove it later.
    httpContext.Response.Headers.CacheControl = isCompletePage
        ? "max-age=31536000"
        : "no-cache";

    // Conditional GET: return 304 only when the client echoes back the exact
    // Last-Modified value we previously emitted. We deliberately do not parse the
    // header as a date — when the page has gained events its Last-Modified string
    // differs, so the strings simply won't match and a fresh page is served.
    if (httpContext.Request.Headers.TryGetValue("If-Modified-Since", out var ifModifiedSince) &&
        string.Equals(ifModifiedSince.ToString(), lastModified, StringComparison.Ordinal))
    {
        return TypedResults.StatusCode(StatusCodes.Status304NotModified);
    }

    // The validator didn't match, so the client genuinely needs the body. Load
    // only the page's events — the totals already came from the metadata lookup,
    // so no second count is issued.
    var pageEvents = await strokeEventRepository.GetPageEventsAsync(
        boardId,
        pageNumber,
        StrokeEventRepository.DefaultPageSize,
        cancellationToken);

    var response = new HistoryPageResponse(
        pageNumber,
        (int)pageInfo.TotalEvents,
        pageInfo.TotalPages,
        pageEvents.Select(MapStrokeEvent).ToList());

    return TypedResults.Ok(response);
})
.WithName("GetBoardHistory")
.WithSummary("Gets a page of board stroke history")
.Produces<HistoryPageResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status304NotModified)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/new", () => Results.Redirect($"/boards/{GenerateBoardName()}"))
    .ExcludeFromDescription();

app.MapGet("/", async Task<IResult> (
    HttpContext context,
    IUserProfileRepository userProfileRepository,
    CancellationToken cancellationToken) =>
{
    var userId = context.Items["UserId"] as string
        ?? throw new InvalidOperationException("User identity is not available.");

    var lastBoardName = await userProfileRepository.GetLastBoardAsync(userId, cancellationToken);
    return Results.Redirect(lastBoardName is null ? "/new" : $"/boards/{lastBoardName}");
})
.ExcludeFromDescription();

app.MapHub<WhiteboardHub>("/hub/whiteboard");
app.MapFallbackToFile("/boards/{*slug}", "index.html");

app.Run();

static StrokeEventResponse MapStrokeEvent(StrokeEvent strokeEvent)
{
    return new StrokeEventResponse(
        strokeEvent.Id.ToString(),
        strokeEvent.Type.ToString(),
        MapStroke(strokeEvent.Stroke),
        strokeEvent.Timestamp);
}

static DateTime TruncateToSeconds(DateTime value)
{
    return new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}

static StrokeResponse MapStroke(Stroke stroke)
{
    return new StrokeResponse(
        stroke.Id,
        stroke.UserId,
        stroke.Color,
        stroke.Width,
        stroke.Points.Select(MapPoint).ToList(),
        stroke.Timestamp);
}

static PointResponse MapPoint(Point point)
{
    return new PointResponse(point.X, point.Y, point.Pressure, point.TimeOffset);
}

static string GenerateBoardName()
{
    const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    var chars = new char[10];

    for (var i = 0; i < chars.Length; i++)
    {
        chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    }

    return new string(chars);
}

public partial class Program;
