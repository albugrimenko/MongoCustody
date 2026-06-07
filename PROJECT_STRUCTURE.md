# MongoCustody Project Structure

Created on: 2025-01-XX

## Solution Structure

```
D:\Repos\MongoCustody\
├── src\
│   ├── MongoCustody.slnx                   # Main solution file
│   │
│   ├── MongoCustody.Core\                  # Core business logic (no Azure dependencies)
│   │   ├── Abstractions\                   # Interfaces (IBackupSourceConnector, IStorageConnector, etc.)
│   │   ├── Models\                         # DTOs, configuration, request/response types
│   │   ├── Engines\                        # FullBackupEngine, BackupPathBuilder, ChunkWriter
│   │   ├── Extensions\                     # IServiceCollection extension methods
│   │   └── MongoCustody.Core.csproj        # .NET 10.0 class library
│   │
│   ├── MongoCustody.Connectors\            # Azure SDK implementations
│   │   ├── Secrets\                        # Secret providers (KeyVault, Environment)
│   │   ├── MongoDbConnector.cs             # IBackupSourceConnector implementation
│   │   ├── BlobStorageConnector.cs         # IStorageConnector implementation
│   │   ├── BlobMetadataRepository.cs       # IMetadataRepository — manifests live in Blob Storage
│   │   └── MongoCustody.Connectors.csproj  # References: MongoCustody.Core
│   │
│   └── MongoCustody.Cli\                   # Command-line tool
│       ├── Commands\                       # CLI command handlers
│       ├── Program.cs                      # Entry point
│       └── MongoCustody.Cli.csproj         # References: Core + Connectors
│
└── tests\
	├── MongoCustody.Core.Tests\            # Unit tests (no infrastructure)
	│   └── MongoCustody.Core.Tests.csproj  # MSTest, references Core
	│
	└── MongoCustody.Connectors.Tests\      # Integration tests (Azurite + MongoDB)
		└── MongoCustody.Connectors.Tests.csproj  # MSTest, references Core + Connectors
```

## Project Dependencies

```
MongoCustody.Core
  └─ No external dependencies (pure business logic)

MongoCustody.Connectors
  └─ References: MongoCustody.Core
  └─ NuGet packages (to be added):
	  - MongoDB.Driver
	  - Azure.Storage.Blobs
	  - Azure.Identity
	  - Azure.Security.KeyVault.Secrets

MongoCustody.Cli
  └─ References: MongoCustody.Core, MongoCustody.Connectors
  └─ NuGet packages (to be added):
	  - System.CommandLine
	  - Microsoft.Extensions.Configuration
	  - Microsoft.Extensions.Configuration.Json
	  - Microsoft.Extensions.DependencyInjection

MongoCustody.Core.Tests
  └─ References: MongoCustody.Core
  └─ NuGet packages (included):
	  - Microsoft.NET.Test.Sdk
	  - MSTest.TestAdapter
	  - MSTest.TestFramework
	  - coverlet.collector

MongoCustody.Connectors.Tests
  └─ References: MongoCustody.Core, MongoCustody.Connectors
  └─ NuGet packages (included):
	  - Microsoft.NET.Test.Sdk
	  - MSTest.TestAdapter
	  - MSTest.TestFramework
	  - coverlet.collector
```

## Framework and Testing

- **Target Framework**: .NET 10.0
- **Test Framework**: MSTest v3 (with source generators)
- **Build Status**: ✅ All projects compile successfully

## Next Steps

According to the README.md Implementation Phases:

1. **Phase 1** - Models, abstractions, and configuration ✅
2. **Phase 2** - Connectors and health checks ✅
1. **Phase 1** - Models, abstractions, and configuration ✅
2. **Phase 2** - Connectors and health checks ✅
3. **Phase 3** - Full backup engine ✅
4. **Phase 4** - Validation engine (full backup)
5. **Phase 5** - Full backup restore engine
6. **Phase 6** - Incremental backup engine
7. **Phase 7** - Incremental restore and validation
8. **Phase 8** - Checkpoint engine
9. **Phase 9** - DI wiring and public API
10. **Phase 10** - CLI tool

## Design Principles Applied

✅ **Separation of Concerns**: Core logic isolated from infrastructure  
✅ **Dependency Inversion**: Core defines interfaces, Connectors implement them  
✅ **Testability**: Core can be unit-tested with mocks, no emulators required  
✅ **Azure SDK Isolation**: All Azure-specific code lives in Connectors project  
✅ **Progressive Enhancement**: CLI depends on both Core and Connectors

## Build Commands

```bash
# Build entire solution
dotnet build src\MongoCustody.slnx

# Run all non-integration tests (no infrastructure required)
dotnet test src\MongoCustody.slnx --filter "Category!=Integration"

# Run integration tests (requires: docker compose -f tests/docker-compose.yml up -d)
$env:MONGOCUSTODY_INTEGRATION_TESTS = "1"
dotnet test tests\MongoCustody.Connectors.Tests --filter "Category=Integration"
```

## Notes

- All default template files (Class1.cs, UnitTest1.cs, Program.cs placeholder) have been removed
- Project structure follows the exact layout specified in README.md
- Solution file is located in `src\` directory as requested
- Test projects are in separate `tests\` directory at repository root
