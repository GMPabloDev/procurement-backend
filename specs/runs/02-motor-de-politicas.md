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
> **Actualizado:** 2026-09-09 19:35 -05
> **HEAD verificado:** `d217f3f`
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
| T-04 | Parcial | `PolicyPersistenceService`: selección serializable de activación, retiro append-only, auditoría administrativa, rowversion e idempotencia por SHA-256; reserva persistente `PolicyEvaluationReservations` con índice scoped y lease de 30 s antes del provider; integración SQL Server `PolicyPersistenceServiceTests` verifica reserva/liberación y espera concurrente desde otro DbContext. Append de evaluación conserva `evaluation_sequence` y `evaluation_result_digest`; aún falta cierre atómico de sucesor y diff/reevaluación completo. | `568f1a6` / Bloque 2 en curso |
| T-05 | Parcial | `PolicyController` expone lectura scoped de versiones/evaluaciones, drafts, publicación+activación atómica, retiro y simulación tipada no persistente sobre snapshot; mutaciones validan pertenencia de draft/activation a `actor.OrganizationId`; edición versionada completa y límites API siguen pendientes. | `45c5576` / Bloque 3 en curso |
| T-06 | Parcial | Se agregó `PolicyFactRequest`, registry exact-one local, timeout 5 s, manifest de líneas, validación de digest de política y `EvaluateEnterprisePurchaseRequest` que carga la política activa desde persistencia mediante parser canónico. Replay: lookup scoped antes del provider, reserva distribuida por lease/índice único, lock local, fingerprint y validación de integridad del JSON persistido; faltan catálogos completos y manifest de sourcing/attestation exhaustivo. | `568f1a6` / Bloque 3 en curso |
| T-07 | Parcial | `QuotationWaiverEvaluator` limita la excepción, valida floor/binding/nonce/evidence, autoridad y SoD, y `ApplyVerifiedQuotationWaiver` modifica únicamente el control `REQUIRE_QUOTATIONS` objetivo. Falta persistir snapshot/replay y verifier registry de workflow. | working-tree / Bloque 3 en curso |
| T-08 | Parcial | `PolicyConfigurationHealthCheck` y `/health/policy` distinguen ausencia/ambigüedad y validan estado publicado + digest SHA-256 del contenido cargado sin exponer reglas; faltan contratos HTTP completos de salud/corrupción. | `332ce3e` / Bloque 4 en curso |
| T-09 | Parcial | Unitarias: 31 correctas; IntegrationTests: 8 correctas con SQL Server/Testcontainers; ApiE2ETests: 2 correctas; solución compila con 0 advertencias/errores; `lens_diagnostics --mode=all --severity=error`: sin errores. Se agregó round-trip del JSON persistido y metadata de secuencia/digest. Falta ampliar evidencia negativa/golden/provider/API específica de CA-01–CA-12. | `45c5576` / Bloque 4 |

## Checkpoints

### CP-01 — 2026-09-09 10:12 -05 — Bloque 1

- Tareas: T-01, T-02.
- Cambios: modelo tipado de PolicySetVersion, reglas/predicados/efectos, fallbacks, snapshots de authority, canonicalización `policy-canonical-json/v1`, SHA-256, evaluación LINE/REQUEST/SOURCING_PO y combinación cross-scope.
- Tests y checks: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` (30 correctos); build de UnitTests correcto (0 advertencias/errores); LSP primario sin diagnósticos en dominio. El LSP de tests conserva referencias stale a tipos nuevos, pero compilación y suite ejecutada los resuelven correctamente; queda como observación de tooling, no como error de código.
- Resultado: el motor puro evalúa todas las reglas coincidentes, suma importes de líneas, conserva controles por key y no crea Approval Tasks. Persistencia, API, providers y auditoría quedan para los bloques siguientes.
- HEAD: working-tree sobre `spec-02-motor-de-politicas`.
- Próximo paso: T-03, persistencia aditiva y migración compatible con SPEC 01.

### CP-02 — 2026-09-09 19:35 -05 — Replay persistido y scope

- Cambios: replay de `evaluation_key` usa `OrganizationId` del perfil cuando está disponible y valida fingerprint antes de facts; lookup scoped por organización después de facts; serialización por workload/operación/key para evitar doble adquisición concurrente en proceso; fingerprint scoped, rehidratación del bundle persistido e integridad contra `id`, subject, input/result digest; mutaciones administrativas y simulación filtran por `actor.OrganizationId`. Los preimages de input/result/facts omiten request timestamp del facts payload, usan `subject_ref`, `combined_result`, controles explícitos y códigos enum canónicos.
- Persistencia: la primera respuesta y los replays se rehidratan desde el mismo `BundleJson`; `PolicyEvaluationBundleRecord.ResultDigest` conserva el `evaluation_result_digest` contractual; la prueba SQL Server verifica JSON, secuencia y digest.
- Tests/checks: build 0/0; UnitTests 31/31; IntegrationTests 8/8; ApiE2ETests 2/2; `git diff --check` limpio; diagnósticos blocking del turno resueltos.
- Resultado: el replay secuencial y distribuido dispone de reserva persistente previa al provider cuando existe scope confiable (el servicio ahora rechaza requests sin organización), lease de 5 minutos, snapshots separados (`inputCanonicalJson` contractual + `requestSnapshotJson`) y serialización; no se declara PASS contractual: faltan pruebas HTTP del servicio real sin provider, corrupción 503 y CA-01–CA-12.
- HEAD: `d217f3f`.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Pendiente | — | — |
| CA-02 | Pendiente | — | — |
| CA-03 | Parcial | CP-02/`PolicyCanonicalizer`: preimages explícitos para input/result, UUIDs/timestamps UTC y orden determinista de scopes/controles; falta golden vector contractual independiente. | Subagente / pendiente de PASS |
| CA-04 | Pendiente | — | — |
| CA-05 | Pendiente | — | — |
| CA-06 | Pendiente | — | — |
| CA-07 | Pendiente | — | — |
| CA-08 | Parcial | CP-02: replay/re-hidratación e idempotencia persistida verificadas en persistencia SQL Server; reserva scoped previa al provider con lease/índice único y espera cross-DbContext cubierta; faltan E2E del servicio real sin provider y respuesta HTTP 503 por corrupción. | Subagente / pendiente de PASS |
| CA-09 | Pendiente | — | — |
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

- Conformidad con la spec: Parcial; la revisión de `d0a918b` confirma mejoras de replay/canonicalización pero conserva blockers contractuales.
- Cobertura de criterios: Unitarias 31/31, IntegrationTests 8/8, ApiE2ETests 2/2; CA-08 tiene evidencia parcial, CA-01–CA-07 y CA-09–CA-12 requieren evidencia contractual adicional.
- Cambios fuera de alcance: No observados.
- Riesgos residuales: concurrencia distribuida/reserva previa al provider, prueba HTTP de health/replay, snapshots/provenance/waiver/sourcing, golden canonical, API y Problem Details.

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `specs/02-motor-de-politicas.md` | Metadato administrativo de ejecución | Flujo `/spec-impl` |
| `specs/runs/02-motor-de-politicas.md` | Registro de ejecución | Todas |

## Cierre

- HEAD verificado: `45c5576`.
- Estrategia de integración: Pendiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Pendiente.
