# Canvas

Collaborative whiteboard demo built with ASP.NET Core, MongoDB, SignalR, HTML5 Canvas, and vanilla JavaScript.

## Architecture

- `Program.cs` wires MongoDB, SignalR, static files, and the minimal API endpoints.
- `Services/` contains the board and user-profile persistence logic.
- `Hubs/` contains the typed SignalR hub for collaboration traffic.
- `wwwroot/` contains the browser shell, drawing canvas, and SignalR client.

## Prerequisites

- .NET 10 SDK
- MongoDB Atlas account or compatible MongoDB deployment

## Setup

1. Initialize user secrets:
   `dotnet user-secrets init`
2. Store the MongoDB connection string:
   `dotnet user-secrets set "MongoDB:ConnectionString" "<connection-string>"`
3. Keep `MongoDB:DatabaseName` in `appsettings.json` (defaults to `canvas`).

## Run

`dotnet run`

Open a board at `/boards/{name}` or create one via `/new`.

## Tests

The `Tests/` project uses MSTest and targets `net10.0`.

For Atlas-backed integration tests, use a separate test database connection string in user secrets or environment variables so the development database is never reused.
