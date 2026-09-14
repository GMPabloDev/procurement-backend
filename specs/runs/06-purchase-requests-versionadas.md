# RUN SPEC 06 — Purchase Requests versionadas e integración con Policy y Approval

> **Formato:** sdd-run/v2
> **Estado del run:** Integrado
> **Spec:** specs/06-purchase-requests-versionadas.md
> **Revisión contractual:** 1
> **Commit de la spec:** 0027efbd3478fd1321689f787489b00f5c56e9ca
> **Blob aprobado:** 94000447c516c99624c1a05b4c1dfc5b3d1d9d8a
> **Digest contractual:** f8c570320cc1093698b8b2998c83eee168335c5345236a2fe457755bf4a41dd8
> **Rama base:** main
> **Commit base:** 0027efbd3478fd1321689f787489b00f5c56e9ca
> **Rama de implementación:** spec-06-purchase-requests-versionadas
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-14 05:52 -0500
> **Actualizado:** 2026-09-14 12:40 -0500
> **HEAD verificado:** 6e94b4d8fdc2423eb60e5f1b7f173965ad036737
> **Commit de integración:** afbbb1dc2db423ec53f318ac07c8dd20899c22c8

## Línea base

- `dotnet build ProcureToPay.sln --nologo -v q`: compilación correcta, 3 avisos preexistentes (CS0105 en `PolicyEvaluatorTests.cs`, CS9113 en `ApprovalWorkflowWorker.cs`, CS8602 en `ApprovalDecisionIntegrationTests.cs`).
- Línea base del checkpoint CP-02 (commit `adb49a7`): Unit 160/160, Integración 55/55, API/E2E 17/17.
- Árbol final verificado en `f289865` (fixes R6/R7 de la ronda 2): Unit **167/167**, Integración **63/63**, API/E2E **21/21** (251 pruebas, 0 fallos). Árbol previo `953c811` (fixes R6–R10): 167/62/21; árbol `80e73c8`: 167/61/21.
- Nota de entorno: la primera invocación de `dotnet test` tras `dotnet build ProcureToPay.sln` reportó 0 pruebas (exit 5) y, a partir de CP-03, el runner `Microsoft.Testing.Platform` reportó 0 pruebas de forma persistente aunque compilaba el proyecto de pruebas. Las suites se ejecutaron con los binarios compilados (`tests/<proyecto>/bin/Debug/net10.0/<proyecto>`), que es la evidencia válida; ver `## Desviaciones y bloqueos`.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio `PurchaseRequests` (snapshot append-only, delta retained/changed/added/removed, refs tipadas), persistencia EF con schema `PurchaseRequest`, migración `Spec06PurchaseRequests`, ledger de comandos idempotente y eventos de lifecycle. Evidencia: `PurchaseRequestCanonicalGoldenTests` (5), `PurchaseRequestPolicyIntegrationTests.Revision_reuses_unchanged_line_versions_and_advances_changed_ones` y `PurchaseRequestSubmissionIntegrationTests.Concurrent_submissions_and_revisions_produce_exactly_one_effect` (revisión concurrente produce una sola sucesora). | `adb49a7` / CP-01–CP-02 |
| T-02 | Verificada | `PurchaseRequestController` con create/revision/cancel/lectura y payloads exactos `snake_case` (incluye `command_version`, decimales como string, sin `agreement_status` y propiedades desconocidas rechazadas), actor derivado del token (organización y requester nunca del payload), `403` para mutaciones de otro usuario, `404` para lecturas fuera de scope y proyección minimizada para `AUDITOR` con attestations y referencias minimizadas; mapeo de dependencia → `503 /problems/purchase-request-dependency-unavailable`, tamaño → `413` (500/501 líneas, 256/257 respuestas, 5 MiB con o sin `Content-Length`) y forma/límites → `400`. Evidencia API/E2E: `PurchaseRequestE2ETests` (4). | `f289865` / CP-04–CP-06 |
| T-03 | Verificada | `PurchaseRequestAttestationService` (owners exact-one, timeout 5 s, eco de binding, cardinalidades, attestation+manifest atómicos y ahora race-safe) y canonicalización `reference_attestation_digest`/`domain_attestation_digest`/`policy_manifest_digest` con vectores golden publicados. Evidencia: `Missing_owner_registration_fails_closed_without_manifest`, `Inactive_reference_is_rejected_without_manifest`, golden de attestation `e00b6c3f…` y la carrera de presentación concurrente sin attestation duplicada. | `80e73c8` / CP-03–CP-04 |
| T-04 | Verificada | `PurchaseRequestPolicyFactProvider` registrado exact-one como `IPolicyFactProvider` (`PURCHASE_REQUEST/REQUEST_EVALUATE`, `purchase-request-domain`, `purchase-request-policy-facts/v1`), proyección al catálogo cerrado de SPEC 02, verificación de digests y fail-closed ante corrupción. Evidencia: `Attested_purchase_request_is_evaluated_by_policy_with_every_line`, `Provider_fails_closed_when_the_stored_manifest_is_corrupted` y `PolicyRiskAnswerMatchingTests` (13). | `adb49a7` / CP-02 |
| T-05 | Verificada | `PurchaseRequestSubmissionService`: attempt durable (`PENDING → POLICY_CONFIRMED → APPROVAL_CONFIRMED`, `BLOCKED`, `DEPENDENCY_FAILED`), keys internas deterministas por request/versión, attestation → Policy por referencia → apertura o supersesión con adapter v2, y proyección `SUBMITTED → IN_APPROVAL\|APPROVED`. Evidencia: `Submit_presents_once_and_resumes_after_a_partial_approval_failure` (fallo inyectado tras Policy, retry recupera el mismo attempt y un solo caso), `Cancellation_closes_an_open_case_and_keeps_a_completed_case_terminal` (la cancelación cierra el caso abierto por su workload owner con relectura de carrera) y `PurchaseRequestE2ETests`/`POST /v1/purchase-requests/{id}/submission`. | `80e73c8` / CP-03 |
| T-06 | Verificada | `policy-approval-adapter/v2` (requester obligatorio, `requester_id=originator_id` admitido y excluido una sola vez, `APPROVE\|REJECT\|REQUEST_CHANGES`), `approval-supersession-delta/v1` bajo `approval-canonical-json/v3` con cobertura `RETAINED\|ADDED\|REMOVED`, carry-forward solo con materiality verificada y compatibilidad v1/v2. Evidencia: golden de bytes y SHA-256 `6016e088…` en `ApprovalSupersessionDeltaTests`, `Supersession_v3_fingerprint_matches_the_contractual_vector_bytes_and_digest` y `Delta_supersession_carries_forward_retained_lines_only_with_verified_materiality`. | `80e73c8` / CP-03 |
| T-07 | Verificada | Consumer exact-one de `approval-result/v2`, `approval-result/v3` y `approval-case-lifecycle/v1`: dedupe por `event_id+contract_version` y target, verificación de organización/caso/subject/manifest, inbox append-only y proyección por línea/agregada que ignora eventos tardíos o duplicados. Evidencia: `Approval_results_project_line_status_and_ignore_duplicate_deliveries`, `Revision_supersedes_with_retained_added_lines_and_verified_materiality` y la corrección de `ApprovalOutboxEvent.Restore` (versión de contrato persistida); la lectura auditada de attestations se prueba en `Submit_presents_once_and_resumes_after_a_partial_approval_failure` y `PurchaseRequestE2ETests`. | `80e73c8` / CP-03–CP-04 |
| T-08 | Verificada | `PurchaseRequestHealthCheck` (`/health/purchase-request`) distingue provider/adapter/workload, el slot exacto de owner ausente, attempts atascados o fallidos, attestation ausente y manifest corrupto; `docs/purchase-request-operations.md` documenta despliegue, contratos, recuperación, proyección, health, límites y rollback. Evidencia: `Health_reports_the_missing_purchase_request_registrations`, la suite completa de regresión SPEC 01–05 y el recorrido de cancelación con caso Approval abierto. | `80e73c8` / CP-04 |

## Checkpoints

### CP-01 — Bloques 1 y 2 parciales (T-01, T-03, T-04)

Árbol probado: `working-tree sobre 0027efbd` en rama `spec-06-purchase-requests-versionadas`. Unit **147/147**, Integración **55/55**, API/E2E **17/17**. Evidencia nueva: `PurchaseRequestCanonicalGoldenTests` (5) y `PurchaseRequestPolicyIntegrationTests` (5).

### CP-02 — Matcher de riesgo y superficie HTTP (T-04 completo, T-02 parcial)

Commit `9734f00` más `working-tree sobre 9734f00`, luego `adb49a7`. Unit **160/160**, Integración **55/55**, API/E2E **17/17** (232 pruebas). Evidencia nueva: `PolicyRiskAnswerMatchingTests` (13) y `PurchaseRequestController`.

### CP-03 — Adapter v2, supersesión v3 y orquestador (T-05, T-06, T-07)

Árbol probado: `working-tree sobre adb49a7`. Cambios: `policy-approval-adapter/v2`, `approval-supersession-delta/v1` con canonicalización v3, orquestador de presentación con attempts durables y consumer de resultados/lifecycle. Comandos: `dotnet build ProcureToPay.sln` (0 errores), Unit **167/167**, Integración **61/61** (incluye `ApprovalSupersessionDeltaTests`, `Delta_supersession_*` y `Submit_presents_once_*`). Próximo paso: evidencia API/E2E de T-02 y T-08.

### CP-04 — Superficie HTTP, salud y árbol final (T-02, T-08)

Commit candidato `80e73c85aa87d065f87dd616ee01e73d655759d5` en rama `spec-06-purchase-requests-versionadas`. Cambios: `PurchaseRequestE2ETests` (visibilidad, límites ±1, Problem Details, idempotencia y health), `PurchaseRequestHealthCheck` y `docs/purchase-request-operations.md`; además la corrección de la versión de contrato en el outbox, la attestation race-safe, el orden determinista del lifecycle y el mapeo `413` de los límites. Comandos: `dotnet build ProcureToPay.sln` (0 errores), Unit **167/167**, Integración **61/61**, API/E2E **21/21** (249 pruebas, 0 fallos) sobre el árbol del commit. Próximo paso: revisión independiente y metadatos finales.

### CP-05 — Cierre de hallazgos R6–R10 de la revisión independiente

Commit `953c811` en rama `spec-06-purchase-requests-versionadas`. Cambios: payloads `snake_case` exactos con `command_version` y rechazo de propiedades desconocidas (R6), cancelación que cierra el caso Approval abierto con relectura de carrera (R7), límite de 5 MiB independiente de `Content-Length` (R8), digest de materialidad publicado en el delta y afirmación de carry-forward solo por hechos (R9) y lectura auditada de attestations minimizadas (R10). Comandos: `dotnet build ProcureToPay.sln` (0 errores), Unit **167/167**, Integración **62/62**, API/E2E **21/21**. Próximo paso: ronda delta de verificación independiente sobre los IDs R6–R10.

### CP-06 — Cierre de R6 y R7 (ronda 2 de revisión)

Commit `f289865` en rama `spec-06-purchase-requests-versionadas`. Cambios: miembros obligatorios de los payloads con `[JsonRequired]` y guardas de null (R6), E2E negativos de `risk_answers:null`, `line_drafts` nulo y omitido; seam documentado `BeforeApprovalCaseCancel` y prueba determinista de las dos ramas de la carrera de cancelación (reintento que cancela y 409 fail-closed sin medio estado) (R7). Comandos: `dotnet build ProcureToPay.sln` (0 errores), Unit **167/167**, Integración **63/63**, API/E2E **21/21**. Próximo paso: ronda delta de R6/R7 si el usuario autoriza una tercera llamada (el paquete automático por run está agotado) y metadatos finales.

### CP-07 — PASS de la revisión independiente y run listo

Árbol revisado y certificado: `6e94b4d8fdc2423eb60e5f1b7f173965ad036737` (código `f289865`, metadatos posteriores solo administrativos). Ronda 3 delta autorizada: PASS, R6 y R7 resueltos sin cobertura pendiente. Suites sobre el código certificado: Unit **167/167**, Integración **63/63**, API/E2E **21/21**. `specctl preflight 06` coherente. Próximo paso: el usuario integra el código ya commiteado y ejecuta `/spec-impl 06 --close` desde la rama base.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | `Revision_reuses_unchanged_line_versions_and_advances_changed_ones`, `Concurrent_submissions_and_revisions_produce_exactly_one_effect` (dos revisiones concurrentes → una sola sucesora; la perdedora falla cerrado sin versión ni delta parcial) y el ledger de comandos con replay por key en create/revision/cancel. | Pendiente de revisión |
| CA-02 | Cumplido | `PurchaseRequestE2ETests` (4) cubre visibilidad requester/otro/`AUDITOR` scoped y organizacional/`ADMIN`, proyección minimizada con attestations, payload `snake_case` exacto con `command_version`, miembros obligatorios presentes y no nulos (`risk_answers`/`line_drafts` null u omitido → `400`) y miembro desconocido rechazado, 500 vs 501 líneas (`413`), 256 vs 257 respuestas (`413`), 5 MiB sin `Content-Length` (`413`), key/summary inválidos (`400`) y terminales (`409`); unitarias cubren FX, monedas, importes y refs. | Pendiente de revisión |
| CA-03 | Cumplido | Owners exact-one con `503` sin manifest, referencia inactiva con `422` sin manifest, manifest corrupto con `503` en el provider, golden de attestation `e00b6c3f…` y attestation+manifest atómicos y race-safe ante presentaciones concurrentes. | Pendiente de revisión |
| CA-04 | Cumplido | `Attested_purchase_request_is_evaluated_by_policy_with_every_line` (2 LINE + 1 REQUEST reales), `PolicyRiskAnswerMatchingTests` (13) y `Submit_presents_once_and_resumes_after_a_partial_approval_failure`: el retry reutiliza el bundle de Policy confirmado, el fallo inyectado no crea caso ni éxito y `BLOCKED` responde `422`. | Pendiente de revisión |
| CA-05 | Cumplido | Descriptor v2 con requester obligatorio y `requester=originator` excluido una sola vez, acciones `APPROVE\|REJECT\|REQUEST_CHANGES` en cada requirement, retry idéntico recupera el mismo caso y otra carga con la key da `409` (`ApprovalContractTests`, `PolicyApprovalAdapterTests`, `Submit_presents_once_*`). | Pendiente de revisión |
| CA-06 | Cumplido | `Cancellation_closes_an_open_case_and_keeps_a_completed_case_terminal` y `Cancellation_retries_once_and_fails_closed_when_the_case_keeps_moving` (carrera determinista con las dos ramas), `Delta_supersession_carries_forward_retained_lines_only_with_verified_materiality` y `Delta_supersession_covers_removed_and_added_lines_and_fails_closed_on_invalid_deltas`: delta `RETAINED\|ADDED\|REMOVED` cubre ambos manifests, `ADDED` nunca hace carry-forward, `REMOVED` no reaparece, retained solo con materiality/contrato/set verificados y `CHANGES_REQUESTED` prevalece. `Approval_results_project_line_status_and_ignore_duplicate_deliveries` cubre duplicados y eventos tardíos. | Pendiente de revisión |
| CA-07 | Cumplido | Goldens independientes de content, revision, attestations, manifests, submit, materiality y supersesión v3 (`6016e088…`) reproducen bytes y SHA-256; el `materiality_digest` declarado en el delta es el preimage publicado exacto de `purchase-request-materiality/v1` y la comparación de carry-forward solo normaliza referencias inmateriales; permutar sets no cambia el hash y alterar un fact o el membership sí. Los vectores v2 históricos conservan sus hashes (`ApprovalEvolutionGoldenTests` sin cambios). | Pendiente de revisión |
| CA-08 | Cumplido | Recorrido real PR→Policy→Approval→PR sin doubles de provider/adapter (solo owners externos controlados), proyección por línea/request, presentación/revisión concurrentes con un solo efecto, cancelación que cierra el caso abierto antes de confirmar con relectura y `409` fail-closed si sigue moviéndose, recuperación del mismo attempt tras fallo entre módulos y `/health/purchase-request` con razones acotadas sin texto, PII, tokens ni snapshots. | Pendiente de revisión |

## Desviaciones y bloqueos

### Bloqueo material

- Ninguno. El alcance de la spec está implementado y verificado en `80e73c8`; queda la verificación independiente y los metadatos finales.

### Desviaciones registradas

- **Diagnóstico de lenguaje obsoleto.** pi-lens reportó repetidamente que los namespaces y tipos nuevos no existían, contradiciendo compilaciones frescas (`dotnet build` con 0 errores) durante toda la sesión. Se registra una sola vez y la verificación se hizo exclusivamente con el compilador y con los binarios de prueba compilados; no se repitieron barridos por esa captura.
- **Runner `dotnet test` con 0 pruebas.** Además de la intermitencia ya conocida en CP-01, el wrapper `Microsoft.Testing.Platform` reportó de forma persistente 0 pruebas aun recompilando. Las suites se ejecutaron directamente con `tests/<proyecto>/bin/Debug/net10.0/<proyecto>`, que es la evidencia válida y la misma asamblea que compila el runner. Es un riesgo de entorno, no de producto.
- **Interpretación del payload cerrado.** `purchase-request-line-content/v1` no incluye `agreement_status`, preferencia de proveedor ni catálogo de riesgo; la proyección emite `PREFERRED_SUPPLIER=false` y `EXTERNAL_AGREEMENT_STATUS=NONE` y omite `DATA_RISK` en vez de inventar un hecho. Documentado en `PurchaseRequestPolicyProjection`.
- **Clave de provenance.** El mapa `provenance` del bundle usa claves con identidad de línea (`<line-id>:<fact-key>`); el preimage publicado de `purchase-request-materiality/v1` conserva la clave plana del vector golden.
- **Fingerprint de cancelación.** La idempotencia de cancelación usa la forma de `purchase-request-submission/v1` con digests cero como clave privada del ledger de comandos; no es un digest público del contrato.
- **Prueba de materialidad para carry-forward.** El preimage publicado de `purchase-request-materiality/v1` incluye en `provenance` referencias con versión de snapshot y digest de attestation, que cambian en cada versión aunque los hechos sean idénticos. El `materiality_digest` declarado en el delta es el preimage publicado exacto del replacement, y la afirmación de carry-forward (`VerifiedMaterialityIdentities`) es la comparación del dueño in-process sobre los hechos con el ancla del fact (`#<field>`), sin introducir una segunda semántica de digest. Se documenta en `PurchaseRequestSubmissionService.MaterialityProof`.
- **Versión de contrato del outbox.** El camino de hidratación en memoria de `ApprovalOutboxEvent` devolvía siempre `approval-result/v2`; los eventos v3 y lifecycle se entregaban al consumer v2 y fallaban en el dispatcher. Se corrigió propagando la `ContractVersion` persistida en `ApprovalOutboxEvent.Create/Restore` y `ApprovalOutboxDispatcher.Hydrate`, sin cambiar payloads ni digests históricos.
- **Límites de tamaño con `413`.** Las violaciones de 500 líneas y 256 respuestas por línea lanzaban `DomainValidationException` (`400`); se movió `PurchaseRequestPayloadTooLargeException` al dominio y se mapean a `413` como exige REQ-11. El guard de 5 MiB se evalúa en el borde HTTP con el límite de Kestrel y, cuando el host no expone esa feature (TestServer, transferencia chunked), con un stream de lectura limitado: ambos caminos devuelven `413`.
- **Ampliación del presupuesto de revisión.** ronda 3 autorizada explícitamente por el usuario tras agotar las dos rondas automáticas del run; se acotó al delta de R6/R7 y resultó PASS sin hallazgos nuevos.
- **E2E de SPEC 05 con adapter v2.** Las pruebas de contrato de Policy→Approval ahora declaran `requesterId` en las submissions del adapter, como exige REQ-07; los cuerpos de excepción de política y quotation-waiver conservan su contrato previo (requester nulo).

## Verificación independiente

> **Resultado:** Sin bloqueos
> **Rondas:** 3/3
> **Triaje:** Sin bloqueos
> **Modelo efectivo:** openai-codex/gpt-5.6-sol · effort high (metadatos de la herramienta)
> **Método:** ronda 1 a `646ddbd` y ronda 2 delta a `b20e630` (R8/R9/R10 resueltos; R6/R7 abiertos por presencia de miembros y evidencia de carrera), correcciones en `f289865`. Revisión independiente de implementación a `646ddbd` (cobertura full de las cinco áreas, evidencia del candidato: build 0 errores, Unit 167/167, Integración 61/61, API/E2E 21/21). Hallazgos: R6 payloads no exactos, R7 cancelación sobre caso abierto, R8 límite 5 MiB evadible sin `Content-Length`, R9 digest de materialidad no publicado, R10 lectura auditora sin attestations. Correcciones con regresión en `953c811` (Unit 167/167, Integración 62/62, API/E2E 21/21). Ronda 2 delta a `b20e630`: R8/R9/R10 resueltos, R6/R7 abiertos por presencia de miembros y evidencia de carrera; correcciones en `f289865` con Unit 167/167, Integración 63/63 y API/E2E 21/21. Ronda 3 delta autorizada a `6e94b4d`: **PASS**, R6 y R7 resueltos sin hallazgos nuevos.
> **Fecha:** 2026-09-14

## Cierre

- **Integración:** merge `afbbb1dc2db423ec53f318ac07c8dd20899c22c8` de `spec-06-purchase-requests-versionadas` (tip `ed8e79f`) sobre `main` (`0027efb`), estrategia `--no-ff`, 2026-09-14.
- **Equivalencia:** el candidato verificado `6e94b4d` (código `f289865`) es ancestro del commit de integración; el diff de `src/`, `tests/` y `docs/` entre la rama integrada y `main` está vacío, por lo que el código integrado es exactamente el revisado.
- **Validaciones:** `dotnet build ProcureToPay.sln` 0 errores; Unit **167/167**, Integración **63/63**, API/E2E **21/21** sobre el árbol certificado; `specctl doctor` sin errores; `specctl git-check 06` coherente (solo el aviso esperado de metadatos sin commitear).
- **Vigencia:** SPEC 02, 03, 04 y 05 quedan `Sustituida parcialmente por SPEC 06`; SPEC 06 pasa a `Implementada` / `Integrada` / `Vigente`.
