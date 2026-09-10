# RUN SPEC 02 — Motor de políticas de compras

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/02-motor-de-politicas.md
> **Revisión contractual:** 1
> **Commit de la spec:** be81039c1afc851575c9694d999d7f38ddeed573
> **Blob aprobado:** 3ea27731b44f4b30a69edcaf849aee1bef6d00ce
> **Digest contractual:** 63fec425400bc197524e23cb3a130ab2d7d64f6eba823944b3ebee8923e6d688
> **Rama base:** main
> **Commit base:** be81039c1afc851575c9694d999d7f38ddeed573
> **Rama de implementación:** spec-02-motor-de-politicas
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** balanced
> **Iniciado:** 2026-09-09 09:58 -05
> **Actualizado:** 2026-09-09 22:23 -05
> **HEAD verificado:** `7b77821`
> **Commit de integración:** Pendiente

## Línea base

| Comando o comprobación | Resultado | Evidencia breve |
| --- | --- | --- |
| `git status --short` | Pasa | Árbol limpio antes de crear la rama dedicada. |
| `specctl check 02 --approval` | Pasa | Spec aprobada, sdd/v3, digest válido y 0 avisos. |
| `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` | Pasa | 19 tests correctos, 0 errores. |
| `dotnet build ProcureToPay.sln --no-restore` | Pasa | Build correcto, 0 advertencias y 0 errores. |
| `git rev-parse HEAD` | Pasa | `be81039c1afc851575c9694d999d7f38ddeed573`. |
| `git rev-parse HEAD:specs/02-motor-de-politicas.md` | Pasa | Blob aprobado `3ea27731b44f4b30a69edcaf849aee1bef6d00ce`. |
| `intercom list-cwd` | Pasa | No hay otra sesión en el worktree. |

**Fallos preexistentes:** Ninguno observado en la línea base.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
| --- | --- | --- | --- |
| T-01 | Verificada | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`: 24 correctos; modelo de reglas tipadas, fallback por scope, validación de efectos, snapshots de authority y publicación inmutable cubiertos. | working-tree / Bloque 1 en curso |
| T-02 | Verificada | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`: 31 correctos; canonicalización/digest, evaluación LINE+REQUEST/SOURCING_PO, suma de líneas, fallback, precedencia BLOCK/PO, authority NONE y validaciones de publicación cubiertos. Se corrigió canonicalización snake_case y `ALLOW` no genera controles. | working-tree / Bloque 1 |
| T-03 | Verificada | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore`: 7 correctos; schema `Policy`, tablas append-only, índices de activación/retiro/idempotencia y rowversion verificados. `dotnet build` de Infrastructure correcto; migración `20260909152512_PolicyEngineFoundation` generada y compilable. | working-tree / Bloque 2 en curso |
| T-04 | Parcial | `PolicyPersistenceService`: selección serializable de activación, retiro append-only, auditoría administrativa, rowversion e idempotencia por SHA-256; reserva persistente `PolicyEvaluationReservations` con índice scoped y lease de 5 min antes del provider; `EvaluationSequence` persistida y latest lookup para sourcing; `PolicyPersistenceServiceTests` verifica reserva/liberación, espera concurrente, provider único, replay y conflicto. Aún falta cierre atómico de sucesor y diff/reevaluación completo. | `7b77821` / Bloque 2 en curso |
| T-05 | Parcial | `PolicyController` expone lectura scoped de versiones/evaluaciones, drafts, publicación+activación atómica, retiro y simulación tipada no persistente sobre snapshot; mutaciones validan pertenencia de draft/activation a `actor.OrganizationId`; edición versionada completa y límites API siguen pendientes. | `7b77821` / Bloque 3 en curso |
| T-06 | Parcial | Se agregó `PolicyFactRequest`, registry exact-one local, timeout 5 s, manifest de líneas, validación de digest de política y `EvaluateEnterprisePurchaseRequest` que carga la política activa desde persistencia mediante parser canónico. Replay: lookup scoped antes del provider, reserva distribuida por lease/índice único, lock local, fingerprint y validación de integridad; sourcing exige latest `EvaluationSequence`, result/facts/manifest digests; faltan catálogos completos y manifest de attestation exhaustivo. | `7b77821` / Bloque 3 en curso |
| T-07 | Parcial | `QuotationWaiverEvaluator` valida `from/to/floor` contra el control publicado, binding/nonce/evidence, autoridad y SoD; `ApplyVerifiedQuotationWaiver` actualiza controles/scopes y recalcula digest canónico; `AppendExceptionVerificationAsync` conserva request/evidence/reduced bundle en snapshot. Falta replay/reevaluación append-only y verifier registry de workflow. | `7b77821` / Bloque 3 en curso |
| T-08 | Parcial | `PolicyConfigurationHealthCheck` y `/health/policy` distinguen ausencia/ambigüedad y validan estado publicado + digest SHA-256 del contenido cargado sin exponer reglas; `ApiE2ETests` verifica `503` + `POLICY_CONFIGURATION_REQUIRED`; faltan corrupción/indisponibilidad HTTP. | `7b77821` / Bloque 4 en curso |
| T-09 | Parcial | Unitarias: 31 correctas; IntegrationTests: 8 correctas con SQL Server/Testcontainers; ApiE2ETests: 2 correctas (incluye `/health/policy` required); solución compila con 0 advertencias/errores; `lens_diagnostics --mode=all --severity=error`: sin errores. Se agregó replay real, corrupción fail-closed, reserva concurrente y actualidad persistida de sourcing. Falta ampliar evidencia negativa/golden/API específica de CA-01–CA-12. | `7b77821` / Bloque 4 |

## Checkpoints

### CP-01 — 2026-09-09 10:12 -05 — Bloque 1

- Tareas: T-01, T-02.
- Cambios: modelo tipado de PolicySetVersion, reglas/predicados/efectos, fallbacks, snapshots de authority, canonicalización `policy-canonical-json/v1`, SHA-256, evaluación LINE/REQUEST/SOURCING_PO y combinación cross-scope.
- Tests y checks: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` (30 correctos); build de UnitTests correcto (0 advertencias/errores); LSP primario sin diagnósticos en dominio. El LSP de tests conserva referencias stale a tipos nuevos, pero compilación y suite ejecutada los resuelven correctamente; queda como observación de tooling, no como error de código.
- Resultado: el motor puro evalúa todas las reglas coincidentes, suma importes de líneas, conserva controles por key y no crea Approval Tasks. Persistencia, API, providers y auditoría quedan para los bloques siguientes.
- HEAD: working-tree sobre `spec-02-motor-de-politicas`.
- Próximo paso: T-03, persistencia aditiva y migración compatible con SPEC 01.

### CP-02 — 2026-09-09 22:23 -05 — Replay, actualidad y scope

- Cambios: replay de `evaluation_key` usa `OrganizationId` del perfil cuando está disponible y valida fingerprint antes de facts; lookup scoped por organización después de facts; serialización por workload/operación/key para evitar doble adquisición concurrente en proceso; fingerprint scoped, rehidratación del bundle persistido e integridad contra `id`, subject, input/result digest; mutaciones administrativas y simulación filtran por `actor.OrganizationId`. Los preimages de input/result/facts omiten request timestamp del facts payload, usan `subject_ref`, `combined_result`, controles explícitos y códigos enum canónicos.
- Persistencia: la primera respuesta y los replays se rehidratan desde el mismo `BundleJson`; `PolicyEvaluationBundleRecord.ResultDigest` conserva el `evaluation_result_digest` contractual; la prueba SQL Server verifica JSON, secuencia y digest.
- Tests/checks: build 0/0; UnitTests 31/31; IntegrationTests 8/8; ApiE2ETests 2/2; `git diff --check` limpio; diagnósticos blocking del turno resueltos.
- Resultado: el replay secuencial y distribuido dispone de reserva persistente previa al provider con organización obligatoria, validación temprana de `evaluation_key`, lease de 5 minutos y el overload de snapshot del puerto ya delega en la ruta scoped; snapshots separados (`inputCanonicalJson` contractual + `requestSnapshotJson`) y serialización; no se declara PASS contractual: faltan pruebas HTTP del servicio real sin provider, corrupción 503 y CA-01–CA-12.
- HEAD: `7b77821`.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Pendiente | — | — |
| CA-02 | Pendiente | — | — |
| CA-03 | Parcial | CP-02/`PolicyCanonicalizer`: preimages explícitos para input/result, UUIDs/timestamps UTC y orden determinista de scopes/controles; falta golden vector contractual independiente. | Subagente / pendiente de PASS |
| CA-04 | Pendiente | — | — |
| CA-05 | Pendiente | — | — |
| CA-06 | Pendiente | — | — |
| CA-07 | Parcial | `EvaluateSourcingAsync` ahora exige el bundle persistido latest por organización/subject/version, secuencia, input/policy/result/facts/manifest digests y conjunto exacto de líneas; faltan pruebas de materialidad y sourcing manifest golden. | Subagente / pendiente de PASS |
| CA-08 | Parcial | `PolicyPersistenceServiceTests` cubre servicio real, provider instrumentado (1 llamada), replay sin provider, conflicto previo al provider, reserva scoped, espera cross-DbContext y JSON corrupto fail-closed; `ApiE2ETests` cubre `/health/policy` con `503` y `POLICY_CONFIGURATION_REQUIRED`. Falta corrupción HTTP 503 y matriz completa. | Subagente / pendiente de PASS |
| CA-09 | Parcial | Waiver valida floor publicado, límites `from/to`, binding, nonce, evidence digest y persiste snapshot del bundle reducido; faltan combinación NOT_EXCEPTIONABLE, reevaluación append-only y replay HTTP. | Subagente / pendiente de PASS |
| CA-10 | Pendiente | — | — |
| CA-11 | Pendiente | — | — |
| CA-12 | Pendiente | — | — |

## Verificaciones manuales

| Criterio | Procedimiento | Resultado | Confirmado por/fecha |
|---|---|---|---|
| — | No hay verificaciones manuales previstas; la spec exige evidencia automatizada. | — | — |

## Desviaciones y bloqueos

- La revisión independiente detectó cobertura contractual incompleta en providers/manifests, combinación cross-scope, canonicalización golden, waiver integrado, simulación real y Problem Details específicos. No se declara la SPEC completa hasta cerrar esos puntos.

## Verificación independiente

> **Resultado:** BLOCK
> **Método:** Subagente `sdd-implementation-reviewer`
> **Fecha:** 2026-09-09

- Conformidad con la spec: Parcial; la revisión de `913ae3f` confirma latest lookup/migración, pero conserva blockers de sourcing/waiver y evidencia contractual.
- Cobertura de criterios: Unitarias 31/31, IntegrationTests 8/8, ApiE2ETests 2/2; CA-08 tiene evidencia parcial, CA-01–CA-07 y CA-09–CA-12 requieren evidencia contractual adicional.
- Cambios fuera de alcance: No observados.
- Riesgos residuales: waiver reevaluado append-only/replay, sourcing manifest/materialidad, golden canonical, corrupción HTTP 503 y matriz completa API/CA.

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `specs/02-motor-de-politicas.md` | Metadato administrativo de ejecución | Flujo `/spec-impl` |
| `specs/runs/02-motor-de-politicas.md` | Registro de ejecución | Todas |
| `src/ProcureToPay.Domain/Modules/Policy/*` | Modelo, evaluación, canonicalización, waiver y rehidratación | T-01, T-02, T-07 |
| `src/ProcureToPay.Infrastructure/Persistence/Policy/*` | Providers, persistencia, replay, reserva, sourcing y snapshots | T-03, T-04, T-06, T-07 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/*` | Migraciones append-only y secuencia persistida | T-03, T-04, T-06 |
| `src/ProcureToPay.Api/*` | API, ownership y health | T-05, T-08 |
| `tests/*/Policy/*` y `tests/ProcureToPay.ApiE2ETests/UnitTest1.cs` | Evidencia de evaluación, replay, reserva, corrupción y health | T-09 |

## Cierre

- HEAD verificado: `7b77821`.
- Estrategia de integración: Pendiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Pendiente.
