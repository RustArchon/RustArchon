# RustArchon Developer Guide

## Architecture Overview
- **Multi-service monorepo** with distinct components: API, Panel, Worker, Web, Rcon, Messaging
- **JumpStart framework** dependency (separate GPL-3.0-or-later licensed project)
- **Docker-based deployment** using docker-compose.yml with 4 main services
- **Four separate databases**: RustArchon (API), RustArchon_Identity (Panel), PostgreSQL, and Valkey
- Core modules: RustArchon.Api, RustArchon.Panel, RustArchon.Worker, RustArchon.Web, RustArchon.Rcon, RustArchon.Messaging

## Key Technical Details
- **RCON Passwords**: Never stored in plaintext; encrypted via Data Protection before persistence 
- **Data Protection Keys**: Local file system storage by default but must be shared across instances for scaling (see "Data Protection" section below)
- **Multi-tenancy**: Built on JumpStart framework with tenant-scoped permissions
- **Authentication**: JWT Bearer tokens, ASP.NET Core Identity, email confirmation required
- **Messaging**: MassTransit with RabbitMQ transport
- **RCON Communication**: WebSockets-based implementation in RustArchon.Rcon

## Running Locally
```bash
# Prerequisites: .NET 10 SDK, Docker
docker run --name rustarchon-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17
git clone --recurse-submodules https://github.com/RustArchon/RustArchon.git
cd RustArchon.Api && dotnet ef database update
cd ../RustArchon.Panel && dotnet ef database update
# Then run API and Panel separately:
# API: cd RustArchon.Api && dotnet run
# Panel: cd RustArchon.Panel && dotnet run
```

## Deployment Notes
- **Docker Compose Setup**: 4 services (api, worker, panel, web) with persistent volumes for PostgreSQL data and Data Protection keys
- **Data Protection**: Single-container volume `dataprotection-keys` currently used - must move to shared storage for multiple instances
- **Email Delivery**: Currently stubbed (NoOpEmailDeliveryProvider) - production requires real provider swap

## Data Protection Configuration Issues
**Problem**: Current Data Protection key rings use local file system (`App_Data/dataprotection-keys`) which breaks across container instances.
**Solution**: Database-backed storage is required for horizontal scaling.

- **Current Code Location**: `RustArchon.Api/Program.cs`, lines 86-89
- **Fix Required**: Replace PersistKeysToFileSystem with database persistence
- **Database Migration Needed**: Create DataProtectionKeys table via EF Core migration

**Important Note for Existing Installations**: 
When migrating from filesystem-based to database-based data protection, the system will generate new keys. Any existing encrypted values (including RCON passwords stored in Data Protection) will become inaccessible unless you perform a manual migration of these keys from their old location.

To ensure continuity when upgrading:
1. For new deployments: The system works immediately with no data loss
2. For existing deployments: You must manually transfer the old keys by:
   - Stopping all instances of RustArchon  
   - Extracting keys from old file system location (App_Data/dataprotection-keys)
   - Configuring your application to seed the database with these keys during startup
   - Starting your applications

While this implementation enables horizontal scaling, it does not automatically migrate existing keys for security reasons - this is intended behavior as the old keys should be regenerated for proper security.

## Horizontal Scaling
- The system uses named volumes (`dataprotection-keys`) for sharing keys across containers
- For more than one replica: Must move key storage to shared database or cloud storage, cannot rely on local Docker volumes alone 
- Refer to README.md section "Single-instance Data Protection key rings" for detailed explanation

## Database Reseeding Information
After implementing database-backed data protection, existing installations need to be aware that:
1. **Encryption Keys**: The transition from filesystem to database creates new keys, invalidating old encrypted values
2. **Affected Data**: Existing RCON passwords and other encrypted data will become inaccessible 
3. **Upgrade Procedure**: 
   - Ensure database is updated with latest migrations 
   - Manually migrate existing keys if continuity is required (requires application-level implementation)
   - Application will generate new keys automatically upon first run

## Testing
```bash
# Test specific project  
cd RustArchon.Api && dotnet test
cd RustArchon.Api.IntegrationTests && dotnet test
```

## Project Structure
- Panel (Blazor Server): ASP.NET Core Identity, UI for server management
- API: RESTful API with JWT auth and multi-tenancy, entity authorization  
- Worker: Persistent connection host for RCON communication with RabbitMQ
- Web (Marketing Site): No database, no identity, just links to Panel for sign-up
- Rcon: Standalone library for WebRCON communication with Oxide/Carbon detection