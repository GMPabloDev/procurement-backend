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
> **Actualizado:** 2026-09-10 05:00 -05
> **HEAD de implementación verificado:** `working-tree`
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
| T-04 | Parcial | `PolicyPersistenceService`: selección serializable de activación, retiro append-only, auditoría administrativa, rowversion e idempotencia por SHA-256; reserva persistente `PolicyEvaluationReservations` con índice scoped y lease de 5 min antes del provider; `EvaluationSequence` persistida con índice único y latest lookup para sourcing; `PolicyPersistenceServiceTests` verifica reserva/liberación, espera concurrente, provider único, replay, conflicto, sucesor atómico y retirement append-only. El diff completo para reevaluaciones distintas sigue pendiente. | `562b9d9` / Bloque 2 en curso |
| T-05 | Parcial | `PolicyController` expone lectura scoped de versiones/evaluaciones, drafts, publicación+activación atómica, retiro y simulación tipada no persistente sobre snapshot; mutaciones validan pertenencia de draft/activation a `actor.OrganizationId`; `PUT /api/v1/policies/drafts/{draftId}` permite editar drafts con digest esperado y conflicto optimista, probado por E2E; faltan límites/matriz API completa. | `ea63c22` / Bloque 3 en curso |
| T-06 | Parcial | Se agregó `PolicyFactRequest`, registry exact-one local, timeout 5 s, manifest de líneas, validación de digest de política y `EvaluateEnterprisePurchaseRequest` que carga la política activa desde persistencia mediante parser canónico. Replay: lookup scoped antes del provider, reserva distribuida por lease/índice único, lock local, fingerprint e integridad; sourcing exige workload allowlisted, key válida, snapshot policy canónico publicado y activación vigente, latest `EvaluationSequence`, result/facts/manifest digests y contenido canónico persistido; los digests de manifest/facts ahora comparten serializer canónico y el sourcing liga sus facts y líneas al `InputDigest`, además de rechazar cambio material sin nueva versión; se añadió `PolicySourcingManifest` con attestation y binding exacto de líneas; faltan catálogos completos y providers reales. | `0f7d7fe` / Bloque 3 en curso |
| T-07 | Parcial | `QuotationWaiverEvaluator` valida `PolicyDigest/EvaluationDigest`, `from/to/floor` y allowance publicado, binding/nonce/evidence, autoridad y SoD; rechaza explícitamente controles `NOT_EXCEPTIONABLE`; actualiza controles/scopes por identidad contractual y recalcula digest canónico. El servicio rehidrata y valida el bundle persistido antes de aplicar, conserva verification snapshot, calcula el `exception_verification_digest` contractual y apendea una reevaluación con `PreviousBundleId`/input digest de excepción y diff `REMOVED`; el rehidratador/validador conserva ese diff. La API expone `POST /api/v1/policies/evaluations/{id}/quotation-waiver` con ownership del originador y validación de tipo; existe adapter HTTP configurable con timeout/503 y el registry mantiene default-deny sin workflow. Falta replay HTTP y evidencia E2E contra workflow HTTP real. | `b2fb2fc` / Bloque 3 en curso |
| T-08 | Parcial | `PolicyConfigurationHealthCheck` y `/health/policy` distinguen ausencia/ambigüedad, corrupción y validan estado publicado + digest SHA-256 del contenido cargado sin exponer reglas; `ApiE2ETests` verifica por HTTP `503` + `POLICY_CONFIGURATION_REQUIRED` y `POLICY_CONFIGURATION_CORRUPT`; evaluación empresarial rechaza payloads de más de 500 líneas con `413`; el adapter de workflow traduce red/5xx/malformed evidence a `PolicyDependencyUnavailableException`; faltan E2E de indisponibilidad y exportación completa de telemetría. | `b2fb2fc` / Bloque 4 en curso |
| T-09 | Parcial | Unitarias: 43 correctas; IntegrationTests: 10 correctas con SQL Server/Testcontainers, incluyendo provider de facts, publicación sucesora atómica, default-deny, workflow controlado y vectores golden; ApiE2ETests: 2 correctas (incluye health required/corrupt, edición optimista, validación de waiver y ciclo draft/publicación/simulación); `dotnet test --solution ProcureToPay.sln --no-restore`: **53/53** correctas en el último commit verificado; solución compila con 0 advertencias/errores; métricas/logs básicos y adapter HTTP configurable añadidos. Falta ampliar evidencia negativa/golden/API específica de CA-01–CA-12. | working-tree / Bloque 4 |

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
- HEAD de implementación: `ad97c4f`.

### CP-03 — 2026-09-10 00:06 - Canonicalización contractual y facts digest

- Cambios: `policy-canonical-json/v1` ahora ordena sets por bytes UTF-8 canónicos, normaliza strings a NFC, rechaza duplicados de `PolicyValue.Set`, y aplica representación canónica completa a rules, predicates, effects, scopes y controls. Se agregó canonicalización tipada de `exception_verification_digest` y su uso productivo durante reevaluaciones de waiver.
- Persistencia/providers: el digest de manifest y facts usa la misma representación `SortedDictionary` + serializer canónico en producción y provider controlado; el digest de excepción ya se incluye en `exception_verification_digests` del input de reevaluación.
- Tests/checks: UnitTests 38/38; golden de policy/input/result/exception; IntegrationTests 1/1 para facts manifest/bundle y 1/1 para replay SQL Server; `dotnet build ProcureToPay.sln --no-restore` 0/0; `git diff --check` limpio.
- Resultado: se cerró la divergencia de digest que impedía el provider controlado y quedó evidencia independiente para los preimages principales. CA-03 sigue parcial por faltar evidencia HTTP de corrupción/indisponibilidad y matriz completa.
- HEAD de implementación: `57f63fe`.

### CP-04 — 2026-09-10 00:20 - Binding de facts de SOURCING_PO

- Cambios: `EvaluateSourcing` ahora calcula `ManifestDigest` y `FactsDigest` propios a partir de sujeto, líneas cubiertas y facts de sourcing; esos digests alimentan el input canónico y se conservan en el bundle. `EvaluateSourcingAsync` rechaza un segundo bundle del mismo sourcing id/version cuando cambia el input digest, obligando a publicar una nueva versión del sujeto.
- Tests/checks: UnitTests 38/38; prueba dirigida de persistencia SQL Server 1/1; build y LSP primario sin diagnósticos. La prueba unitaria confirma que cambiar facts de sourcing cambia FactsDigest/InputDigest sin cambiar el manifest de líneas.
- Resultado: se cerró la omisión por la que facts materiales de sourcing podían cambiar sin quedar ligados al digest; permanecen pendientes providers/manifests reales y cobertura completa de cambios materiales.

### CP-05 — 2026-09-10 00:50 - Ciclo API, sucesor atómico y default-deny

- Cambios: el verifier registry retorna un adapter default-deny cuando no hay workflow, evitando convertir la ausencia esperada del workflow en `503`. Se agregaron pruebas HTTP de corrupción de configuración (`503`), creación de draft, publicación, health y simulación no persistente. La prueba SQL cubre publicación de sucesor y retiro append-only exactamente en `effective_from`.
- Tests/checks: UnitTests 38/38; IntegrationTests 10/10; ApiE2ETests 2/2; build 0/0; `specctl check 02 --approval` válido; LSP primario limpio.
- Resultado: queda cubierta evidencia HTTP de corrupción y lifecycle administrativo básico; el run sigue BLOCK por materialidad/manifest de sourcing completa, waiver integrado y matriz contractual restante.
- HEAD de implementación: `748771d`.

### CP-06 — 2026-09-10 01:15 - Diff de reevaluación de waiver

- Cambios: `PolicyEvaluationBundle` conserva entradas de diff tipadas; un quotation waiver verificado registra `REMOVED`, control objetivo, líneas y mínimos anterior/actual, y el `evaluation_result_digest` usa ese diff canónico. La reevaluación persistida conserva la historia y el diff al rehidratarse.
- Tests/checks: solución completa con `dotnet test --solution ProcureToPay.sln --no-restore`: 50/50; UnitTests 38/38, incluyendo aserciones del diff `3 → 2`; IntegrationTests 10/10; ApiE2ETests 2/2; build 0/0; LSP primario limpio.
- Resultado: se cerró la pérdida de evidencia del cambio en reevaluaciones de waiver; falta generalizar `ADDED/HARDENED/UNCHANGED` para cambios materiales y cubrirlo en integración/HTTP.

### CP-07 — 2026-09-10 02:00 - Integridad de reevaluaciones persistidas y límites

- Cambios: `PolicyEvaluationBundleRehydrator` rehidrata el diff tipado; la validación de bundles persistidos recalcula el result digest usando el diff cuando existe y mantiene compatibilidad con bundles históricos sin diff. Se valida el binding de waiver contra organización, policy, sujeto y digests persistidos. Providers empresariales con más de 500 líneas reciben `413` mediante Problem Details.
- Tests/checks: `dotnet test --solution ProcureToPay.sln --no-restore`: 51/51; UnitTests 39/39; IntegrationTests 10/10; ApiE2ETests 2/2; build 0/0. El fix elimina el fallo observado de replay persistido.
- Resultado: corregido el blocker real que hacía fallar cualquier reevaluación de waiver al rehidratarse; aún faltan tipos versionados de facts/sourcing, matriz completa de excepciones, telemetría y evidencia contractual restante.
- HEAD de implementación: `1e7700d`.

### CP-08 — 2026-09-10 02:20 - Edición versionada de drafts

- Cambios: se añadió `UpdateDraftAsync` con transacción serializable, ownership organizacional, estado `DRAFT`, digest esperado como control optimista y auditoría `POLICY_DRAFT_UPDATED`; la API expone `PUT /api/v1/policies/drafts/{draftId}`.
- Tests/checks: build 0/0; `ApiE2ETests` 2/2, incluyendo actualización válida y rechazo `409` de digest stale.
- Resultado: se cerró la ausencia de edición básica de drafts; todavía faltan referencias versionadas, edición tras publicación prohibida con matriz HTTP completa y cobertura contractual restante.
- HEAD de implementación: `ea63c22`.

### CP-09 — 2026-09-10 02:35 - Observabilidad operativa básica

- Cambios: `PolicyEvaluationService` publica métricas `evaluations_total`, `replays_total`, `provider_failures_total` y duración, etiquetadas solo por operación, tipo de sujeto y resultado; conserva logs sin facts sensibles.
- Tests/checks: build de solución 0/0; las suites anteriores permanecen en 51/51; no se expone contenido de políticas ni datos personales en las etiquetas.
- Resultado: se cubre la instrumentación mínima del flujo principal; faltan exportador OpenTelemetry, dashboards/alertas y validación operacional del despliegue.
- HEAD de implementación: `bb582bf`.

### CP-10 — 2026-09-10 02:50 - Referencias versionadas tipadas

- Cambios: se añadieron `VersionedCodeRef`, `VersionedEntityRef` y `TypedAnswerRef` con validación de versión, digest SHA-256, identidad estable y tipo de respuesta. Esto establece el contrato de dominio para facts versionados sin convertirlos en strings libres.
- Tests/checks: `dotnet test --solution ProcureToPay.sln --no-restore`: 52/52; UnitTests 40/40; IntegrationTests 10/10; ApiE2ETests 2/2; build de solución 0/0.
- Resultado: cerrado el modelo explícito de referencias; falta proyectarlo en los payloads reales de providers/catalogs, canonicalización de estos campos y attestation completa de sourcing.
- HEAD de implementación: `0666aa1`.

### CP-11 — 2026-09-10 03:10 - Attestation de sourcing

- Cambios: `PolicySourcingManifest` representa provider, versión de contrato, digest de attestation y líneas cubiertas; `PolicySourcingInput` lo incorpora opcionalmente, lo liga al manifest digest canónico y `EvaluateSourcingAsync` rechaza una attestation cuya línea cubierta no coincide exactamente.
- Tests/checks: `dotnet test --solution ProcureToPay.sln --no-restore`: 53/53; UnitTests 41/41; IntegrationTests 10/10; ApiE2ETests 2/2; build de solución 0/0.
- Resultado: el contrato de attestation ya existe y está validado en dominio; falta conectarlo con un provider real, exigirlo en el flujo empresarial final y persistir/validar su preimage completo.
- HEAD de implementación: `0f7d7fe`.

### CP-12 — 2026-09-10 03:45 - Endpoint de waiver

- Cambios: se añadió `POST /api/v1/policies/evaluations/{evaluationId}/quotation-waiver`; carga el bundle scoped, exige usuario activo como originador, traduce el tipo cerrado y delega la verificación/persistencia al servicio. E2E cubre rechazo de tipo inválido con Problem Details.
- Tests/checks: build 0/0; `ApiE2ETests` 2/2; suites anteriores 53/53.
- Resultado: la superficie HTTP existe con ownership y default-deny; falta conectar un workflow/verifier real y probar una reevaluación válida end-to-end.
- HEAD de implementación: `2f2d298`.

### CP-13 — 2026-09-10 04:20 - Waiver integrado y NOT_EXCEPTIONABLE

- Cambios: `PolicyPersistenceServiceTests` ejecuta una reevaluación válida sobre SQL Server con verifier controlado, verifica `exception_verification` persistido, `PreviousBundleId`, diff y reducción `3 → 2`; el dominio rechaza explícitamente quotation controls sin allowance como `NOT_EXCEPTIONABLE`.
- Tests/checks: UnitTests 41/41; IntegrationTests 10/10; build de solución 0/0; la prueba integrada evita provider duplicado y mantiene el snapshot append-only.
- Resultado: se cerró la evidencia integrada del camino válido y la invariante NOT_EXCEPTIONABLE; queda conectar el workflow real, revocación y replay HTTP.
- HEAD de implementación: `b2fb2fc`.

### CP-14 — 2026-09-10 05:00 - Workflow verifier HTTP configurable

- Cambios: se añadió `HttpQuotationWaiverVerifier`, que llama al workflow configurado, distingue rechazo de negocio (`404/409/422`) de indisponibilidad (`network/5xx/malformed`), valida digest de decisión, evidencia de autoridad, versión y `segregation_satisfied`, y traduce fallos a dependencia tipada `503`. Se añadió `Microsoft.Extensions.Http` y cobertura unitaria del adapter.
- Tests/checks: build de solución 0/0; UnitTests 43/43; IntegrationTests 10/10; el adapter usa timeout de 5 segundos y respeta cancelación.
- Resultado: eliminado el default deny como única implementación cuando existe `Policy:ExceptionWorkflow:BaseUrl`; sin URL configurada se conserva fail-closed. Falta E2E contra un workflow HTTP real y replay HTTP.
- HEAD de implementación: pendiente de commit.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Parcial | La integración cubre publicación de sucesor, cierre atómico en `effective_from`, retirement append-only y E2E cubre draft/publicación; faltan carreras HTTP, referencias de catálogo y rollback completo. | Subagente / pendiente de PASS |
| CA-02 | Parcial | Modelo tipado, operadores/efectos cerrados, límites y simulación directa no persistente están cubiertos por unitarias/E2E; falta matriz exhaustiva de referencias y límites HTTP. | Subagente / pendiente de PASS |
| CA-03 | Parcial | CP-03 agrega golden vectors exactos e independientes para policy, input, result, exception verification y facts manifest/bundle; se validan bytes UTF-8, NFC, ordenación completa y digest productivo de excepción. Falta evidencia HTTP de corrupción/indisponibilidad y matriz completa del criterio. | Subagente / pendiente de PASS |
| CA-04 | Pendiente | — | — |
| CA-05 | Pendiente | — | — |
| CA-06 | Pendiente | — | — |
| CA-07 | Parcial | `EvaluateSourcingAsync` exige el bundle persistido latest por organización/subject/version, secuencia, input/policy/result/facts/manifest digests y conjunto exacto de líneas; `EvaluateSourcing` liga facts/líneas propios al input y el servicio rechaza cambio de input para el mismo sujeto/version. Faltan providers/manifests contractuales y cobertura de todos los cambios materiales. | Subagente / pendiente de PASS |
| CA-08 | Parcial | `PolicyPersistenceServiceTests` cubre servicio real, provider instrumentado (1 llamada), replay sin provider, conflicto previo al provider, reserva scoped, espera cross-DbContext y JSON corrupto fail-closed; `ApiE2ETests` cubre por HTTP `503` tanto `POLICY_CONFIGURATION_REQUIRED` como `POLICY_CONFIGURATION_CORRUPT`, además de lifecycle API. Falta matriz completa de dependencia/idempotencia. | Subagente / pendiente de PASS |
| CA-09 | Parcial | Waiver valida digests de policy/evaluación, floor/allowance publicado, límites `from/to`, binding, nonce, evidence digest, actualiza scopes por identidad contractual, rechaza `NOT_EXCEPTIONABLE`, persiste snapshot y diff `REMOVED`; `PolicyPersistenceServiceTests` prueba workflow controlado, rehidratación, digest y reevaluación persistida; registry default-deny evita bypass sin workflow; API expone aplicación scoped y rechaza tipo inválido por HTTP. Faltan verifier real, revocación/expiración HTTP y replay HTTP. | Subagente / pendiente de PASS |
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

- HEAD de implementación: `1e7700d`.
- Estrategia de integración: Pendiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Pendiente.
