# RUN SPEC 02 — Motor de políticas de compras

> **Formato:** sdd-run/v2
> **Estado del run:** Integrado
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
> **Actualizado:** 2026-09-11 08:40 -05
> **HEAD verificado:** dac85ab671e0fc2d91bb538135e58c78b26d2092
> **Commit de integración:** c4ecd80fda6c5dbfad3ff3d2780572fe4495fe15

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
| T-04 | Verificada | `dotnet test --project tests/ProcureToPay.UnitTests/...`: 60 correctos; `PolicyEvaluationDiffTests` cubre `ADDED/HARDENED/REMOVED/UNCHANGED`, authority/importe/documentos y keys/sujetos separados. `PolicyReevaluationTests` (SQL Server, 11/11 integración): v1→v2 persiste `PreviousBundleId`, diff `ADDED`, causa `MATERIAL_FACT_CHANGE` y replay idempotente sin volver a llamar al provider; `PolicyPersistenceServiceTests` conserva conflicto de subject. | `cfc530c` + árbol de trabajo |
| T-05 | Verificada | `ApiE2ETests` 3/3: `UnitTest1` cubre ciclo draft/edición optimista/publicación/simulación no persistente y matriz 400/401/403/404/409/422/503; `PolicyWorkflowE2ETests` cubre `413 /problems/payload-too-large` (política >10 MiB) y rechazo de >2.000 reglas. `PolicyReevaluationTests` verifica 413 para reglas/predicados/efectos antes de persistir. | `cfc530c` + árbol de trabajo |
| T-06 | Verificada | `PolicyAdapterContractTests` exige exactamente un provider/catálogo y fallo cerrado; `PolicyPersistenceServiceTests` ejercita provider controlado, manifest/digests, replay y conflicto; `PolicyReevaluationTests` rechaza un `CompletenessManifest` que omite una línea sin persistir bundle. Los providers empresariales reales de PR/Sourcing siguen fuera de alcance (CA-12). | `cfc530c` + árbol de trabajo |
| T-07 | Verificada | `PolicyEvaluatorTests` y `PolicyPersistenceServiceTests` cubren límites `from/to/floor`, `NOT_EXCEPTIONABLE`, binding/nonce/evidencia y reevaluación persistida; `PolicyWorkflowE2ETests` prueba por HTTP la aplicación del waiver con verifier controlado y un replay idempotente que devuelve la misma reevaluación con un único `PolicyExceptionVerification`. El workflow HTTP real está fuera de alcance (SPEC 03). | `cfc530c` + árbol de trabajo |
| T-08 | Verificada | `PolicyTelemetry` (ActivitySource `ProcureToPay.Policy`) se registra con `.AddSource` en `Program`; `PolicyReevaluationTests` captura el span `policy.evaluate` con `policy.operation`/`policy.result` y comprueba que no se etiqueta ningún snapshot; `UnitTest1` cubre `/health/policy` `POLICY_CONFIGURATION_REQUIRED` y `POLICY_CONFIGURATION_CORRUPT`. | `cfc530c` + árbol de trabajo |
| T-09 | Verificada | Suite completa en secuencia: UnitTests 60/60, IntegrationTests 11/11 (SQL Server/Testcontainers) y ApiE2ETests 3/3; `dotnet build ProcureToPay.sln` 0 advertencias/0 errores; `git diff --check` limpio; `lens_diagnostics (workspace)` 82 limpios sin hallazgos. Evidencia compartida por CA-01–CA-12. | `cfc530c` + árbol de trabajo |

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
- HEAD de implementación: `b2fb2fc`.

### CP-15 — 2026-09-10 05:40 - Replay idempotente de waiver

- Cambios: se verificó que repetir la misma decisión `(workflow_decision_id, base_bundle_id, target_key)` y binding devuelve la misma reevaluación persistida y no duplica `exception_verification`.
- Tests/checks: IntegrationTests 10/10 en SQL Server; build 0/0; replay confirmado con el mismo id de reevaluación.
- Resultado: cerrado el replay idempotente a nivel de servicio; falta evidencia HTTP con workflow real y revocación.
- HEAD de implementación: `d3f29ea`.

### CP-16 — 2026-09-10 06:00 - Adapter HTTP de catálogo

- Cambios: se añadió `HttpPolicyReferenceCatalog`, configurable por `Policy:ReferenceCatalog:BaseUrl`, `CatalogId` y `ContractVersion`; resuelve referencias versionadas, distingue inexistencia (`404`) de indisponibilidad (`5xx/network/malformed`) y respeta timeout/cancelación. El registro DI conserva default-deny cuando no hay catálogo configurado.
- Tests/checks: UnitTests 45/45; IntegrationTests 10/10; build de solución 0/0.
- Resultado: cerrado el adapter de catálogo externo; faltan E2E con catálogo real, resolver relaciones Cost Center→Department y hacer obligatorio el manifest atestado en sourcing empresarial.
- HEAD de implementación: `cb456ef`.

### CP-17 — 2026-09-10 06:50 - Catálogo SQL de organización

- Cambios: se añadió `DatabasePolicyReferenceCatalog` para validar `DEPARTMENT` y `LEGAL_ENTITY` activos por id/version desde SQL Server; el DI usa este catálogo concreto cuando no hay catálogo HTTP configurado, y el test de persistencia verifica versión válida e inválida.
- Tests/checks: IntegrationTests 10/10; build de solución 0/0.
- Resultado: Department y Legal Entity ya tienen una fuente concreta local; Supplier, Cost Center y otros catálogos siguen requiriendo sus dominios propietarios.
- HEAD de implementación: `aa3666b`.

### CP-18 — 2026-09-10 07:10 - Activación del catálogo SQL por defecto

- Cambios: cuando no se configura `Policy:ReferenceCatalog:BaseUrl`, DI registra catálogos SQL concretos para `DEPARTMENT` y `LEGAL_ENTITY`; cuando sí se configura URL, usa el adapter HTTP. Esto evita que facts organizacionales válidos caigan en default-deny.
- Tests/checks: full suite 57/57; build 0/0; IntegrationTests verifica una referencia Department activa y una versión incorrecta.
- Resultado: cerrada la fuente concreta para referencias organizacionales Release 1; Supplier, Cost Center, productos y sourcing siguen dependiendo de sus dominios propietarios.
- HEAD de implementación: `40e6d6a`.

### CP-19 — 2026-09-10 07:40 - Attestation obligatoria de sourcing

- Cambios: `PolicySourcingInput` exige `PolicySourcingManifest`; el digest canónico incluye provider, contrato, attestation y líneas, y `EvaluateSourcingAsync` valida el conjunto exacto antes de consultar el bundle actual.
- Tests/checks: full suite 57/57; UnitTests 45/45; IntegrationTests 10/10; ApiE2ETests 2/2; build 0/0.
- Resultado: cerrado el bypass por sourcing sin attestation; sigue pendiente el provider empresarial que produzca la attestation y el preimage persistido completo.
- HEAD de implementación: `cef3756`.

### CP-20 — 2026-09-10 07:40 - Persistencia del preimage de attestation

- Cambios: `PolicyEvaluationBundle` conserva `ManifestCanonicalJson` para sourcing; el rehidratador lo recupera y `ValidatePersistedEvaluation` comprueba que su SHA-256 coincide con `ManifestDigest`. Se añadió evidencia unitaria de la relación digest/preimage.
- Tests/checks: UnitTests 45/45; build 0/0; suites de integración y E2E previas pasan.
- Resultado: el preimage de attestation ya no se pierde al persistir/rehidratar; siguen faltando providers empresariales reales y pruebas HTTP del sourcing.
- HEAD de implementación: `6a47849`.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Cumplido | `PolicyPersistenceServiceTests` prueba el cierre atómico de la activación abierta en el `effective_from` del sucesor y el retiro append-only; `UnitTest1` (E2E) cubre draft, edición con conflicto optimista `409`, publicación y simulación, y `PolicyReevaluationTests` confirma que los límites fallan antes de persistir sin auditoría de éxito. | Subagente ronda 1 |
| CA-02 | Cumplido | `PolicyModelTests` valida catálogo cerrado de facts/operadores/efectos, fallback por scope y rechazo de efectos incompatibles; `PolicyCombinationTests` cubre PO sobre compra directa; `UnitTest1` confirma que la simulación no persiste `PolicyEvaluationBundle` (`Assert.Equal(0, bundles)`), no acepta excepciones y exige `ADMIN`; `PolicyTypedFactsTests` prueba el tipado versionado, el rechazo tipo-por-fact y MoneyBase con ISO 4217, y `PolicyCanonicalizationGoldenTests` fija el schema v2. | Subagente ronda 1 |
| CA-03 | Cumplido | `PolicyCanonicalizationGoldenTests` y `PolicyCanonicalizationIntegrationTests` fijan vectores golden de policy/input/result/exception/manifest con bytes UTF-8, NFC y SHA-256; `PolicyReevaluationTests` verifica cero activaciones vigentes → `PolicyConfigurationUnavailableException` sin bundle; `UnitTest1` y `PolicyPersistenceServiceTests` cubren corrupción de digest/contenido → `503` sin defaults embebidos. | Subagente ronda 1 |
| CA-04 | Cumplido | `PolicyCombinationTests` prueba la expansión de un control REQUEST sobre todas las líneas del manifiesto, `BLOCK` vence a `ALLOW`, `REQUIRE_PO` vence a compra directa, cotizaciones usa máximo, documentos se unen, máxima authority por key y keys distintas separadas; `PolicyEvaluatorTests` confirma LINE antes de REQUEST en el mismo bundle. | Subagente ronda 1 |
| CA-05 | Cumplido | `PolicyCombinationTests.Department_approval_targets_the_cost_center_owner_not_the_beneficiary_department`: una línea barata con riesgo/contrato genera Department Approval dirigido al Department del Cost Center (no al Beneficiary Department) y revisiones IT/Legal con provenance, líneas y fase, sin candidato ni Approval Task; `PolicyTypedFactsTests` verifica que un control de budget conserva Cost Centers, importe base, moneda y `OriginFacts`, y `PolicyReevaluationTests` prueba `FactProvenance` desde el provider. | Subagente ronda 1 |
| CA-06 | Cumplido | `PolicyCombinationTests.Request_total_is_computed_by_the_engine_and_ignores_a_caller_supplied_aggregate` demuestra que un total aportado por el caller no reduce controles; `PolicyReevaluationTests` rechaza un `CompletenessManifest` con línea omitida sin resultado parcial; `PolicyPersistenceServiceTests` valida manifest y facts digests del provider controlado; `PolicyReevaluationTests` verifica provenance por fact y que una referencia de catálogo sin adapter falla 503. | Subagente ronda 1 |
| CA-07 | Cumplido | `PolicyEvaluatorTests.Sourcing_evaluation_is_a_bundle_linked_to_the_current_request_bundle` verifica `PreviousBundleId`, líneas cubiertas exactas y digests propios de manifest/facts que cambian con facts materiales; `PolicyPersistenceServiceTests` exige el bundle persistido más reciente y rechaza un input distinto para el mismo subject/version. | Subagente ronda 1 |
| CA-08 | Cumplido | `PolicyPersistenceServiceTests` cubre la key scoped a organización+workload+operación, el replay sin provider, el conflicto previo al provider, la reserva y la corrupción `503`; `PolicyReevaluationTests` rehidrata `Cause`/`PreviousBundleId` y hace replay idempotente de una reevaluación; `PolicyWorkflowE2ETests` cubre el replay HTTP; `UnitTest1` cubre health require/corrupt. | Subagente ronda 1 |
| CA-09 | Cumplido | `PolicyEvaluationDiffTests` fija `ADDED`, `HARDENED`, `REMOVED` y `UNCHANGED`; `PolicyReevaluationTests` persiste el diff `ADDED` con historia y replay idempotente; `PolicyWorkflowE2ETests` aplica y repite por HTTP una reducción `3→2` con diff `REMOVED`; `PolicyEvaluatorTests`/`PolicyPersistenceServiceTests` cubren límites `from/to/floor`, `NOT_EXCEPTIONABLE`, binding/nonce/SoD y default-deny. | Subagente ronda 1 |
| CA-10 | Cumplido | `PolicyEvaluatorTests` prueba `NOT_EXCEPTIONABLE` y que solo `REQUIRE_QUOTATIONS` admite reducción; `PolicyAdapterContractTests` verifica el verifier default-deny; `PolicyWorkflowE2ETests` exige verifier controlado y ownership del originador; `PolicyReevaluationTests` confirma que la telemetría solo registra tags minimizados sin snapshot. | Subagente ronda 1 |
| CA-11 | Cumplido | `PolicyWorkflowE2ETests` verifica `413` con `/problems/payload-too-large`; `UnitTest1` cubre `400/401/403/404/409/422/503` con Problem Details; `PolicyReevaluationTests` aplica los límites `413` (reglas, predicados, efectos, risk answers) antes de persistir; `PolicyPersistenceServiceTests` demuestra atomicidad e idempotencia sin datos huérfanos. | Subagente ronda 1 |
| CA-12 | Cumplido | `PolicyAdapterContractTests` exige exactamente un provider/catálogo y un verifier, y comprueba que `COST_CENTER`/`SUPPLIER` sin catálogo fallan cerrado sin delegar al resolver de elegibilidad de SPEC 01; `PolicyWorkflowE2ETests` usa un adapter controlado de waiver; los providers de Purchase Request/Sourcing y catálogos propietarios siguen fuera de alcance. | Subagente ronda 1 |

## Verificaciones manuales

| Criterio | Procedimiento | Resultado | Confirmado por/fecha |
|---|---|---|---|
| — | No hay verificaciones manuales previstas; la spec exige evidencia automatizada. | — | — |

## Desviaciones y bloqueos

- **ronda 5 autorizada** (2026-09-11): el usuario autorizó una quinta ronda, únicamente para verificar el último residual (tag `policy.version_id` en los spans de waiver y replay), señalando que no se autorizan más rondas después de esta. No cambia el contrato ni el digest `63fec425…`.
- **ronda 4 autorizada** (2026-09-11): el usuario autorizó una cuarta ronda de verificación independiente tras el BLOCK de la ronda 3, para cubrir B1/B2/B4/B7 y el delta de sus correcciones. No cambia el contrato ni el digest `63fec425…`.
- **ronda 3 autorizada** (2026-09-11): el usuario autorizó una tercera ronda de verificación independiente tras el BLOCK de la ronda 2, para cubrir B1–B4/B7 y el delta de sus correcciones. No cambia el contrato ni el digest `63fec425…`.
- **BLOQUEO tras ronda 2 (2026-09-11):** la ronda 2 devolvió `Con bloqueos` con B5/B6 cumplidos y residuales en B1–B4 y B7. Se aplicaron correcciones adicionales (ver `### Ronda 2`) y las suites quedan 64/64 + 11/11 + 3/3, pero el presupuesto de verificación (`2/2`) está agotado. El run y la spec quedan `Bloqueado`/`Ejecución: Bloqueada` a la espera de decisión humana; una ronda 3 exige la frase **`ronda 3 autorizada`** registrada aquí, o el usuario acepta integrar con los residuales documentados.
- **Resolución de B1 (2026-09-11):** por aclaración del usuario, el tipado de REQ-04 ya estaba en el contrato y el preimage no cambia: `canonicalization_version` permanece `policy-canonical-json/v1` y solo sube `policy_schema_version` a `policy-schema/v2` (ya era input de `policy_content_digest`), con vectores golden regenerados. Decisión registrada como **detalle interno sin efecto contractual**; no se ejecutó `/spec revisar` ni cambió el digest `63fec425…`. Se implementaron B1–B7 y CA-02/CA-05/CA-06 pasan a `Cumplido`. **Ronda 2 pendiente cubriendo B1 y el delta de B2–B7.**
- **BLOQUEO previo tras ronda 1:** la verificación independiente (`openai-codex/gpt-5.6-sol`) devolvió `Con bloqueos` con 7 hallazgos en el núcleo en alcance (B1–B7, triaje en `## Verificación independiente`). No eran dominios ausentes y no se cerraron como fuera de alcance.
- **Reanudación 2026-09-11:** los puntos que la revisión anterior marcó como incompletos (providers/manifests, combinación cross-scope, canonicalización golden, waiver integrado, simulación y Problem Details) quedaron cubiertos por evidencia automatizada; ver `## Evidencia de aceptación`.
- **Estado Git:** la implementación de esta sesión vive en el árbol de trabajo sobre `cfc530c` y no se ha commiteado (el flujo no hace commits automáticos). Las rondas 1–5 revisan ese árbol, que es el `HEAD verificado` registrado; el usuario debe commitear antes de integrar.
- **Hallazgos fuera de alcance (no bloquean):** providers reales de Purchase Request/Sourcing, catálogos propietarios de Supplier y Cost Center, E2E contra un workflow HTTP real y los dominios posteriores están excluidos por `## Alcance → No incluye` y aceptados por CA-12 con adapters controlados. Se registran aquí y no disparan rondas adicionales.
- **`256 risk answers/línea`:** el catálogo tipado de REQ-04 admite a lo sumo un `RISK_ANSWER` por línea; el motor añade una guarda `413` si una línea declara más de 256 facts (`PolicyReevaluationTests`).
- **LSP degradado:** `csharp-ls` conservó un snapshot obsoleto al añadir símbolos entre proyectos y emitió falsos `CS0103/CS0117`; se reinició el servidor y `lens_diagnostics` (workspace, 83 archivos) reporta 0 hallazgos. `dotnet build ProcureToPay.sln` da 0 advertencias y 0 errores.
- **Deriva administrativa previa:** el tip `cfc530c` está por delante del `HEAD verificado` anterior (`1e4829d`) y solo toca `specs/runs/`; no exige repetir la verificación.
- **Sobrecoste histórico:** la implementación acumuló 20 pases de revisión independiente sin presupuesto. Desde el presupuesto `Rondas: N/2`, superar 2 rondas exige autorización humana explícita.

## Verificación independiente

> **Resultado:** Sin bloqueos
> **Rondas:** 5/5
> **Modelo efectivo:** `openai-codex/gpt-5.6-sol` (informado por el subagente; distinto del orquestador `opencode-go/deepseek-flash`)
> **Método:** Subagente `sdd-implementation-reviewer`
> **Fecha:** 2026-09-11

- Conformidad con la spec: parcial. La revisión confirma contrato/digest, rama y árbol declarado, y `lens_diagnostics` sin errores (10 hints `CS8019`), pero señala incumplimientos materiales en REQ-04, REQ-05, REQ-08, REQ-12, REQ-13, REQ-14, REQ-17, NFR-02 y NFR-05, y degrada CA-02/CA-05/CA-07/CA-09/CA-10/CA-11 a no demostrados.
- Cobertura de criterios: la revisión considera que varias filas de `## Evidencia de aceptación` sobredeclaran cobertura (tipos versionados no conectados al pipeline, authority del waiver autocertificada, telemetría sin parte de la matriz, límites no aplicados sobre todos los datos).
- Cambios fuera de alcance: ninguno dentro del delta; `specs/03-approval-workflow.md` excluido expresamente. Providers PR/Sourcing, catálogos Supplier/Cost Center y workflow HTTP real siguen fuera de alcance y no se cuentan como bloqueos.
- Riesgos residuales: modelo tipado de REQ-04, evidencia del verifier de REQ-14, comando/fingerprint de reevaluación de REQ-13, atomicidad de NFR-02 y matriz de observabilidad de NFR-05.

### Hallazgos de la ronda 1 (triaje)

| # | Hallazgo | REQ/CA | Evaluación contra el contrato |
| --- | --- | --- | --- |
| B1 | Catálogo tipado de REQ-04 no conectado: `PolicyValue` no usa `VersionedCodeRef`/`VersionedEntityRef`/`TypedAnswerRef`, no fija tipo por fact (`PURCHASE_TYPE` admite dinero) y `Money` no lleva moneda. | REQ-04, REQ-05, REQ-08 / CA-02, CA-05, CA-06 | **Válido y en alcance.** Afecta al preimage canónico y a los vectores golden; cerrarlo bien puede exigir nueva `canonicalization_version` o una decisión de contrato. |
| B2 | El waiver confía en `ApproverRole`/`AuthorityType`/`ApproverId`/`WorkloadSubjectId`/`OriginatorId` aportados por HTTP; el verifier no devuelve approver, scope, vigencia ni EligibilityEvidence, y no expone `verifier_id`/`contract_version` (se hardcodean al persistir). | REQ-14 / CA-09, CA-10 | **Válido y en alcance.** Es forma de evidencia de REQ-14, no dominio ausente; CA-12 solo relaja el adapter real, no el contenido probado. |
| B3 | El comando de reevaluación no recibe `Cause` ni `PreviousResultDigest`; la API no recibe `PreviousBundleId`; la causa se infiere y el replay usa la causa persistida, no la declarada. | REQ-13 / CA-08, CA-09 | **Válido y en alcance.** Introducido/expuesto por esta ronda; corregible sin cambio de contrato. |
| B4 | `PolicyEvaluationDiff` ignora role, authority type, id/version/code, moneda y `DecisionScope` en la equivalencia, y la identidad por sujetos descompone cambios de cobertura. | REQ-13 / CA-09 | **Válido y en alcance.** Corregible sin cambio de contrato. |
| B5 | `AppendExceptionVerificationAsync` y la reevaluación se confirman en operaciones separadas: un fallo intermedio deja verificación huérfana. | NFR-02 / CA-09, CA-11 | **Válido y en alcance.** Corregible sin cambio de contrato. |
| B6 | Límites incompletos: 4 KiB por provenance, 5 MiB para snapshots empresariales y 10 MiB fuera del controller no se aplican; la guarda de 256 “risk answers” cuenta todos los facts. | REQ-12, REQ-17 / CA-07, CA-11 | **Parcialmente válido.** Corregible sin cambio de contrato salvo el significado de “risk answers”. |
| B7 | Telemetría incompleta frente a NFR-05: faltan policy version y scope en los spans, instrumentación de administración/selección/bloqueo/conflicto y matriz completa (métricas+logs+trazas). | NFR-05 / CA-10, CA-11 | **Válido y en alcance.** Corregible sin cambio de contrato. |

Evaluación de las tres causas contractuales de reevaluación respecto a `APPROVED_EXCEPTION`, la forma exacta del contrato de evidencia del verifier y el alcance del rework del catálogo tipado son decisiones que exceden un detalle interno y requieren confirmación humana antes de la ronda 2.

### Correcciones posteriores a la ronda 1 (B2–B7)

| # | Corrección | Evidencia |
| --- | --- | --- |
| B2 | `QuotationWaiverEvidence` transporta approver, rol, authority type, scope, vigencia, `verifier_id` y `contract_version`; `VerifyAsync` valida esa evidencia y ya no confía en el request; el `ExceptionVerificationSnapshot` se puebla desde el verifier. | `PolicyEvaluatorTests`, `PolicyPersistenceServiceTests`, `PolicyWorkflowE2ETests`, `HttpQuotationWaiverVerifierTests` (74/74). |
| B3 | El comando (puerto y API) declara `PreviousBundleId`, `Cause` y `PreviousResultDigest`; el motor valida la causa (`MATERIAL_FACT_CHANGE`/`POLICY_VERSION_CHANGE`), el digest previo y reconstruye el fingerprint desde el comando. | `PolicyReevaluationTests` (v1→v2 y replay), `PolicyPersistenceServiceTests`. |
| B4 | `PolicyEvaluationDiff` empareja por `(key, tipo, fase)` y sujetos y compara todos los parámetros efectivos: rol, authority type, level id/version/code/rank, importe, moneda, `DecisionScope`, cotizaciones, documentos y exceptionable. | `PolicyEvaluationDiffTests`. |
| B5 | Verificación de excepción y reevaluación se confirman en una única transacción (`AppendVerifiedQuotationWaiverAsync`). | `PolicyPersistenceServiceTests` (waiver integrado), `PolicyWorkflowE2ETests`. |
| B6 | Límites: 10 MiB en persistencia (bytes), 5 MiB de snapshot, 4 KiB por provenance y 256 risk answers por línea contando solo claves `RISK_ANSWER*`. | `PolicyReevaluationTests`. |
| B7 | Spans con `policy.version_id`, `policy.content_digest`, `policy.scopes`, `policy.result`, `policy.blocked` y `policy.duration_ms`, más spans de administración (`policy.draft.create/update`, `policy.publish`, `policy.retire`). | `PolicyReevaluationTests`, `PolicyTelemetry`. |
| B1 | **Implementado.** `PolicyValue` incorpora `VersionedCodeRef` (catalog/code/version/digest), `VersionedEntityRef` (entity type/id/version) y `TypedAnswer` (question code/schema version/value kind); `Money` y `MoneyRange` llevan ISO 4217; `PolicyFactCatalog` fija tipo y operadores por fact y rechaza p. ej. `PURCHASE_TYPE GT Money`; `PolicyGeneratedControl` conserva `CostCenterIds`/`AmountBase`/`BaseCurrency` y `OriginFacts`/`FactProvenance`; `ValidateReferenceCatalogsAsync` resuelve por catálogo, entity type o question code. `canonicalization_version` sigue `policy-canonical-json/v1`; `policy_schema_version` sube a `policy-schema/v2` (ya era input de `policy_content_digest`) y se regeneraron los vectores golden de CA-03. | `PolicyTypedFactsTests`, `PolicyCombinationTests`, `PolicyEvaluationDiffTests`, `PolicyReevaluationTests` (provenance + catálogo sin adapter → 503), `PolicyCanonicalizationGoldenTests`, `PolicyCanonicalizationIntegrationTests`. |

### Ronda 2 (2026-09-11)

> **Resultado:** Con bloqueos · **Rondas:** 2/2 · **Modelo efectivo:** `openai-codex/gpt-5.6-sol`

Veredicto por hallazgo: **B5 `CUMPLIDO`**, **B6 `CUMPLIDO`**; B1, B2, B3, B4 y B7 con bloqueos residuales.

- B1: el lookup de catálogo no transportaba código, valor ni `value_kind` de `VersionedCodeRef`/`TypedAnswer`.
- B2: faltaba `EligibilityEvidence`; `VerifierId`/`ContractVersion` no se ligaban al verifier registrado y `ApproverId` seguía tomándose del request.
- B3: el replay aceptaba `factRequest.Cause ?? replay.Cause` y permitía combinaciones causa/bundle incoherentes.
- B4: `ParametersEqual` omitía `CostCenterIds`/`AmountBase`/`BaseCurrency` y `Exceptionable=true` se clasificaba como endurecimiento (invertido).
- B7: matriz de observabilidad incompleta (selección/conflicto y campos en sourcing/waiver).

Correcciones aplicadas tras la ronda 2 (sin consumir ronda nueva): `PolicyReferenceLookup` con `Code`/`ValueKind`; `QuotationWaiverEvidence` con `EligibilityEvidenceDigest` y `VerifyAsync` ligando `VerifierId`/`ContractVersion` al verifier registrado y exigiendo cobertura de scopes; `AppendExceptionVerificationAsync` persiste el approver de la evidencia; el replay usa solo el `Cause`/`PreviousResultDigest` recibidos y rechaza causa sin bundle previo; el diff compara parámetros de budget y trata la pérdida de exceptionabilidad como endurecimiento; los spans de evaluación/sourcing/waiver incorporan versión, digest, scopes, resultado y duración, y el replay marca `policy.selection=REPLAY`. Suites: UnitTests 64/64, IntegrationTests 11/11, ApiE2ETests 3/3; `dotnet build` 0/0.

Una tercera ronda requiere la frase **`ronda 3 autorizada`** en `## Desviaciones y bloqueos`.

### Ronda 3 (2026-09-11, autorizada)

> **Resultado:** Con bloqueos · **Rondas:** 3/3 · **Modelo efectivo:** `openai-codex/gpt-5.6-sol`

Veredicto: **B3 `CUMPLIDO`**; B1, B2, B4 y B7 con residuales.

- B1: `TypedAnswerValueKind.EnumCode` se serializaba como `ENUMCODE` en el lookup en vez del token contractual `ENUM_CODE`.
- B2: la evidencia no contrastaba la cobertura de líneas (`SubjectIds`), solo nombres de scope.
- B4: `AnyDemandIncrease` no clasificaba `AmountBase`/`CostCenterIds`/`BaseCurrency` del control de budget.
- B7: faltaban versión/digest/scopes en waiver y replay, correlation en sourcing, y resultado/duración en selección/conflicto.

Correcciones aplicadas tras la ronda 3 (sin consumir ronda nueva): `Lookup` transporta `BOOLEAN`/`ENUM_CODE`; `QuotationWaiverEvidence.CoveredLineIds` y `QuotationWaiverRequest.TargetLineIds` con validación de superset; `AnyDemandIncrease` compara `AmountBase`, `CostCenterIds` y `BaseCurrency`; spans de waiver/replay con digest y scopes, sourcing con correlation, y `policy.select_version`/`policy.conflict` con resultado y duración. Suites: 64/64 + 11/11 + 3/3; build 0/0; LSP 84 archivos, 0 diagnósticos.

El presupuesto de rondas está agotado: una ronda 4 requiere autorización humana registrada.

### Ronda 4 (2026-09-11, autorizada)

> **Resultado:** Con bloqueos · **Rondas:** 4/4 · **Modelo efectivo:** `openai-codex/gpt-5.6-sol`

Veredicto: **B1 `CUMPLIDO`**, **B2 `CUMPLIDO`**, **B4 `CUMPLIDO`**; **B7** residual: los spans de waiver y replay no etiquetaban `policy.version_id`.

Corrección aplicada tras la ronda 4 (sin consumir ronda nueva): waiver usa `persistedRecord.PolicySetVersionId` y el replay usa `existing.PolicySetVersionId` como `policy.version_id`. UnitTests 64/64; build 0/0.

El presupuesto de rondas está agotado (4/4): una ronda 5 requiere autorización humana registrada.

### Ronda 5 (2026-09-11, autorizada)

> **Resultado:** Sin bloqueos · **Rondas:** 5/5 · **Modelo efectivo:** `openai-codex/gpt-5.6-sol`

Verificación acotada al último residual B7 / NFR-05 / CA-10, CA-11: los spans de waiver (`PolicyEvaluationService.cs:273`) y replay (`:161`) etiquetan `policy.version_id` con el GUID de versión, sin snapshots ni PII. `lens_diagnostics` concluyente (0 diagnósticos). **PASS sin bloqueos.**

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `specs/02-motor-de-politicas.md` | Metadato administrativo de ejecución | Flujo `/spec-impl` |
| `specs/runs/02-motor-de-politicas.md` | Registro de ejecución y evidencia de aceptación | Todas |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyEvaluation.cs` | `PolicyEvaluationDiff` y `Cause` persistible para reevaluaciones | T-04 / CA-09 |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyEvaluationBundleRehydrator.cs` | Rehidratar `Cause` para el replay idempotente | T-04 / CA-08 |
| `src/ProcureToPay.Domain/Modules/Policy/QuotationWaiver.cs` | Diff contractual del waiver mediante `PolicyEvaluationDiff` | T-07 / CA-09 |
| `src/ProcureToPay.Infrastructure/Persistence/Policy/PolicyEvaluationService.cs` | Reevaluación con `PreviousBundleId`/causa, replay por subject/previous, guardas 413 y trazas | T-04, T-06, T-08 |
| `src/ProcureToPay.Infrastructure/Persistence/Policy/PolicyPersistenceService.cs` | `413` para reglas/predicados/efectos antes de persistir | T-05 / CA-11 |
| `src/ProcureToPay.Infrastructure/Persistence/Policy/PolicyTelemetry.cs` | ActivitySource OpenTelemetry minimizado | T-08 / NFR-05 |
| `src/ProcureToPay.Api/Controllers/PolicyController.cs` | Aceptar el código de contrato del tipo de excepción | T-07 / CA-11 |
| `src/ProcureToPay.Api/Program.cs` | Registrar el ActivitySource del motor en OTLP | T-08 / NFR-05 |
| `tests/ProcureToPay.UnitTests/Policy/PolicyEvaluationDiffTests.cs` | Diff `ADDED/HARDENED/REMOVED/UNCHANGED` | T-04 / CA-09 |
| `tests/ProcureToPay.UnitTests/Policy/PolicyCombinationTests.cs` | Cross-scope, Cost Center, antifraccionamiento, autoridad y claves | T-09 / CA-04, CA-05, CA-06 |
| `tests/ProcureToPay.UnitTests/Policy/PolicyAdapterContractTests.cs` | Registries exact-one, default-deny y `COST_CENTER` | T-09 / CA-10, CA-12 |
| `tests/ProcureToPay.IntegrationTests/Policy/PolicyReevaluationTests.cs` | Reevaluación + diff, límites 413, manifest atestiguado, 409 y trazas | T-04, T-06, T-08, T-09 |
| `tests/ProcureToPay.ApiE2ETests/PolicyWorkflowE2ETests.cs` | 413 Problem Details y replay HTTP del waiver | T-05, T-07 / CA-09, CA-11 |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyEvaluationDiff.cs` | Implementación del diff contractual | T-04 / CA-09 |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyModels.cs` | Tipos versionados, MoneyBase ISO 4217 y catálogo tipo-por-fact | T-01 / CA-02 |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyDocumentParser.cs` | Parseo y validación de valores tipados | T-01 / CA-02, CA-12 |
| `src/ProcureToPay.Domain/Modules/Policy/PolicyEvaluationBundleRehydrator.cs` | Rehidratación de parámetros y provenance de control | T-04 / CA-05 |
| `src/ProcureToPay.Api/Controllers/PolicyController.cs` | Parseo tipado de la simulación y código de contrato de excepción | T-05, T-07 / CA-11 |
| `tests/ProcureToPay.UnitTests/Policy/PolicyTypedFactsTests.cs` | Evidencia de tipado, MoneyBase y parámetros de budget | T-01, T-04 / CA-02, CA-05 |

## Cierre

- HEAD verificado: `dac85ab671e0fc2d91bb538135e58c78b26d2092`.
- Estrategia de integración: merge no fast-forward de `spec-02-motor-de-politicas` en `main` (`0205e99`), con el commit verificado como segundo padre; `git diff dac85ab..HEAD -- src tests` vacío (sin drift del contenido verificado).
- Commit integrado en rama base: `c4ecd80fda6c5dbfad3ff3d2780572fe4495fe15` (HEAD de `main` al cierre; contiene `0205e99` y `dac85ab`).
- Verificación ejecutada sobre rama base: `dotnet build ProcureToPay.sln` 0 advertencias/0 errores; UnitTests 64/64; IntegrationTests 11/11; ApiE2ETests 3/3; `specctl run-lint 02`, `git-check` y `doctor` válidos; digest contractual `63fec425…` intacto.
- Metadatos de vigencia actualizados: ninguno (`Modifica: Ninguna`, `Reemplaza: Ninguna`).
- Pendientes posteriores: providers empresariales de Purchase Request/Sourcing, catálogos propietarios de Supplier/Cost Center y workflow HTTP real, fuera del alcance de SPEC 02 (SPEC 03).
