# RUN SPEC 06 — Purchase Requests versionadas e integración con Policy y Approval

> **Formato:** sdd-run/v2
> **Estado del run:** Bloqueado
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
> **Actualizado:** 2026-09-14 05:52 -0500
> **HEAD verificado:** Pendiente
> **Commit de integración:** Pendiente

## Línea base

- `dotnet build ProcureToPay.sln --nologo -v q`: compilación correcta, 3 avisos preexistentes (CS0105 en `PolicyEvaluatorTests.cs`, CS9113 en `ApprovalWorkflowWorker.cs`, CS8602 en `ApprovalDecisionIntegrationTests.cs`).
- `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj -v n`: 142/142 correctas.
- `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj -v n`: 50/50 correctas (Testcontainers/Docker disponibles).
- `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj -v n`: 17/17 correctas.
- Total 209 pruebas, 0 fallos, sobre `working-tree sobre 0027efbd`.
- Nota: la primera invocación de `dotnet test` tras `dotnet build ProcureToPay.sln` reportó 0 pruebas (exit 5); se reprodujo de forma no determinista y se resolvió recompilando el proyecto de pruebas. Se vigila como riesgo de entorno, no de producto.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio `PurchaseRequests` (snapshot append-only, delta retained/changed/added/removed, refs tipadas), persistencia EF con schema `PurchaseRequest`, migración `Spec06PurchaseRequests`, ledger de comandos idempotente y eventos de lifecycle. Evidencia: `PurchaseRequestCanonicalGoldenTests` (5) y `PurchaseRequestPolicyIntegrationTests.Revision_reuses_unchanged_line_versions_and_advances_changed_ones`. Pendiente de evidencia multi-instancia (ver bloqueos). | working-tree sobre 0027efbd / CP-01 |
| T-02 | Parcial | `PurchaseRequestController` con create/revision/cancel/lectura, actor derivado del token (organización y requester nunca del payload), `403` para mutaciones de otro usuario, `404` para lecturas fuera de scope y proyección minimizada para `AUDITOR`; mapeo de `PurchaseRequestDependencyUnavailableException` → `503 /problems/purchase-request-dependency-unavailable` y de tamaño → `413`. Pendiente: pruebas API/E2E de límites, visibilidad y Problem Details. | working-tree sobre 9734f00 / CP-02 |
| T-03 | Verificada | `PurchaseRequestAttestationService` (owners exact-one, timeout 5 s, eco de binding, cardinalidades, attestation+manifest atómicos) y canonicalización `reference_attestation_digest`/`domain_attestation_digest`/`policy_manifest_digest` con vectores golden publicados. Evidencia: `Missing_owner_registration_fails_closed_without_manifest`, `Inactive_reference_is_rejected_without_manifest`, golden de attestation `e00b6c3f…`. | working-tree sobre 0027efbd / CP-01 |
| T-04 | Verificada | `PurchaseRequestPolicyFactProvider` registrado exact-one como `IPolicyFactProvider` (`PURCHASE_REQUEST/REQUEST_EVALUATE`, `purchase-request-domain`, `purchase-request-policy-facts/v1`), proyección al catálogo cerrado de SPEC 02, verificación de digests y fail-closed ante corrupción. Evidencia: `Attested_purchase_request_is_evaluated_by_policy_with_every_line` (2 LINE + 1 REQUEST reales), `Provider_fails_closed_when_the_stored_manifest_is_corrupted` y `PolicyRiskAnswerMatchingTests` (13: matriz EQ/NEQ/IN/NOT_IN, ausencia, varias preguntas, duplicidad y validación de predicates) que implementa la modificación de SPEC 02. | working-tree sobre 9734f00 / CP-02 |
| T-05 | Pendiente | Requiere adapter v2 y supersession v3. | — |
| T-06 | Pendiente | Snapshot v3 y delta de supersesión definidos en dominio (`approval-supersession-delta/v1` aún sin implementar en Approval). | — |
| T-07 | Pendiente | Sin consumer de resultados ni proyección de estado. | — |
| T-08 | Pendiente | Sin health propio ni `docs/purchase-request-operations.md`. | — |

## Checkpoints

- **CP-01 — Bloques 1 y 2 parciales (T-01, T-03, T-04).** Árbol probado: `working-tree sobre 0027efbd` en rama `spec-06-purchase-requests-versionadas`. Comandos: `dotnet build ProcureToPay.sln --nologo -v q` (0 errores) y suites completas en el árbol estable — Unit **147/147**, Integración **55/55**, API/E2E **17/17** (baseline 209 → 219 con las 10 pruebas nuevas). Evidencia nueva: `PurchaseRequestCanonicalGoldenTests` (5) y `PurchaseRequestPolicyIntegrationTests` (5). Bloqueo: T-05..T-08 dependen de adapter v2/supersession v3; ver `## Desviaciones y bloqueos`. Próximo paso: implementar adapter v2 + supersession v3 para habilitar orquestador e inbox.
- **CP-02 — Matcher de riesgo y superficie HTTP (T-04 completo, T-02 parcial).** Commit `9734f00` (bloques 1–2 verdes) más `working-tree sobre 9734f00`. Cambio: matcher `RISK_ANSWER` de SPEC 02 (selección por question/schema, ausencia nunca satisface operadores negativos, duplicidad o miembro de otro kind invalida el bundle) y `PurchaseRequestController` con sus DTOs y Problem Details. Comandos: `dotnet build ProcureToPay.sln` (0 errores) y suites completas — Unit **160/160**, Integración **55/55**, API/E2E **17/17** (232 pruebas, 0 fallos). Evidencia nueva: `PolicyRiskAnswerMatchingTests` (13). Próximo paso: T-06 (adapter v2 + supersesión v3), luego T-05 (orquestador), T-07 (inbox) y T-02/T-08 (evidencia API/E2E, health y runbook).

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Parcial | `Revision_reuses_unchanged_line_versions_and_advances_changed_ones` prueba retained por ref exacta, changed con avance de una versión y delta cubriendo ambos manifests; el ledger de comandos y los constraints únicos impiden duplicar la sucesora. Falta la prueba de dos escritores concurrentes y de replay de create/revision por key. | Pendiente de revisión |
| CA-02 | Pendiente | Sin superficie HTTP ni pruebas de visibilidad/Problem Details; las validaciones de contenido (FX, producto preferido/requerido, límites) sí están cubiertas por unitarias. | — |
| CA-03 | Cumplido | Los tests de integración cubren owner ausente (`503` sin manifest), referencia inactiva (`422` sin manifest) y manifest corrupto (`503` en el provider); el golden de attestation fija bytes y SHA-256 reproducibles. | Pendiente de revisión |
| CA-04 | Parcial | `Attested_purchase_request_is_evaluated_by_policy_with_every_line` demuestra que el provider real sirve solo versiones atestiguadas y que el motor evalúa todas las líneas (2 LINE + 1 REQUEST) sin facts ni total del caller; `PolicyRiskAnswerMatchingTests` cubre la matriz de risk answers (ausencia, coincidencia, varias preguntas, duplicidad y fail-closed). Falta el retry del submit (T-05). | Pendiente de revisión |
| CA-05 | Pendiente | Requiere adapter v2, requester=originator y orquestador idempotente (T-05/T-06). | — |
| CA-06 | Pendiente | Requiere supersession v3 e inbox (T-06/T-07). | — |
| CA-07 | Parcial | Golden de attestation (`e00b6c3f…`) y de materiality (`2a5dcf85…`) reproducen byte a byte los vectores publicados; `Materiality_is_set_order_invariant_and_field_sensitive` cubre invariacia de orden y sensibilidad a un fact. Faltan goldens de content/revision/manifest/submit y el vector de supersession v3. | Pendiente de revisión |
| CA-08 | Pendiente | Requiere recorrido E2E, carreras de cancelación/revisión, health y telemetría (T-05..T-08). | — |

## Desviaciones y bloqueos

### Bloqueo material

- **Alcance pendiente que impide declarar el run listo.** T-02 (API de borrador/revisión/cancelación), T-05 (orquestador Policy→Approval), T-06 (adapter v2 + supersesión `approval-supersession-delta/v1` con canonicalización v3) y T-07 (inbox/proyección) no están implementados; T-08 (health/runbook) tampoco. Consecuencia: CA-02, CA-05, CA-06 y CA-08 no son demostrables todavía, y CA-01/CA-04/CA-07 quedan parciales. La causa raíz es de volumen: la extensión de Approval (supersesión con cardinalidad variable) es el habilitador de los tres bloques restantes.
- Opciones agrupadas para continuar: (a) aprobar una spec 07 que extraiga adapter v2/supersesión v3 y orquestador con su propia revisión; (b) continuar el run 06 en una sesión nueva reanudando desde `CP-01` con los mismos IDs; (c) reducir el alcance contractual de SPEC 06 a los bloques 1 y 2 mediante revisión de delta aprobada.

### Desviaciones registradas

- **Diagnóstico de lenguaje obsoleto.** pi-lens reportó repetidamente que el namespace `ProcureToPay.Domain.Modules.PurchaseRequests` y los tipos nuevos no existían, contradiciendo compilaciones frescas (`dotnet build` con 0 errores) más de dos veces. Se registra una sola vez y la verificación se hizo exclusivamente con el compilador; no se repitieron barridos por esa captura.
- **Interpretación del payload cerrado.** `purchase-request-line-content/v1` no incluye `agreement_status`, preferencia de proveedor ni catálogo de riesgo; la proyección emite `PREFERRED_SUPPLIER=false` y `EXTERNAL_AGREEMENT_STATUS=NONE` y omite `DATA_RISK` en vez de inventar un hecho. Documentado en `PurchaseRequestPolicyProjection`.
- **Clave de provenance.** El mapa `provenance` del bundle usa claves con identidad de línea (`<line-id>:<fact-key>`) para no perder ambigüedad con varias líneas; el preimage publicado de `purchase-request-materiality/v1` conserva la clave plana del vector golden (el golden se reproduce byte a byte).
- **Fingerprint de cancelación.** La idempotencia de cancelación usa la forma de `purchase-request-submission/v1` con digests cero como clave privada del ledger de comandos; no es un digest público del contrato.

## Verificación independiente

> **Resultado:** Pendiente
> **Rondas:** 0/2
> **Triaje:** Pendiente
> **Modelo efectivo:** Pendiente
> **Método:** Pendiente
> **Fecha:** Pendiente
