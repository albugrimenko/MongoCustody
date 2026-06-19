# MongoCustody CLI Configuration

This directory contains `mongocustody.json`, the default configuration file for the MongoCustody CLI tool.

## Configuration

The configuration is set to use **EnvironmentSecretProvider** to retrieve connection strings from environment variables.

### Required Environment Variables

Before running the CLI, set the following environment variables:

| Environment Variable | Description |
|---------------------|-------------|
| `MONGOCUSTODY_MONGO_CONNECTION_STRING` | MongoDB source connection string |
| `MONGOCUSTODY_STORAGE_CONNECTION_STRING` | Azure Blob Storage connection string (optional if using managed identity) |

### Example (PowerShell)

```powershell
$env:MONGOCUSTODY_MONGO_CONNECTION_STRING = "mongodb://your-connection-string"
$env:MONGOCUSTODY_STORAGE_CONNECTION_STRING = "DefaultEndpointsProtocol=https;AccountName=..."
```

### Example (Bash)

```bash
export MONGOCUSTODY_MONGO_CONNECTION_STRING="mongodb://your-connection-string"
export MONGOCUSTODY_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=..."
```

## Customization

Update `mongocustody.json` to:
- Configure your MongoDB endpoint and storage account
- Add databases and collections to back up
- Adjust backup policy settings (chunk size, compression, etc.)
- Switch to Azure Key Vault for production (set `secrets.provider` to `AzureKeyVault`)

Refer to the main [README.md](../../README.md) for complete configuration documentation.
