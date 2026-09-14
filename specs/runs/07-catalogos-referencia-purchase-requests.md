# RUN SPEC 07 — Catálogos de referencia para Purchase Requests y scope Cost Center

> **Formato:** sdd-run/v2
> **Estado del run:** Integrado
> **Spec:** specs/07-catalogos-referencia-purchase-requests.md
> **Revisión contractual:** 1
> **Commit de la spec:** 40e6678e70120be5c0b572646f2d57cf980f51d1
> **Blob aprobado:** 168e32feb61b7329e7126b3a9fb0411e12bf0166
> **Digest contractual:** 64daf9c60d28f2d9a2dc6c286a8e2f12d83dd97a01a7651a3a72e73681f0eed1
> **Rama base:** main
> **Commit base:** 40e6678e70120be5c0b572646f2d57cf980f51d1
> **Rama de implementación:** spec-07-catalogos-referencia-purchase-requests
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-14 11:12 -0500
> **Actualizado:** 2026-09-14 19:05 -0500
> **HEAD verificado:** b72447a9e00fcdffb666f847678379a37b962ed5
> **Commit de integración:** ad588b2ba7a28fa35fc6c07a9bf2d645579ae45c

## Línea base

- `dotnet restore ProcureToPay.sln` → 0 errores.
- `dotnet build ProcureToPay.sln --no-restore` → 0 errores, 3 avisos preexistentes (CS0105 en `PolicyEvaluatorTests`, CS9113 en `ApprovalWorkflowWorker`, CS8602 en `ApprovalDecisionIntegrationTests`).
- `tests/ProcureToPay.UnitTests/bin/Debug/net10.0/ProcureToPay.UnitTests` → 167/167 correctas.
- `tests/ProcureToPay.IntegrationTests/bin/Debug/net10.0/ProcureToPay.IntegrationTests` → 63/63 correctas (Testcontainers/Docker).
- `tests/ProcureToPay.ApiE2ETests/bin/Debug/net10.0/ProcureToPay.ApiE2ETests` → 21/21 correctas (Testcontainers/Docker).
- Desviación de entorno ya conocida: el wrapper `dotnet test` reporta 0 pruebas; la evidencia válida usa las asambleas compiladas directamente. Rama `spec-07-catalogos-referencia-purchase-requests` sobre `main` en `40e6678e`.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio `ReferenceCatalogs` (código/versión/digest, proyecciones y canonicalizador `spend-category/v1`), persistencia raíz+versiones append-only con `predecessor_version`, audit atómico y migración `Spec07ReferenceCatalogs` con CHECK de monotonicidad y triggers append-only/identidad estable. Evidencia: `ReferenceCatalogCanonicalizationTests` (3), `ReferenceCatalogPersistenceTests` (11: versiones/audit, unicidad case-insensitive, carrera de updates, bloqueo por scope, reactivación sin Department activo, formas inválidas de lookup, SQL directo rechazado, carrera desactivación↔scope, digest por versión, Department current, catálogos Policy/resolver). | working-tree sobre `40e6678e` / CP-01–CP-02 |
| T-02 | Verificada | `ReferenceCatalogController` con mutaciones `ADMIN`, lecturas mínimas para usuario activo, historia por scope y Problem Details; `BuildScopeJsonAsync` acepta `COST_CENTER`; desactivación de Department bloqueada por Cost Center current activo. Evidencia: `ReferenceCatalogE2ETests` (`Admin_mutations_and_scoped_reads_follow_the_contract`, `Deactivation_is_blocked_while_a_live_reference_exists`). | working-tree sobre `40e6678e` / CP-03 |
| T-03 | Verificada | `COST_CENTER` habilitado en `AuthorizationScopeSet`, cobertura exacta por código, sin herencia Department↔Cost Center, bloqueo de desactivación con scopes vigentes y reconciliación existente al revocar. Evidencia: `OrganizationAuthorizationTests.Scope_set_requires_explicit_non_empty_scope_and_accepts_cost_centers`, `ReferenceCatalogPersistenceTests.Deactivation_is_blocked_by_active_scopes_and_allowed_after_the_revoke`, asignación de rol con scope Cost Center en `ReferenceCatalogE2ETests`. | working-tree sobre `40e6678e` / CP-03 |
| T-04 | Verificada | `decision-scope/v1` admite Cost Center UUID/versión; `ApprovalScopeResolver` resuelve id/versión a código current activo; `PolicyReferenceLookup` transporta `organization_id` con nulos reales; catálogos Policy exact-one `COST_CENTER`/`SPEND_CATEGORY`. Evidencia: `ApprovalContractTests` (round-trip Cost Center), `ReferenceCatalogPersistenceTests.Policy_and_approval_resolution_is_organization_bound_and_fail_closed`. | working-tree sobre `40e6678e` / CP-03 |
| T-05 | Verificada | Seis slots owner declarados explícitamente, `ReferenceType` no anulable sin wildcard y registro rechazado si `owner_id`/contrato están vacíos; owners reales Cost Center (actividad y relación exacta) y Spend Category (código/versión/digest). Evidencia: `PurchaseRequestReferenceOwnerRegistryTests` (3), `ReferenceCatalogOwnerTests` (3). | working-tree sobre `40e6678e` / CP-04 |
| T-06 | Verificada | Attestation valida eco, `owner_id`, `owner_contract_version`, vocabulario cerrado de `status` y coherencia `active`/`status`; submit real con owners y catálogos de producción (sin doubles) atestigua, evalúa Policy, abre caso con scope Cost Center y asigna solo al candidato cubierto; referencias stale, slot ausente, respuesta incoherente, fallo de catálogo Policy tras atestiguar y carreras de dos instancias fallan cerrado sin duplicar artefactos y con retry recuperable. Evidencia: `ReferenceCatalogSubmitIntegrationTests` (5). | working-tree sobre `40e6678e` / CP-04 |
| T-07 | Verificada | Health por slot/catálogo/storage con gramática exacta, contrato e identidad por slot y sin datos; E2E de cardinalidad 0/2, contrato incorrecto, corrupción de puntero/digest y storage indisponible; runbook `docs/reference-catalogs-operations.md`; regresión completa. Evidencia: `ReferenceCatalogE2ETests.Health_distinguishes_missing_ambiguous_and_corrupted_catalogs`, `PurchaseRequestE2ETests.Health_reports_only_the_really_missing_registrations`. | working-tree sobre `40e6678e` / CP-05 |

## Checkpoints

### CP-01 — Bloque 1: modelo, persistencia y migración

Dominio `ProcureToPay.Domain/Modules/ReferenceCatalogs` con validación de código/nombre/motivo, proyecciones `cost-center-reference/v1` y `spend-category-reference/v1` y digest reproducible. Persistencia `ReferenceCatalog` (raíces + versiones append-only, índices únicos por organización) y migración `Spec07ReferenceCatalogs` aplicada por los arneses de integración y E2E. `ReferenceCatalogCanonicalizationTests` fija el vector SHA-256 publicado.

### CP-02 — Bloque 1: carreras y constraints

`ReferenceCatalogPersistenceTests` demuestra una sola versión sucesora bajo dos writers, unicidad de código sin distinguir mayúsculas, versión stale `409`, digest por versión y proyección con la versión Department current. Los conflictos de EF se traducen a conflicto de contrato en la misma transacción.

### CP-03 — Bloque 2: scope, Policy y API

`COST_CENTER` habilitado en `AuthorizationScopeSet` y `decision-scope/v1`; `ApprovalScopeResolver` resuelve el par UUID/versión contra el catálogo. Catálogos Policy `COST_CENTER`/`SPEND_CATEGORY` registrados exact-one y ligados a `organization_id`. `ReferenceCatalogController` administra con motivo/versión/audit y publica lecturas mínimas e historia por scope. E2E: mutaciones, errores `400/403/404/409`, historia global vs `COST_CENTER` vs `DEPARTMENT` y bloqueos de desactivación.

### CP-04 — Bloque 3: owners y submit real

Seis slots owner explícitos y owners reales de Cost Center/Spend Category con respuestas `ACTIVE|INACTIVE|NOT_FOUND` exactas. `PurchaseRequestAttestationService` valida identidad, contrato y coherencia de estado. `ReferenceCatalogSubmitIntegrationTests` ejecuta el recorrido sin doubles: attestation real → provider → Policy con catálogos reales → Approval con scope Cost Center, asignando solo al candidato cuyo assignment cubre `CC-IT-DEV`; un rename que vuelve stale la referencia falla `422` sin manifest ni caso.

### CP-05 — Bloque 4: health, documentación y regresión

Health distingue slots ausentes/ambiguos (`PURCHASE_REQUEST_OWNER_UNAVAILABLE:<ASSERTION>:<REFERENCE>`), catálogos Policy ausentes, corrupción de puntero/digest y storage indisponible, sin exponer códigos de negocio, ids, nombres ni digests; un catálogo vacío no degrada. Runbook `docs/reference-catalogs-operations.md` con despliegue, scopes, owner table, health y recuperación. Suites finales: Unit **172/172**, Integración **75/75**, API/E2E **24/24**, build 0 errores.

### CP-06 — Correcciones de la revisión independiente (ronda 1)

Triaje de R1–R10: reactivación de Cost Center exige Department current activo; lookups Policy validan exactamente la nulabilidad por tipo; registry y health verifican identidad y contrato por slot (no solo cardinalidad); attestation rechaza status fuera del vocabulario; la desactivación serializa con la creación de scopes vía lectura bloqueante `UPDLOCK/HOLDLOCK` en aislamiento serializable; la migración añade CHECK de monotonicidad y triggers append-only/identidad estable; `Down` deja de ser destructivo; submit real añade negativos de owner/catálogo incoherente, fallo Policy recuperable tras atestiguar y carrera de dos instancias. Suites: Unit **173/173**, Integración **82/82**, API/E2E **24/24**, build 0 errores.

### CP-07 — R8 cerrado: historia sin saltos ni punteros huérfanos

La ronda delta detectó que los CHECK solo exigían `PredecessorVersion = Version - 1` y los triggers de raíz solo impedían retrocesos: con current=1 era posible insertar versión 3/predecessor=2 y avanzar el puntero. Se añadieron triggers de inserción que exigen que la versión sea exactamente `current + 1` y que el predecessor exista en la misma raíz, y el trigger de raíz exige ahora `CurrentVersion = anterior + 1` y que la versión destino exista. La actualización de catálogos appendea primero la versión y después avanza el puntero dentro de la misma transacción serializable, de modo que el invariante no depende del orden de sentencias de EF. La regresión cubre salto con predecessor inexistente, puntero a versión inexistente y salto de Spend Category. Suites: Unit **173/173**, Integración **82/82**, API/E2E **24/24**, build 0 errores.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | `ReferenceCatalogPersistenceTests` (11) cubre crear/renombrar/reasignar/desactivar/reactivar con historia y audit, unicidad case-insensitive, stale `409`, carrera con una sola sucesora, reactivación solo con Department activo, rechazo por SQL directo de saltos de versión (incluido predecessor inexistente), punteros a versiones inexistentes, UPDATE/DELETE histórico y digest por versión; `ReferenceCatalogCanonicalizationTests` reproduce el SHA-256 `4ed4dc13…` y falla al alterar nombre, organización o versión. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-02 | Cumplido | `ReferenceCatalogE2ETests.Admin_mutations_and_scoped_reads_follow_the_contract` verifica lecturas mínimas por cualquier usuario activo, `403` para no-ADMIN, `400/404/409` de validación/unicidad/versión, historia global vs `COST_CENTER` (ajena `403`) vs `DEPARTMENT` (`403`) y Spend Category solo `ORGANIZATION`. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-03 | Cumplido | `OrganizationAuthorizationTests` fija cobertura global/exacta y ausencia de herencia Department↔Cost Center; el E2E acepta un assignment `COST_CENTER` real y rechaza código inexistente (`400`); la integración bloquea la desactivación con scope vigente y la libera tras revocar. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-04 | Cumplido | `ReferenceCatalogPersistenceTests.Policy_and_approval_resolution_is_organization_bound_and_fail_closed` prueba catálogos Policy válidos, versión stale, otra organización, digest distinto, formas de lookup inválidas (campos no nulos donde el contrato exige `null`) y resolución/`422` de `decision-scope/v1`; `ApprovalContractTests` cubre round-trip/digest con `COST_CENTER`; el E2E de health prueba registros 0 de ambos catálogos. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-05 | Cumplido | `PurchaseRequestReferenceOwnerRegistryTests` exige seis slots exactos, rechaza ausencia/ambigüedad y registros sin identidad/contrato; `ReferenceCatalogOwnerTests` verifica `ACTIVE/INACTIVE/NOT_FOUND` por antigüedad, organización, digest y relación exacta Cost Center→Department (incluida versión Department stale). | sdd-implementation-reviewer (ronda 3 delta) |
| CA-06 | Cumplido | `ReferenceCatalogSubmitIntegrationTests` (5) ejecuta el recorrido real sin owners ni catálogos permisivos: attestation con owners de catálogo, Policy con catálogos reales, caso con `"dimension":"COST_CENTER"` y asignación solo al candidato cubierto; replay recupera caso y artefactos; referencia stale, slot ausente, respuesta incoherente o fallo de catálogo Policy no crean manifest/caso (y el fallo Policy sí conserva manifest con attempt recuperable); dos presentaciones concurrentes producen un solo efecto. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-07 | Cumplido | `ReferenceCatalogE2ETests.Health_distinguishes_missing_ambiguous_and_corrupted_catalogs` cubre owner 0/2, contrato de owner incorrecto, catálogos 0, corrupción de puntero/digest y storage indisponible con la gramática exacta y sin datos de negocio; `PurchaseRequestE2ETests` confirma health estable con catálogo vacío y registros reales. | sdd-implementation-reviewer (ronda 3 delta) |
| CA-08 | Cumplido | Migración aplicada sobre la línea base en todos los arneses de integración/E2E (con CHECK/triggers append-only y `Down` no destructivo); scopes y digests históricos se conservan; suites completas Unit **173/173**, Integración **82/82**, API/E2E **24/24**, build 0 errores, `git diff --check` limpio y runbook `docs/reference-catalogs-operations.md` ejercitado por las pruebas de health y desactivación. | sdd-implementation-reviewer (ronda 3 delta) |

## Desviaciones y bloqueos

### Bloqueo material

- Ninguno.

### Desviaciones registradas

- **Ronda 3 autorizada.** El presupuesto automático es de dos llamadas; la ronda 3 delta se ejecutó solo para verificar el cierre de R8 con autorización explícita del usuario (ronda 3 autorizada).
- **Runner `dotnet test` con 0 pruebas.** Desviación de entorno ya documentada en SPEC 06: el wrapper `Microsoft.Testing.Platform` reporta 0 pruebas aunque compile. Toda la evidencia usa las asambleas compiladas directamente (`tests/<proyecto>/bin/Debug/net10.0/<proyecto>`), que son las mismas que produce el runner.
- **Modo HTTP de catálogos Policy.** La certificación cubre el modo in-process (sin `Policy:ReferenceCatalog:BaseUrl`), donde `COST_CENTER` y `SPEND_CATEGORY` quedan registrados exact-one. En el modo HTTP, el servicio externo debe servir ambas familias con la misma forma; queda documentado en el runbook y no se certifica aquí.

## Verificación independiente

> **Resultado:** Sin bloqueos
> **Rondas:** 3/3
> **Triaje:** PASS. R4/R5 resueltos en el diseño; R1/R2/R3/R6/R7/R9/R10 cerrados y confirmados en la ronda 2 delta; R8 cerrado en la ronda 3 delta acotada sobre `b72447a`, autorizada explícitamente por el usuario, sin hallazgos nuevos
> **Modelo efectivo:** sdd-implementation-reviewer · openai-codex/gpt-5.6-sol · effort high (metadatos de la herramienta)
> **Método:** Ronda 1 full sobre `50b0900` (Git, contrato, migración, evidencia; R1–R10). Ronda 2 delta sobre `b6a281b`: R1–R7, R9 y R10 cerrados; R8 reabierto porque el CHECK no validaba predecessor existente ni avance exacto del puntero. Ronda 3 delta acotada sobre `b72447a`: R8 resuelto (triggers de sucesor exacto/predecessor existente, puntero a fila existente, append antes del avance en la misma transacción y regresión SQL de salto/orfandad/puntero colgante); **PASS** sin hallazgos nuevos. Árbol revisado `b72447a9e00fcdffb666f847678379a37b962ed5`; suites Unit **173/173**, Integración **82/82**, API/E2E **24/24**, build 0 errores.
> **Fecha:** 2026-09-14

## Cierre

- **Integración:** merge `ad588b2ba7a28fa35fc6c07a9bf2d645579ae45c` de `spec-07-catalogos-referencia-purchase-requests` (tip `d15a7107c885a75fbb878133db3626166823bfa4`) sobre `main` (`40e6678e70120be5c0b572646f2d57cf980f51d1`), 2026-09-14.
- **Equivalencia:** el candidato verificado `b72447a9e00fcdffb666f847678379a37b962ed5` es ancestro del commit de integración; `git diff b72447a..HEAD -- src/ tests/ docs/` está vacío, por lo que el código integrado es exactamente el revisado (squash + merge, sin reauditoría).
- **Validaciones:** `dotnet build ProcureToPay.sln` 0 errores; Unit **173/173**, Integración **82/82**, API/E2E **24/24** sobre el árbol certificado; `specctl doctor` sin errores; `specctl git-check 07` coherente.
- **Vigencia:** SPEC 01, 02, 03, 05 y 06 quedan `Sustituida parcialmente por SPEC 07`; SPEC 07 pasa a `Implementada` / `Integrada` / `Vigente`.
