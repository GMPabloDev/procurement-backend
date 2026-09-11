# AGENTS.md

Guía operativa del repositorio para agentes y personas. Los flujos `/spec` y `/spec-impl` la leen antes de trabajar: si un comando o una convención cambia, actualiza este archivo — es la fuente que evita que el agente adivine.

## Comandos

| Qué | Comando |
|---|---|
| Restaurar | `dotnet restore ProcureToPay.sln` |
| Build | `dotnet build ProcureToPay.sln --no-restore` |
| Todos los tests | `dotnet test ProcureToPay.sln --no-restore` |
| Unitarias | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` |
| API/E2E | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` |
| Higiene de diff | `git diff --check` |

- El runner es **Microsoft.Testing.Platform** con xunit v3 (`global.json` → `test.runner`); no hay `vstest`.
- `IntegrationTests` y `ApiE2ETests` usan **Testcontainers** (`Testcontainers.MsSql`, `Testcontainers.LocalStack`): exigen **Docker en marcha**. Si Docker no está disponible, dilo y no declares esos tests como pasados.
- Las versiones de paquetes están centralizadas en `Directory.Packages.props`.
- Acota la salida de build y test (`--nologo -v q`, `| tail -n 20`) cuando ejecutes comandos largos.

## Estructura

- `src/ProcureToPay.Domain/Modules/<Área>/` — dominio y contratos. Sin dependencias de Infrastructure ni Api.
- `src/ProcureToPay.Infrastructure/Persistence/` — EF Core, `Migrations/` y adapters in-process (fact providers, catálogos de referencia, verificadores de excepción).
- `src/ProcureToPay.Application/` — casos de uso.
- `src/ProcureToPay.Api/` — controllers, Problem Details y health.
- `tests/` — unitarias, integración y E2E; un directorio por área con el nombre del módulo.
- `docs/<módulo>-operations.md` — operación por módulo (despliegue, comandos, recuperación).
- `specs/` — contratos SDD; `specs/runs/` — evidencia de ejecución.

## Base de datos y migraciones

```bash
dotnet tool restore
dotnet ef migrations add <Nombre> --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api
dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api
```

- Las migraciones se aplican **antes** de publicar la API.
- No ejecutes `database update` ni ningún comando contra datos reales sin autorización explícita.

## Contrato y convenciones

- **Determinismo.** Todo digest usa JSON canónico `policy-canonical-json/v1` + SHA-256. Cambiar preimage, orden o claves de un digest exige nueva `canonicalization_version`; cambiar el schema del documento se resuelve subiendo `policy_schema_version`.
- **Errores.** Problem Details: dependencia ausente o ambigua → `503`; conflicto de contrato → `409`; payload inválido → `400`/`413`/`422`; autorización → `403`.
- **Seguridad.** Roles `ADMIN` y `AUDITOR`; los workloads se identifican por `issuer + client_id` allowlisted y estable ante rotación de credenciales. Sin PII, tokens ni snapshots completos en logs, trazas o respuestas.
- **Fail-closed.** La ausencia o ambigüedad de política, provider, catálogo o verificador nunca degrada a `ALLOW`.
- **Comandos operativos.** Bootstrap y recuperación se ejecutan desde el artefacto publicado (`--organization-bootstrap`, `--organization-recover-admin`), nunca por HTTP; exigen `Reason` y quedan auditados.
- **Idioma.** Documentación y specs en español; identificadores, mensajes de error y commits en inglés.
- **Datos.** Fechas en UTC; importes con `decimal` y moneda ISO 4217 explícita.

## Entorno local

`docker-compose.yml` levanta SQL Server 2022 y Keycloak 26.3.0. La configuración local vive en `.env` (ignorado por Git): usa `.env.example` como plantilla y **no** copies secretos a specs, runs, logs ni memoria del agente.

## Contratos SDD

- `specs/NN-slug.md` es el contrato aprobado: inmutable salvo metadatos administrativos. El progreso se registra en `specs/runs/NN-slug.md`, nunca marcando la spec.
- Aprobación humana: `specctl approve NN`. Comprobaciones: `specctl check NN`, `specctl run-lint NN`, `specctl git-check NN`, `specctl doctor`.
- No se implementa nada que no esté trazado a un `T-NN` de una spec `Aprobada`.

## Fuera de alcance sin spec

- Dominios de specs futuras, cambios de contrato, refactors masivos y dependencias nuevas.
- Si algo hace falta y el contrato no lo cubre, se propone una spec nueva en lugar de improvisarlo.
