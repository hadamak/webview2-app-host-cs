# AGENTS.md

## Build Commands

```powershell
# Standard build (MSBuild, not dotnet build)
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64

# Local development setup (builds + copies Steamworks DLL + test-www)
.\tools\setup-local-dev.ps1
.\tools\setup-local-dev.ps1 -Configuration Release

# Create release ZIP
.\tools\package-release.ps1
```

## Build Configurations

| Config | Purpose |
|--------|---------|
| `Debug` | Local dev; copies `test-www\` to output `www\` |
| `Release` | Shipping; includes MCP, Sidecar, Pipe, CDP |
| `SecureRelease` | Offline; excludes all external connectivity (`SECURE_OFFLINE` define) |

## Test Commands

```powershell
# .NET tests (uses xunit, central versions in Directory.Packages.props)
dotnet test tests\UnitTests\UnitTests.csproj -c Debug
dotnet test tests\IntegrationTests\IntegrationTests.csproj -c Debug
dotnet test tests\StressTests\StressTests.csproj -c Debug
dotnet test tests\SecureTests\SecureTests.csproj -c SecureRelease

# JS tests
node tests/host-js/host.test.js
```

## Important Notes

- **Target Framework**: .NET Framework 4.8 (`net48`), not .NET Core
- **WebView2 Dependency**: Requires `Microsoft.Web.WebView2` (centrally managed in `Directory.Packages.props`)
- **Git Submodule**: Facepunch.Steamworks is a submodule; run `git submodule update --init` before building if using Steam features
- **Content Packaging**: `web-content/` is auto-zipped to `app.zip` at build time (embedded resource)
- **Platform Target**: Always `x64`
- **CI Build Order**: Builds both `Release` and `SecureRelease` configurations

## Architecture

- **Entry Point**: `src/Program.cs`
- **Main Window**: `src/App.cs`
- **Connectors** (extensions): `src/connectors/*.cs` (Browser, Dll, Sidecar, Pipe, MCP)
- **Content Loading**: `src/ZipContentProvider.cs` handles embedded/sibling/loose `www/` loading

## Key Entry Points

- `src/AppConfig.cs` - configuration parsing
- `src/MessageBus.cs` - JavaScript<->.NET bridge
- `src/ReflectionDispatcherBase.cs` - method invocation from JS
