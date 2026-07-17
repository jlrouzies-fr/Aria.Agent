# Aria.Bridge/Program.cs Split Plan

Goal: reduce `Program.cs` to builder setup + one-line pipeline/DB/endpoint/lifetime calls.

## Current state check

`Program.cs` is 1195 lines. Since the last check, another agent added `Endpoints/ProjectFileEndpoints.cs` (chat `#` file reference picker) and wired it in `Program.cs` at line 137 via `app.MapProjectFileEndpoints();`. This endpoint group is already extracted, so it just needs to be included in the centralized mapper.

## Target `Program.cs`

```csharp
using Aria.Bridge;
using Aria.Bridge.Data;
using Aria.Bridge.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddBridgeServices();

var app = builder.Build();

app.UseBridgePipeline();
await app.InitializeBridgeDatabaseAsync();
app.MapBridgeEndpoints();
app.RegisterBridgeLifetimeEvents();

app.Run();
```

## New Files

| Folder | File | Responsibility |
|---|---|---|
| `Services/` | `BridgeLogger.cs` | `Version`, `StartedAt`, log ring buffer, `LogFilePath`, `Log(level, message)`. |
| `Services/` | `BridgeServiceRegistration.cs` | `AddBridgeServices(this WebApplicationBuilder)` — URLs, CORS, `SessionStore`, `DirectTunnel`, SQLite `BridgeDbContext`. |
| `Services/` | `BridgePipeline.cs` | `UseBridgePipeline(this WebApplication)` — PNA preflight middleware + `UseCors`. |
| `Services/` | `BridgeDatabaseInitializer.cs` | `InitializeBridgeDatabaseAsync(this WebApplication)` — `EnsureCreated`, manual `Contacts`/`LlmKeys` table creation, `Souls` column migration, log vault path. |
| `Services/` | `BridgeLifetimeEvents.cs` | `RegisterBridgeLifetimeEvents(this WebApplication)` — browser launch + startup console output. |
| `Frontend/` | `BridgeStatusPage.cs` | `Build()` returning the raw-string HTML status page. |
| `Endpoints/` | `EndpointsMapper.cs` | `MapBridgeEndpoints(this WebApplication)` calling all endpoint groups. |
| `Endpoints/` | `StatusEndpoints.cs` | `/`, `/status`, `/health`, `/logs`. |
| `Endpoints/` | `DbAdminEndpoints.cs` | `/db-info`, `/db/cogitations`, `/db/messages`, `/db/soul`. |
| `Endpoints/` | `ToolEndpoints.cs` | `/tools/list`, `/tools/call`. |
| `Endpoints/` | `ProjectFileEndpoints.cs` | *(already exists)* `/project-files/list`, `/project-files/read`. Included in `MapBridgeEndpoints()`. |

`EndpointsMapper.cs` will call all existing endpoint groups:

```csharp
app.MapSoulEndpoints();
app.MapCogitationEndpoints();
app.MapContactEndpoints();
app.MapLlmKeyEndpoints();
app.MapNodeEndpoints();
app.MapProjectFileEndpoints();
app.MapStatusEndpoints();
app.MapDbAdminEndpoints();
app.MapToolEndpoints();
```

## Rules

- Namespaces stay `Aria.Bridge` for `Services/` and `Frontend/` files, `Aria.Bridge.Endpoints` for `Endpoints/` files.
- `dbPath` moves to a shared helper so both service registration and DB init use the same path.
- Preserve all existing behavior, public signatures, and route definitions.
- After implementation, run `dotnet build` and fix any errors before summarizing.

## Status

Implemented. `Program.cs` reduced from 1195 lines to 16 lines. Build succeeded with 0 warnings, 0 errors.
