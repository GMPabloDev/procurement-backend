# RUN SPEC 01 — Organización, identidad, roles y autoridades de aprobación

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/01-organizacion-identidad-roles-autoridades-aprobacion.md
> **Revisión contractual:** 1
> **Commit de la spec:** 468f51deda013e5455c8fc477ab44edde12cbbd9
> **Blob aprobado:** 0060a8ea61cb914b3bf680f49b92991e2ef7b63b
> **Digest contractual:** c5f194a4dca2597aff6cbbace581b1f9212197416c2918559e1a124484de458e
> **Rama base:** main
> **Commit base:** 468f51deda013e5455c8fc477ab44edde12cbbd9
> **Rama de implementación:** spec-01-organizacion-identidad-roles-autoridades-aprobacion
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** balanced
> **Iniciado:** 2026-09-08 05:56 -05
> **Actualizado:** 2026-09-08 07:35 -05
> **HEAD verificado:** Pendiente (checkpoint de implementación)
> **Commit de integración:** Pendiente

## Línea base

| Comando o comprobación | Resultado | Evidencia breve |
| --- | --- | --- |
| `specctl check 01 --approval` | Pasa | Spec aprobada, sdd/v3, digest válido, sin avisos. |
| `specctl doctor` | Pasa | Estado administrativo válido, sin avisos. |
| `dotnet build ProcureToPay.sln --no-restore` | Pasa | Build .NET 10 correcto, 0 advertencias, 0 errores. |
| `dotnet test --solution ProcureToPay.sln --no-restore` | Pasa | 3 ensamblados ejecutados, 3 tests correctos, 0 errores. |
| `git status --short` | Pasa | Árbol limpio antes de crear la rama. |

**Fallos preexistentes:** Ninguno. Las tres pruebas existentes son placeholders y no cubren la SPEC.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
| --- | --- | --- | --- |
| T-01 | Verificada | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`: 9 correctos; build de Domain correcto. | working-tree / CP-01 |
| T-02 | Verificada | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`: 14 correctos; resolver de elegibilidad, matriz, scopes y evidencia verificados. | working-tree / CP-01 |
| T-03 | Verificada | `dotnet build ProcureToPay.sln --no-restore` (0 warnings/errores); `dotnet test --solution ProcureToPay.sln --no-restore` (18 correctos); migraciones EF Core generadas y prueba de modelo/SQL Server correctas. | working-tree / CP-02 |
| T-04 | Verificada | `OrganizationBootstrapperTests.Bootstrap_is_idempotent_and_recovery_adds_audited_admin`: SQL Server efímero, bootstrap inicial, repetición inocua, divergencia rechazada y recuperación auditada. Suite completa: 18 correctos. | working-tree / CP-02 |
| T-05 | Verificada | Middleware de provisioning JIT por `iss`+`sub`, estado propio `/api/v1/me`, autorización local y denegación de perfiles no activos; integración probada en SQL Server efímero. | working-tree / CP-03 |
| T-06 | Verificada | API versionada para organización, Departments, usuarios, roles, niveles y grants; Problem Details 403/404/409/422; auditoría atómica, control de versiones, último ADMIN protegido y revocación de assignments/grants al desactivar. | working-tree / CP-03 |
| T-07 | Verificada | Suite completa: Domain/Unit, integración SQL Server efímera con bootstrap/JIT/concurrencia y API/E2E `401`; 20 pruebas correctas. | working-tree / CP-04 |
| T-08 | Verificada | `docs/organization-operations.md` documenta despliegue, bootstrap sin secretos, JIT, autorización, concurrencia, auditoría, recuperación y frontera con la siguiente SPEC. | working-tree / CP-04 |

## Checkpoints

### CP-01 — 2026-09-08 06:10 -05 — Bloque 1

- Tareas: T-01, T-02.
- Cambios: modelo de organización, Legal Entity, Departments, perfiles JIT/lifecycle, scopes, role assignments, niveles, grants, matriz de autoridad y resolver de elegibilidad con `EligibilityEvidence` serializable.
- Tests y checks: `dotnet restore ProcureToPay.sln --force-evaluate`; `dotnet build ProcureToPay.sln --no-restore`; `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` (14 correctos); `run_lint.py` del run válido.
- Resultado: invariantes y elegibilidad verificadas; no se crean Approval Tasks ni se persisten snapshots en este bloque.
- HEAD: working-tree sobre `spec-01-organizacion-identidad-roles-autoridades-aprobacion`.
- Próximo paso: implementar persistencia, migración y concurrencia de T-03.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Requiere verificación manual | Bootstrap, idempotencia, divergencia y recovery auditado verificados en SQL Server efímero; falta prueba automatizada del último ADMIN desde API. | CP-02 / CP-04 |
| CA-02 | Requiere verificación manual | JIT secuencial y concurrente verificado; `/api/v1/me` y 401 cubiertos, falta E2E con JWT válido pendiente y 403 empresarial. | CP-03 / CP-04 |
| CA-03 | Requiere verificación manual | Invariantes de activación y ausencia de rol por defecto implementadas; falta contrato HTTP completo. | CP-01 / CP-03 |
| CA-04 | Requiere verificación manual | ADMIN local/global, revocación y último administrador implementados; falta prueba E2E de AUDITOR limitado por scope. | CP-01 / CP-03 / CP-04 |
| CA-05 | Cumplido | Matriz, scopes, grants, exclusiones y evidencia versionada cubiertos por pruebas unitarias; COST_CENTER rechazado. | CP-01 |
| CA-06 | Requiere verificación manual | Índices/FKs y reglas de dominio implementados; falta suite de transición Department contra datos relacionados. | CP-01 / CP-02 |
| CA-07 | Requiere verificación manual | Modelo valida configuración y API permite nombre/zona versionados; falta contrato HTTP de campos inmutables y segunda organización. | CP-02 / CP-03 |
| CA-08 | Cumplido | Resolver devuelve candidatos activos, exclusiones y snapshot inmutable; pruebas unitarias cubren cambios posteriores y conjunto vacío. | CP-01 |
| CA-09 | Requiere verificación manual | Auditoría atómica y append-only implementadas para comandos expuestos; falta prueba de rollback forzado y subcambios compuestos. | CP-03 / CP-04 |
| CA-10 | Requiere verificación manual | Desactivación revoca assignments/grants y retorno no restaura privilegios; falta prueba API/E2E de grants futuros. | CP-03 / CP-04 |
| CA-11 | Requiere verificación manual | 401 E2E y taxonomía Problem Details implementada; faltan pruebas HTTP completas para 403/404/409/422 y captura de telemetría. | CP-03 / CP-04 |

## Verificaciones manuales

| Criterio | Procedimiento | Resultado | Confirmado por/fecha |
|---|---|---|---|
| — | No hay verificaciones manuales previstas en este momento. | — | — |

## Desviaciones y bloqueos

- Ninguno.

### CP-02 — 2026-09-08 06:31 -05 — Bloque 2

- Tareas: T-03, T-04.
- Cambios: entidades EF del módulo `Organization`, schema explícito, índices únicos, tokens rowversion, factory de diseño, migraciones `OrganizationFoundation` y `OrganizationBootstrapMarker`, bootstrap serializable/idempotente y recuperación administrativa fuera de HTTP.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (18 correctos); `run_lint.py` del run válido.
- Resultado: persistencia y bootstrap verificados contra SQL Server efímero; no se ejecutaron migraciones contra datos reales.
- HEAD: working-tree sobre `spec-01-organizacion-identidad-roles-autoridades-aprobacion`.
- Próximo paso: implementar integración JWT/JIT y los contratos administrativos.

### CP-03 — 2026-09-08 07:18 -05 — Bloque 3

- Tareas: T-05, T-06.
- Cambios: provisioning JIT para identidades JWT, middleware posterior a autenticación, endpoint de estado propio, API administrativa versionada, autorización local `ADMIN`/`AUDITOR`, mutaciones versionadas, Problem Details por categoría, protección del último administrador, revocación atómica de permisos y documentación operativa.
- Tests y checks: `dotnet restore ProcureToPay.sln --force-evaluate`; `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (20 correctos); `python3 /home/osiosad/.pi/agent/skills/spec/scripts/run_lint.py specs/runs/01-organizacion-identidad-roles-autoridades-aprobacion.md --specs-dir specs` (0 avisos); `git diff --check` limpio.
- Resultado: primer JWT desconocido queda `PENDING_SETUP`; la API exige bearer token y las mutaciones administrativas se auditan en la misma operación.
- HEAD: working-tree sobre `spec-01-organizacion-identidad-roles-autoridades-aprobacion`.
- Próximo paso: completar pruebas negativas/regresión, recorrer criterios de aceptación y ejecutar revisión independiente.

### CP-04 — 2026-09-08 07:35 -05 — Verificación final preliminar

- Tareas: T-07, T-08.
- Checks: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (20 correctos); `git diff --check` limpio; `run_lint.py` válido (0 avisos); `specctl check 01 --approval` válido; `specctl doctor` válido.
- Cobertura: invariantes, elegibilidad, migración/schema, bootstrap idempotente/divergente/recovery, JIT secuencial y concurrente, endpoint protegido sin bearer y regresión completa.
- Limitaciones que requieren revisión: no se ejecutó un emisor Keycloak real; no todos los contratos HTTP negativos tienen pruebas E2E; la lectura `AUDITOR` acotada por scope y algunas reglas de transición de Department requieren confirmación contra escenarios de integración.
- Estado: no se marca aún `Lista para integrar`; falta revisión independiente y confirmación de criterios pendientes.

## Verificación independiente

> **Resultado:** Pendiente
> **Método:** Subagente `sdd-implementation-reviewer`
> **Fecha:** Pendiente

- Conformidad con la spec: Pendiente.
- Cobertura de criterios: Pendiente.
- Cambios fuera de alcance: Pendiente.
- Riesgos residuales: Pendiente.

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `specs/01-organizacion-identidad-roles-autoridades-aprobacion.md` | Metadato administrativo de ejecución | Flujo `/spec-impl` |
| `specs/runs/01-organizacion-identidad-roles-autoridades-aprobacion.md` | Registro de ejecución | Todas |
| `src/ProcureToPay.Domain/Modules/Organization/OrganizationModels.cs` | Entidades, estados y scopes del dominio | T-01 / CA-03, CA-05, CA-06, CA-07, CA-10 |
| `src/ProcureToPay.Domain/Modules/Organization/AuthorizationAssignments.cs` | Role assignments, authority levels, grants y reglas de solapamiento | T-01 / CA-04, CA-05, CA-08 |
| `src/ProcureToPay.Domain/SharedKernel/DomainRuleExceptions.cs` | Excepciones tipadas de validación y conflicto | T-01 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationPersistenceModels.cs` | Modelo de persistencia del módulo Organization | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/ProcureToPayDbContext.cs` | DbSets, schema, índices y concurrencia | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/ProcureToPayDbContextFactory.cs` | Contexto EF para diseño/migraciones sin secretos | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260908111011_OrganizationFoundation.cs` | Migración inicial del módulo | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260908115345_OrganizationBootstrapMarker.cs` | Migración del marcador singleton de bootstrap | T-03 / T-04 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationBootstrapper.cs` | Bootstrap y recuperación break-glass transaccionales | T-04 |
| `tests/ProcureToPay.IntegrationTests/Organization/OrganizationPersistenceModelTests.cs` | Verificación de schema, índices y rowversion | T-03 |
| `tests/ProcureToPay.IntegrationTests/Organization/OrganizationBootstrapperTests.cs` | Verificación SQL Server de bootstrap, idempotencia y recovery | T-04 |
| `tests/ProcureToPay.UnitTests/Organization/OrganizationAuthorizationTests.cs` | Pruebas de invariantes organizacionales y de autorización | T-01 / CA-03, CA-04, CA-05, CA-06, CA-07, CA-10 |
| `tests/ProcureToPay.IntegrationTests/Organization/OrganizationBootstrapperTests.cs` | Bootstrap, JIT secuencial y concurrencia | T-04 / T-05 / CA-01, CA-02 |
| `tests/ProcureToPay.ApiE2ETests/UnitTest1.cs` | Endpoint protegido sin credenciales | T-06 / CA-11 |
| `src/ProcureToPay.Domain/Modules/Organization/Eligibility.cs` | Matriz, contrato de solicitud, evidencia y resolver de elegibilidad | T-02 / CA-05, CA-08 |
| `tests/ProcureToPay.UnitTests/Organization/EligibilityResolverTests.cs` | Pruebas de matriz, autoridad, scopes, exclusión y snapshots | T-02 / CA-05, CA-08 |
| `src/ProcureToPay.Infrastructure/DependencyInjection.cs` | Registro de bootstrapper y provisioning JIT | T-04 / T-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/CurrentUserProvisioningService.cs` | Alta JIT y autorización basada en asignaciones locales | T-05 |
| `src/ProcureToPay.Api/Identity/CurrentUserProvisioningMiddleware.cs` | Provisioning después de autenticar JWT | T-05 |
| `src/ProcureToPay.Api/Controllers/OrganizationController.cs` | API versionada de lectura y administración | T-06 |
| `src/ProcureToPay.Api/ExceptionHandling/ApiExceptionHandler.cs` | Taxonomía Problem Details | T-06 |
| `src/ProcureToPay.Api/Program.cs` | Pipeline JWT, middleware JIT y versionado | T-05 / T-06 |
| `tests/ProcureToPay.ApiE2ETests/UnitTest1.cs` | Contrato HTTP de endpoint protegido | T-06 |
| `docs/organization-operations.md` | Operación, recuperación y frontera funcional | T-08 |

## Cierre

- HEAD verificado: Pendiente.
- Estrategia de integración: merge/rebase/squash/commit directo por decidir al integrar.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Ninguno.
