# MongoCustody Project Structure

Created on: 2025-01-XX

## Solution Structure

```
D:\Repos\MongoCustody\
├── src\
│   ├── MongoCustody.sln                    # Main solution file
│   │
│   ├── MongoCustody.Core\                  # Core business logic (no Azure dependencies)
│   │   ├── Abstractions\                   # Interfaces (IBackupEngine, IStorageConnector, etc.)
│   │   ├── Models\                         # DTOs, configuration, request/response types
│   │   ├── Engines\                        # BackupEngine, RestoreEngine, ValidationEngine, CheckpointEngine
│   │   ├── Extensions\                     # IServiceCollection extension methods
│   │   └── MongoCustody.Core.csproj        # .NET 10.0 class library
│   │
│   ├── MongoCustody.Connectors\            # Azure SDK implementations
│   │   ├── Secrets\                        # Secret providers (KeyVault, Environment)
│   │   ├── MongoDbConnector.cs             # IBackupSourceConnector implementation
│   │   ├── BlobStorageConnector.cs         # IStorageConnector implementation
│   │   ├── CosmosMetadataRepository.cs     # IMetadataRepository implementation
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
	└── MongoCustody.Connectors.Tests\      # Integration tests (Cosmos emulator + Azurite)
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

1. **Phase 1** - Models, abstractions, and configuration
2. **Phase 2** - Connectors and health checks
3. **Phase 3** - Full backup engine
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
cd D:\Repos\MongoCustody\src
dotnet build

# Build specific project
dotnet build MongoCustody.Core\MongoCustody.Core.csproj

# Run all tests
dotnet test

# Run only unit tests (fast, no infrastructure)
dotnet test ..\tests\MongoCustody.Core.Tests\MongoCustody.Core.Tests.csproj

# Run integration tests (requires Cosmos emulator + Azurite)
dotnet test ..\tests\MongoCustody.Connectors.Tests\MongoCustody.Connectors.Tests.csproj
```

## Notes

- All default template files (Class1.cs, UnitTest1.cs, Program.cs placeholder) have been removed
- Project structure follows the exact layout specified in README.md
- Solution file is located in `src\` directory as requested
- Test projects are in separate `tests\` directory at repository root
