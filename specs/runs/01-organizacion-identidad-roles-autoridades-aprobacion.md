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
> **Actualizado:** 2026-09-08 18:00 -05
> **HEAD verificado:** 61afd78e465f0a3054116906ae15980cf8325ebd
> **Commit de integración:** Pendiente

## Línea base

| Comando o comprobación | Resultado | Evidencia breve |
| --- | --- | --- |
| `specctl check 01 --approval` | Pasa | Spec aprobada, sdd/v3, digest válido, sin avisos. |
| `specctl doctor` | Pasa | Estado administrativo válido, sin avisos. |
| `dotnet build ProcureToPay.sln --no-restore` | Pasa | Build .NET 10 correcto, 0 advertencias, 0 errores. |
| `dotnet test --solution ProcureToPay.sln --no-restore` | Pasa | 3 ensamblados ejecutados, 26 tests correctos, 0 errores. |
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
| T-07 | Verificada | Suite Domain/Unit, integración SQL Server efímera con bootstrap/JIT/concurrencia/CLI y API/E2E con JWT firmado localmente, 401/403, auditoría scoped, elegibilidad y health; 26 pruebas correctas. | `61afd78` / CP-11 |
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
| CA-01 | Cumplido | Integración SQL Server verifica bootstrap idempotente/divergente/recovery; E2E verifica último ADMIN/protección y prueba de proceso ejecuta ambos comandos CLI fuera de HTTP. | CP-02 / CP-11 |
| CA-02 | Cumplido | E2E atraviesa `JwtBearer` con JWT firmado y validación de issuer/audience/firma/expiración, JIT `PENDING_SETUP`, `/api/v1/me`, activación y revocación efectiva. | CP-03 / CP-11 |
| CA-03 | Cumplido | Dominio y E2E verifican lifecycle pendiente/setup/activo/inactivo y ausencia de privilegios implícitos; API versionada responde con DTOs contractuales. | CP-01 / CP-11 |
| CA-04 | Cumplido | Catálogo cerrado y scopes explícitos; E2E verifica ADMIN, AUDITOR scoped/minimizado, mutación `403`, asignación, desactivación y revocación efectiva. | CP-01 / CP-11 |
| CA-05 | Cumplido | Matriz, scopes, grants, exclusiones y evidencia versionada cubiertos por pruebas unitarias; COST_CENTER rechazado. | CP-01 |
| CA-06 | Cumplido | Integración verifica schema/FK/índices; pruebas de dominio y API cubren lifecycle de Department, referencias activas y rechazo de desactivación con referencias. | CP-01 / CP-02 / CP-11 |
| CA-07 | Cumplido | Catálogo ISO vigente (incluye `ZWG`/`XCG`, excluye retirados), zona IANA mediante tzdb (`CET` incluido), mes fiscal y campos inmutables/versionados cubiertos por dominio/API. | CP-02 / CP-11 |
| CA-08 | Cumplido | Resolver devuelve candidatos activos, exclusiones y snapshot inmutable; pruebas unitarias cubren cambios posteriores y conjunto vacío. | CP-01 |
| CA-09 | Cumplido | Bootstrap/recovery/desactivación generan un registro padre con subcambios ordenados y snapshots; integración/E2E validan actor, versiones, before/after, atomicidad y ausencia de mutación ante fallo. | CP-03 / CP-11 |
| CA-10 | Cumplido | Dominio y E2E validan conservación del último ADMIN, desactivación atómica, revocación de assignments/grants y que una petición posterior no conserva autorización. | CP-03 / CP-11 |
| CA-11 | Cumplido | E2E verifica JWT inválido/401, 403, 400 JSON y dominio, 404, 409, health, elegibilidad 200 y Problem Details; logs estructurados no contienen tokens. | CP-03 / CP-11 |

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

### CP-05 — 2026-09-08 12:42 -05 — Correcciones posteriores a revisión

- Cambios: middleware JIT con resolución scoped por request; scopes API tipados y validados contra referencias activas (`Organization`, `LegalEntity`, `Department`); índices persistentes de singleton Legal Entity y assignments activos; moneda/límite de grants; evidencia de elegibilidad ampliada con snapshots de scope, vigencia, límites y nivel; revocación transaccional del último ADMIN; transiciones y lecturas administrativas adicionales; Problem Details 400/409/401.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (20 correctos); `git diff --check` limpio.
- HEAD: `36df88b095b2fb01a230e975f9e1b34727af16f6` en rama aislada.
- Próximo paso: segunda revisión independiente; no integrar mientras queden criterios sin evidencia HTTP completa.

## Verificación independiente

> **Resultado:** BLOCKED (confirmado en segunda revisión)
> **Método:** Subagente `sdd-implementation-reviewer`
> **Fecha:** 2026-09-08 (segunda pasada)

- Conformidad con la spec: BLOCKED; persisten gaps en transiciones de usuarios, scopes compuestos/solapamiento, API de lectura/administración, catálogos, auditoría reproducible, health operativo y pruebas HTTP.
- Cobertura de criterios: CA-05 y CA-08 con cobertura unitaria; CA-01–CA-04 y CA-06–CA-11 no demostrados completamente.
- Cambios fuera de alcance: Ninguno identificado.
- Riesgos residuales: no integrar hasta cerrar los blockers listados por el revisor independiente.

### Hallazgos bloqueantes de revisión independiente

1. La API debe validar scopes tipados (incluidos `LEGAL_ENTITY`), referencias activas y filtrar lecturas `AUDITOR` por scope.
2. Faltan operaciones administrativas de Legal Entity y transiciones de Department, además de pruebas HTTP negativas completas.
3. Grants/elegibilidad deben validar moneda, límites, catálogo y producir evidencia reproducible completa.
4. Último `ADMIN`, auditoría padre/subcambios y concurrencia requieren transacción y versiones reales.
5. Run/implementación deben volver a verificarse tras corregir los puntos anteriores.

### CP-06 — 2026-09-08 13:35 -05 — Correcciones adicionales

- Cambios: guards de estado en dominio/API; scopes como colección tipada con `LEGAL_ENTITY`, referencias activas, scopes compuestos y solapamiento real; catálogo ISO 4217 derivado de culturas; evidencia de elegibilidad copiada a estructuras inmutables; auditoría con versiones y subcambios; lectura auditada; health check de bootstrap; Problem Details uniforme para model-state, autenticación, concurrencia y conflictos.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (21 correctos); `git diff --check` limpio.
- HEAD: `86f3ba78afab733d405e312375b2f65b1ab7ff6b` en rama aislada.
- Estado: pendiente de nueva revisión independiente; no se declara integración.

### CP-07 — 2026-09-08 14:05 -05 — API, elegibilidad y operación

- Cambios: operaciones de renombrado de Legal Entity, revocación scoped por identificador, retiro/versionado de niveles, lectura filtrada de roles/grants/auditoría con snapshots, operación persistida `POST /api/v1/eligibility`, validación determinista ISO 4217 y UTC, health `/health/bootstrap`, logs estructurados JIT y transacciones serializables en mutaciones de roles/departamentos.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (21 correctos); lint de run, `specctl check 01 --approval`, `specctl doctor` y `git diff --check` correctos.
- HEAD: `a531e70bce8315e21ff7b2754d8b53920118bf4e` en rama aislada.
- Estado: pendiente de nueva revisión independiente; no se declara integración.

### CP-10 — 2026-09-08 16:10 -05 — Cobertura HTTP controlada

- Cambios: suite API/E2E con SQL Server efímero y autenticación controlada cubre JIT válido, estado pendiente, 403 empresarial, ADMIN, AUDITOR scoped con minimización, mutación denegada, health y respuestas 400/404/409; el endpoint de elegibilidad devuelve DTOs contractuales.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (24 correctos); `run_lint.py`, `specctl check 01 --approval`, `specctl doctor`, `git diff --check` y `git fsck --full` correctos.
- HEAD: `1af8f046ff675103d9649c0f9a3e63be5ed81142` en rama aislada.
- Estado: pendiente de revisión independiente final; no se declara integración.

### CP-11 — 2026-09-08 18:00 -05 — Cierre de blockers contractuales

- Cambios: E2E usa `JwtBearer` con JWT localmente firmado y validación de issuer/audience/firma/expiración; se cubren lifecycle setup/activate/deactivate y revocación HTTP, elegibilidad exitosa con evidencia, errores 400/404/409, zona IANA `CET`, catálogo ISO vigente, orden total de ranks y `ExpectedPreviousVersion`.
- Cambios de trazabilidad: bootstrap/recovery/desactivación ahora conservan snapshots `before`/`after` por subcambio; el fingerprint excluye el motivo operativo y los procesos CLI se ejecutan desde el ensamblado publicado fuera de HTTP.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (26 correctos); `run_lint.py`, `specctl check 01 --approval`, `specctl doctor`, `git diff --check` y `git fsck --full --no-dangling` correctos.
- Estado: listo para revisión independiente final; no se declara integración hasta obtener `PASS`.
- HEAD de código: `61afd78e465f0a3054116906ae15980cf8325ebd`.

### Segunda revisión — hallazgos históricos

La revisión independiente anterior (`f114666`) fue `BLOCK` sobre el estado previo a CP-11. Sus observaciones se cerraron en el checkpoint siguiente y se conservan aquí como historial, no como estado vigente.

### CP-08 — 2026-09-08 14:45 -05 — Cierre de salvaguardas adicionales

- Cambios: códigos contractuales explícitos sin aliases numéricos, puerto de Application para elegibilidad, restauración con versiones persistidas, lectura AUDITOR por asignación compuesta, privacidad JIT seudonimizada, auditoría con scopes afectados y subcambios ordenados, transacciones serializables grant/departamento, y comandos operativos separados de bootstrap/recovery.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); `dotnet test --solution ProcureToPay.sln --no-restore` (21 correctos); lint, `specctl check 01 --approval`, `specctl doctor`, `git diff --check` y `git fsck --full` correctos.
- HEAD: `0ce1dbff7f66485c67189c38d1b08e7271c733b1` en rama aislada.
- Estado: pendiente de nueva revisión independiente; no se declara integración.

### CP-09 — 2026-09-08 15:20 -05 — Códigos y versiones verificables

- Cambios: versionado de niveles retirable con creación de nueva versión, pruebas de códigos contractuales y restauración de versiones persistidas, más correcciones de auditoría por scope/orden y validación monetaria de elegibilidad.
- Verificación: `dotnet build ProcureToPay.sln --no-restore` (0 advertencias, 0 errores); pruebas unitarias (17 correctas); validaciones de run/specctl/doctor/diff-check previas correctas.
- HEAD: `163832ecab1faa06c9eb90976490f2be2b2e3ff9` en rama aislada.
- Estado: pendiente de nueva revisión independiente; no se declara integración.

La segunda pasada confirmó middleware scoped, validación básica de referencias y transacciones del último ADMIN, pero bloqueó el estado anterior por guards de transición de usuarios, scopes compuestos, API HTTP, catálogo ISO/IANA, versionado, snapshots de auditoría y cobertura E2E. CP-11 incorpora esas correcciones: JWT Bearer firmado, lifecycle/revocación HTTP, elegibilidad exitosa, rank/versionado, catálogo vigente, snapshots before/after y prueba de CLI.

La rama mantiene todos los commits de implementación y checkpoints administrativos; el HEAD verificable actual es `61afd78e`. No se ha hecho merge a `main`.

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `specs/01-organizacion-identidad-roles-autoridades-aprobacion.md` | Metadato administrativo de ejecución | Flujo `/spec-impl` |
| `specs/runs/01-organizacion-identidad-roles-autoridades-aprobacion.md` | Registro de ejecución | Todas |
| `src/ProcureToPay.Domain/Modules/Organization/OrganizationModels.cs` | Entidades, estados y scopes del dominio | T-01 / CA-03, CA-05, CA-06, CA-07, CA-10 |
| `src/ProcureToPay.Domain/Modules/Organization/AuthorizationAssignments.cs` | Role assignments, authority levels, grants y reglas de solapamiento | T-01 / CA-04, CA-05, CA-08 |
| `src/ProcureToPay.Domain/Modules/Organization/ContractCodes.cs` | Catálogo de códigos contractuales de roles, autoridades, scopes y estados | T-01 / T-06 |
| `src/ProcureToPay.Domain/Modules/Organization/CurrencyCatalog.cs` | Catálogo ISO 4217 vigente de moneda base | T-01 / CA-07 |
| `src/ProcureToPay.Domain/Modules/Organization/Eligibility.cs` | Matriz y resolver determinista con evidencia serializable | T-02 / CA-05, CA-08 |
| `src/ProcureToPay.Domain/SharedKernel/DomainRuleExceptions.cs` | Excepciones tipadas de validación y conflicto | T-01 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationPersistenceModels.cs` | Modelo de persistencia del módulo Organization | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/ProcureToPayDbContext.cs` | DbSets, schema, índices y concurrencia | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/ProcureToPayDbContextFactory.cs` | Contexto EF para diseño/migraciones sin secretos | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260908111011_OrganizationFoundation.cs` | Migración inicial del módulo | T-03 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260908115345_OrganizationBootstrapMarker.cs` | Migración del marcador singleton de bootstrap | T-03 / T-04 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationBootstrapper.cs` | Bootstrap y recuperación break-glass transaccionales | T-04 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationEligibilityService.cs` | Adaptador persistido para elegibilidad | T-02 / T-06 |
| `src/ProcureToPay.Infrastructure/Persistence/Organization/OrganizationBootstrapHealthCheck.cs` | Health check del estado de bootstrap | T-06 / T-08 |
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
| `src/ProcureToPay.Api/Health/OrganizationBootstrapHealthCheck.cs` | Registro HTTP del health check de bootstrap | T-06 / T-08 |
| `src/ProcureToPay.Application/Abstractions/IOrganizationEligibilityService.cs` | Puerto Application de elegibilidad | T-02 / T-06 |
| `src/ProcureToPay.Api/Program.cs` | Pipeline JWT, middleware JIT y versionado | T-05 / T-06 |
| `tests/ProcureToPay.ApiE2ETests/UnitTest1.cs` | Contrato HTTP con JWT Bearer, lifecycle, scopes, elegibilidad y Problem Details | T-06 / T-07 |
| `tests/ProcureToPay.IntegrationTests/Organization/OrganizationBootstrapperTests.cs` | Bootstrap, JIT secuencial y concurrencia | T-04 / T-05 / CA-01, CA-02 |
| `tests/ProcureToPay.UnitTests/Organization/EligibilityResolverTests.cs` | Pruebas unitarias de matriz, autoridad y evidencia | T-02 / CA-05, CA-08 |
| `docs/organization-operations.md` | Operación, recuperación y frontera funcional | T-08 |

## Cierre

- HEAD verificado: 61afd78e465f0a3054116906ae15980cf8325ebd.
- Estrategia de integración: checkpoints `4f6dff4`, `36df88b`, `86f3ba7`, `df33285`, `f14c017`, `a531e70`, `0ce1dbf`, `163832e`, `ded2410`, `1af8f04`, `aacdb57` y `61afd78` en rama aislada; integración aún bloqueada por revisión independiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Ninguno.
