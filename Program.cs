using System.Text.Json.Serialization;
using Canvas.Hubs;
using Canvas.Middleware;
using Canvas.Services;

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

app.MapHub<WhiteboardHub>("/hub/whiteboard");
app.MapFallbackToFile("/boards/{*slug}", "index.html");

var boardService = app.Services.GetRequiredService<IBoardService>();
var userProfileService = app.Services.GetRequiredService<IUserProfileService>();
await boardService.EnsureIndexesAsync(CancellationToken.None);
await userProfileService.EnsureIndexesAsync(CancellationToken.None);

app.Run();
