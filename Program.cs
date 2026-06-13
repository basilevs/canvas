using System.Text.Json.Serialization;
using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Middleware;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IMongoDbContext, MongoDbContext>();
builder.Services.AddSingleton<IBoardService, BoardService>();
builder.Services.AddSingleton<IUserProfileService, UserProfileService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseMiddleware<UserIdentityMiddleware>();
app.UseStaticFiles();

app.MapGet("/api/boards/{name}/snapshot", async Task<Results<Ok<BoardSnapshotResponse>, BadRequest, NotFound>> (
    string name,
    IBoardService boardService,
    CancellationToken cancellationToken) =>
{
    if (!BoardNameNormalizer.TryNormalizeBoardName(name, out var boardId))
    {
        return TypedResults.BadRequest();
    }

    var board = await boardService.GetBoardAsync(boardId, cancellationToken);
    if (board is null)
    {
        return TypedResults.NotFound();
    }

    return TypedResults.Ok(MapBoardSnapshot(board));
})
.WithName("GetBoardSnapshot")
.WithSummary("Gets the current board snapshot")
.Produces<BoardSnapshotResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/new", () => Results.Redirect($"/boards/{GenerateBoardName()}"))
    .ExcludeFromDescription();

app.MapGet("/", async Task<IResult> (
    HttpContext context,
    IUserProfileService userProfileService,
    CancellationToken cancellationToken) =>
{
    var userId = context.Items["UserId"] as string
        ?? throw new InvalidOperationException("User identity is not available.");

    var lastBoardName = await userProfileService.GetLastBoardAsync(userId, cancellationToken);
    return Results.Redirect(lastBoardName is null ? "/new" : $"/boards/{lastBoardName}");
})
.ExcludeFromDescription();

app.MapHub<WhiteboardHub>("/hub/whiteboard");
app.MapFallbackToFile("/boards/{*slug}", "index.html");

var boardService = app.Services.GetRequiredService<IBoardService>();
var userProfileService = app.Services.GetRequiredService<IUserProfileService>();
await boardService.EnsureIndexesAsync(CancellationToken.None);
await userProfileService.EnsureIndexesAsync(CancellationToken.None);

app.Run();

static BoardSnapshotResponse MapBoardSnapshot(Board board)
{
    return new BoardSnapshotResponse(
        board.Id,
        board.ActiveStrokes.Select(MapStroke).ToList());
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
