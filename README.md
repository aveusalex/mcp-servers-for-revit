[![Cover Image](./assets/cover.png?v=2)](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)

# mcp-servers-for-revit

**Connect AI assistants to Autodesk Revit via the Model Context Protocol.**

> Para implantação em um escritório, configuração no Codex, atualização,
> remoção, convivência com PyRevit/Dynamo e instruções para agentes, leia o
> [Guia operacional](docs/GUIA_OPERACIONAL.md).

mcp-servers-for-revit enables AI clients like Claude, Cline, and other MCP-compatible tools to read, create, modify, and delete elements in Revit projects. It consists of three components: a TypeScript MCP server that exposes tools to AI, a C# Revit add-in that bridges commands into Revit, and a command set that implements the actual Revit API operations.

> [!NOTE]
> This is a **fork** of [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
> that replaces the 1:1, click-to-activate integration with a **multi-document,
> auto-connecting broker** architecture. It lets one Claude chat see and drive
> every open Revit project, and lets several chats work at once — with no manual
> activation. With a single project open, behaviour and tool schemas are
> unchanged. See [DIVERGENCE.md](./DIVERGENCE.md) for the full delta and for
> what still needs validation inside Revit.

## Architecture

```mermaid
flowchart LR
    ClientA["MCP Client A<br/>(Claude chat)"]
    ClientB["MCP Client B<br/>(Claude chat)"]
    ServerA["MCP Server<br/><code>server/</code>"]
    ServerB["MCP Server<br/><code>server/</code>"]
    Broker["Broker daemon<br/><code>broker/</code><br/>ws://127.0.0.1:8090"]
    Plugin1["Revit 2026 plugin<br/>docs: Torre-A, Torre-B"]
    Plugin2["Revit 2024 plugin<br/>docs: Retrofit"]

    ClientA <-->|stdio| ServerA
    ClientB <-->|stdio| ServerB
    ServerA <-->|WebSocket + token| Broker
    ServerB <-->|WebSocket + token| Broker
    Plugin1 -->|connects OUT| Broker
    Plugin2 -->|connects OUT| Broker
```

The central change: **the plugin is now a client that dials out**, and the
**broker** is the only process that listens on a fixed port. This removes port
scanning, bind races, and discovery files, and lets multiple Revit processes and
multiple MCP clients coexist.

- **Broker** (`broker/`, Node): the single loopback listener. It authenticates
  plugins and MCP clients with a shared token, tracks which document belongs to
  which Revit session, and routes each command envelope
  `{correlationId, docId, command, params}` to the session that owns the target
  document — keyed by the **document**, not the port.
- **MCP Server** (`server/`, TypeScript): one per Claude chat. Resolves the
  target document per call (explicit `document` argument → fixed target →
  the single open document) and forwards commands to the broker. Adds the
  `list_open_documents` and `set_target_document` tools; every existing tool
  gains an optional `document` argument.
- **Revit Plugin** (`plugin/`, C#): connects to the broker automatically on
  startup (no click), registers every open document by its stable
  `ProjectInformation.UniqueId`, heartbeats every 5s, and dispatches commands to
  the resolved document. The ribbon button is now a kill switch.
- **Command Set** (`commandset/`, C#): executes the Revit API operations.
  Doc-agnostic commands resolve their `Document` from the broker-selected target.

### Revit links

`list_revit_links` is a read-only inventory of the selected host model's Revit
links. It normalizes Revit's duplicate nested-link representations into one
tree and returns loaded/unloaded state, each link instance's identity and
transform into host coordinates, and its source path when Revit exposes one.
A linked model is never treated as an editable MCP
target: to modify a house/project that is linked into a host model, open its
source RVT as a normal Revit document and select it with `list_open_documents`
and `set_target_document`.

> Commands to different documents are serialized onto Revit's single UI thread
> (via `ExternalEvent`), so they interleave in one queue rather than running in
> true parallel.

### Command scope (`command.json`)

Each command declares a `scope`:

- **`doc-agnostic`** — runs against any open document (e.g. `create_level`,
  `create_grid`, `get_material_quantities`, `analyze_model_statistics`,
  `export_room_data`, `get_available_family_types`).
- **`ui-bound`** — requires the target document to be the **active** window
  (e.g. `get_current_view_info`, `get_selected_elements`, `create_dimensions`,
  `tag_walls`, `tag_rooms`, `operate_element`, `color_splash`). Targeting a
  non-active document returns a typed `REQUIRES_ACTIVE_DOCUMENT` error.

### Running the broker

The MCP server starts the broker automatically (spawned detached, idempotent —
it exits quietly if one is already running). To run it by hand:

```bash
cd broker && npm install && npm start
```

Security: the broker binds **only** to `127.0.0.1`, requires the shared token
(`%APPDATA%\revit-mcp\broker-token`) from both plugin and MCP clients, keeps
non-active documents **read-only** unless `allowBackgroundWrites` is enabled,
never synchronizes workshared models, and writes a per-command JSONL audit log
under `%APPDATA%\revit-mcp\audit\`.

## Requirements

- **Node.js 20+** (for the MCP server and broker)
- **Autodesk Revit 2020 - 2026** (any supported version)

## Quick Start (Using a Release)

1. Download the ZIP for your Revit version from the [Releases](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/releases) page (e.g., `mcp-servers-for-revit-v1.0.0-Revit2025.zip`)

2. Extract the ZIP and copy the contents to your Revit addins folder:
   ```
   %AppData%\Autodesk\Revit\Addins\<your Revit version>\
   ```
   After copying you should have:
   ```
   Addins/2025/
   ├── mcp-servers-for-revit.addin
   └── revit_mcp_plugin/
       ├── RevitMCPPlugin.dll
       ├── ...
       └── Commands/
           └── RevitMCPCommandSet/
               ├── command.json
               └── 2025/
                   ├── RevitMCPCommandSet.dll
                   └── ...
   ```

3. Configure the MCP server in your AI client (see [MCP Server Setup](#mcp-server-setup))

4. Start Revit — if prompted about an unknown add-in, click **Always Load**

5. In Revit, click the **Settings** button on the mcp-servers-for-revit ribbon tab, enable the commands you want to use, and click **Save**

## MCP Server Setup

The MCP server is published as an npm package and can be run directly with `npx`.

**Claude Code**

Run this in a **terminal** (not inside Claude Code):

```bash
claude mcp add mcp-server-for-revit -- cmd /c npx -y mcp-server-for-revit
```

**Claude Desktop**

Claude Desktop → Settings → Developer → Edit Config → `claude_desktop_config.json`:

```json
{
    "mcpServers": {
        "mcp-server-for-revit": {
            "command": "cmd",
            "args": ["/c", "npx", "-y", "mcp-server-for-revit"]
        }
    }
}
```

Restart Claude Desktop. When you see the hammer icon, the MCP server is connected.

![Claude Desktop connection](./assets/claude.png)

## Cadastro no Claude Desktop (Windows)

Antes de configurar o Claude Desktop, confirme qual instalação e qual arquivo
de configuração estão realmente ativos no computador. Há variantes instaladas
pelo instalador tradicional e pela Microsoft Store; seus caminhos de
configuração podem ser diferentes. Siga o alerta genérico no
[guia operacional](docs/GUIA_OPERACIONAL.md#outros-clientes-mcp-locais) antes
de editar `mcpServers`.

## Revit Plugin Setup

If using a release ZIP, the plugin is already included. For manual installation:

1. Build the plugin from `plugin/` (see [Development](#development))
2. Copy `mcp-servers-for-revit.addin` to `%AppData%\Autodesk\Revit\Addins\<version>\`
3. Copy the `revit_mcp_plugin/` folder to the same addins directory

## Command Set Setup

If using a release ZIP, the command set is pre-installed inside the plugin. For manual installation:

1. Build the command set from `commandset/` (see [Development](#development))
2. Inside the plugin's installation directory, create `Commands/RevitMCPCommandSet/<year>/`
3. Copy the built DLLs into that folder
4. Copy `command.json` (from repo root) into `Commands/RevitMCPCommandSet/`

## Supported Tools

| Tool | Description |
| ---- | ----------- |
| `get_current_view_info` | Get current active view info |
| `get_current_view_elements` | Get elements from the current active view |
| `get_available_family_types` | Get available family types in current project |
| `get_selected_elements` | Get currently selected elements |
| `get_material_quantities` | Calculate material quantities and takeoffs |
| `ai_element_filter` | Intelligent element querying tool for AI assistants |
| `analyze_model_statistics` | Analyze model complexity with element counts |
| `create_point_based_element` | Create point-based elements (door, window, furniture) |
| `create_line_based_element` | Create line-based elements (wall, beam, pipe) |
| `create_surface_based_element` | Create surface-based elements (floor, ceiling, roof) |
| `create_grid` | Create a grid system with smart spacing generation |
| `create_level` | Create levels at specified elevations |
| `create_room` | Create and place rooms at specified locations |
| `create_dimensions` | Create dimension annotations in the current view |
| `create_structural_framing_system` | Create a structural beam framing system |
| `delete_element` | Delete elements by ID |
| `operate_element` | Operate on elements (select, setColor, hide, etc.) |
| `color_elements` | Color elements based on a parameter value |
| `tag_all_walls` | Tag all walls in the current view |
| `tag_all_rooms` | Tag all rooms in the current view |
| `export_room_data` | Export all room data from the project |
| `store_project_data` | Store project metadata in local database |
| `store_room_data` | Store room metadata in local database |
| `query_stored_data` | Query stored project and room data |
| `send_code_to_revit` | Send C# code to Revit to execute |
| `say_hello` | Display a greeting dialog in Revit (connection test) |
| `list_revit_links` | Read-only tree of Revit links in the selected host model |

## Testing

The test project uses [Nice3point.TUnit.Revit](https://github.com/Nice3point/RevitUnit) to run integration tests against a live Revit instance. No separate addin installation is required — the framework injects into the running Revit process automatically.

### Prerequisites

- **.NET 10 SDK** — required by Nice3point.Revit.Sdk 6.1.0. Install via `winget install Microsoft.DotNet.SDK.10`
- **Autodesk Revit 2026** (or 2025) — must be installed and licensed on your machine

### Running Tests

1. Open Revit 2026 (or 2025) and wait for it to fully load
2. Run the tests from the command line:

```bash
# For Revit 2026
dotnet test -c Debug.R26 -r win-x64 tests/commandset

# For Revit 2025
dotnet test -c Debug.R25 -r win-x64 tests/commandset
```

> **Note:** The `-r win-x64` flag is required on ARM64 machines because the Revit API assemblies are x64-only.

Alternatively, you can use `dotnet run`:

```bash
cd tests/commandset
dotnet run -c Debug.R26
```

### IDE Support

- **JetBrains Rider** — enable "Testing Platform support" in Settings > Build, Execution, Deployment > Unit Testing > Testing Platform
- **Visual Studio** — tests should be discoverable through the standard Test Explorer

### Test Structure

| Directory | Purpose |
|-----------|---------|
| `tests/commandset/AssemblyInfo.cs` | Global `[assembly: TestExecutor<RevitThreadExecutor>]` registration |
| `tests/commandset/Architecture/` | Tests for level and room creation commands |
| `tests/commandset/DataExtraction/` | Tests for model statistics, room data export, and material quantities |
| `tests/commandset/ColorSplashTests.cs` | Tests for color override functionality |
| `tests/commandset/TagRoomsTests.cs` | Tests for room tagging functionality |

### Writing New Tests

Test classes inherit from `RevitApiTest` and use TUnit's async assertion API:

```csharp
public class MyTests : RevitApiTest
{
    private static Document _doc;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task MyTest_Condition_ExpectedResult()
    {
        var elements = new FilteredElementCollector(_doc)
            .WhereElementIsNotElementType()
            .ToElements();

        await Assert.That(elements.Count).IsGreaterThan(0);
    }
}
```

## Development

### MCP Server

```bash
cd server
npm install
npm run build
```

The server compiles TypeScript to `server/build/`. During development you can run it directly with `npx tsx server/src/index.ts`.

### Revit Plugin + Command Set

Open `mcp-servers-for-revit.sln` in Visual Studio. The solution contains both the plugin and command set projects. Build configurations target Revit 2020-2026:

- **Revit 2020-2024**: .NET Framework 4.8 (`Release R20` through `Release R24`)
- **Revit 2025-2026**: .NET 8 (`Release R25`, `Release R26`)

Building the solution automatically assembles the complete deployable layout in `plugin/bin/AddIn <year> <config>/` - the command set is copied into the plugin's `Commands/` folder as part of the build.

## Project Structure

```
mcp-servers-for-revit/
├── mcp-servers-for-revit.sln    # Combined solution (plugin + commandset + tests)
├── command.json     # Command set manifest
├── server/          # MCP server (TypeScript) - tools exposed to AI clients
├── plugin/          # Revit add-in (C#) - WebSocket bridge inside Revit
├── commandset/      # Command implementations (C#) - Revit API operations
├── tests/           # Integration tests (C#) - TUnit tests against live Revit
├── assets/          # Images for documentation
├── .github/         # CI/CD workflows, contributing guide, code of conduct
├── LICENSE
└── README.md
```

## Releasing

A single `v*` tag drives the entire release. The [release workflow](.github/workflows/release.yml) automatically:

- Builds the Revit plugin + command set for Revit 2020-2026
- Creates a GitHub release with `mcp-servers-for-revit-vX.Y.Z-Revit<year>.zip` assets
- Publishes the MCP server to npm as [`mcp-server-for-revit`](https://www.npmjs.com/package/mcp-server-for-revit)

To create a release:

1. Run the bump script (updates `server/package.json`, `server/package-lock.json`, and `plugin/Properties/AssemblyInfo.cs`, then commits and tags):
   ```powershell
   ./scripts/release.ps1 -Version X.Y.Z
   ```

2. Push to trigger the workflow:
   ```bash
   git push origin main --tags
   ```

> [!NOTE]
> npm publish uses [trusted publishing](https://docs.npmjs.com/trusted-publishers/) via OIDC — no npm token is required. Provenance attestation is generated automatically.

## Acknowledgements

This project is a fork of the work by the [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit) team. The original repositories:

- [revit-mcp](https://github.com/mcp-servers-for-revit/revit-mcp) - MCP server
- [revit-mcp-plugin](https://github.com/mcp-servers-for-revit/revit-mcp-plugin) - Revit plugin
- [revit-mcp-commandset](https://github.com/mcp-servers-for-revit/revit-mcp-commandset) - Command set

Thank you to the original authors for creating the foundation that this project builds upon.

## License

[MIT](LICENSE)
