# RUN SPEC 05 — Integración de Policy con Approval Workflow

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/05-integracion-policy-approval-workflow.md
> **Revisión contractual:** 1
> **Commit de la spec:** 18977eecbb26e0a650306c317ac1943e9834349a
> **Blob aprobado:** aaba909bcec1b7b82e6a9b363c3d0341421f9602
> **Digest contractual:** 001c559d72845b0f77919df50bad5e7b74eeecb5e6dc8921d52bba932cb20f70
> **Rama base:** main
> **Commit base:** 18977eecbb26e0a650306c317ac1943e9834349a
> **Rama de implementación:** spec-05-integracion-policy-approval-workflow
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-13 09:06 -0500
> **Actualizado:** 2026-09-13 16:45 -0500
> **HEAD verificado:** Pendiente
> **Commit de integración:** Pendiente

## Línea base

- `dotnet build ProcureToPay.sln --no-restore --nologo -v q`: 0 errores, 2 advertencias preexistentes.
- Unitarias (DLL directo): **119/119** correctas.
- Integración (Docker/Testcontainers): **50/50** correctas (5m 54s).
- API/E2E (Docker/Testcontainers): **14/14** correctas (2m 19s).
- Nota de entorno heredada de SPEC 03/04: `dotnet test --project … --no-restore` no ejecuta pruebas
  con el runner de este repositorio; se usa la ejecución directa del DLL compilado. El build-server
  se apaga antes de las suites con Testcontainers porque el host queda al límite de memoria.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | `decision-scope/v1` canónico en `PolicyApprovalDescriptor` (+ regresión de drift en `PolicyModelTests`), proyección material `policy-approval-target/v1` derivada del snapshot/manifest con goldens, wire estricto `workflow-verification-request/v1`/`response/v1` con service JWT y ruta legacy de usuario cerrada. Unitarias `PolicyExceptionGoldenTests`, `HttpQuotationWaiverVerifierTests`, `PolicyModelTests`; E2E `PolicyWorkflowE2ETests` (403 usuario, 401 sin service JWT). | working-tree / CP-01 |
| T-02 | Verificada | `PolicyApprovalAdapter` (`policy-approval-adapter`) con exact-one, recálculo de digests policy/input/result/manifest y de cada target material, tabla cerrada de efectos, owners versionados y proyección de parámetros. Unitarias `PolicyApprovalAdapterTests`; integración/E2E de bundle persistido. | working-tree / CP-01 |
| T-03 | Verificada | DAG por `stage_code` y targets compartidos con edges `Approval|External` completos; requisitos en paralelo dentro de la misma fase y sin dependientes cuando no comparten target. Unitarias `PolicyApprovalAdapterTests.Dependencies_follow_stage_and_confirmed_targets`. | working-tree / CP-01 |
| T-04 | Verificada | `ApprovalPolicyExceptionRequestRecord` + `PolicyExceptionSubmissionService` (workload-only, atómico con caso/requisito, unicidad por binding y por caso/requisito) y prohibición de carry-forward en `ApprovalSupersessionService`. Migración `Spec05PolicyExceptionIntegration`; E2E del flujo completo. | working-tree / CP-02 |
| T-05 | Verificada | `POST /v1/policy-exceptions/verify` con esquema service JWT dedicado, `PolicyExceptionVerificationService` (lookup server-side, comparación exacta, SoD, vigencia, revocación, dos constraints de replay) y `PolicyExceptionVerificationRecord`. Unitarias de wire/goldens, E2E de replay/revocación. | working-tree / CP-02 |
| T-06 | Verificada | `PolicyApprovalWorkflowContractE2ETests` (3 casos: recorrido real Policy→adapter→decisión→verifier→reevaluación con replay y revocación, owner ausente `503` sin caso, bundle sin proyección material `503` sin caso), `PolicyExceptionHealthCheck` y `docs/approval-operations.md`. Suite final: build 0 errores, Unit **142/142**, Integración **50/50**, E2E **17/17**. | working-tree / CP-03 |

## Checkpoints

- **CP-01 — Frontera Policy→Workflow (T-01/T-02/T-03).** `decision-scope/v1` canónico, proyección
  material `policy-approval-target/v1`, adapter exact-one con tabla cerrada de efectos y owners,
  DAG por fase/target. Árbol probado: `dotnet build` 0 errores, Unit 142/142.
- **CP-02 — Extensión, verifier y operación (T-04/T-05).** Persistencia de
  `policy-exception-request/v1` y `policy-exception-verification`, comando workload-only,
  endpoint service-JWT con lookup server-side, revocación/vigencia y prohibición de carry-forward.
  Árbol probado: Integración 50/50, E2E del recorrido real y de owner ausente.
- **CP-03 — Recorrido real y operación (T-06).** E2E nombrado con tres casos, health y documentación
  operativa. Árbol probado: build 0 errores, Unit 142/142, Integración 50/50, E2E 17/17.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | `PolicyModelTests.Approval_decision_scope_must_be_canonical_decision_scope_v1` rechaza tokens/extra/no canónico; `PolicyExceptionGoldenTests` fija el target material; `PolicyApprovalWorkflowContractE2ETests` evalúa, crea caso una vez, replay devuelve el mismo caso y la misma key con otro carga da `409`; bundle sin proyección material `503` sin caso. | Revisor independiente |
| CA-02 | Cumplido | `PolicyApprovalAdapterTests.Mapping_table_projects_every_effect_or_fails_closed` y `Unknown_control_or_phase_fails_closed_and_block_never_submits`; `A_bundle_without_a_material_projection_cannot_open_a_case`; `Missing_owner_workload_blocks_the_adapter_submission_without_a_case` (owner ausente `503`, owner exacto deja prerequisite `WAITING`). | Revisor independiente |
| CA-03 | Cumplido | `PolicyApprovalAdapterTests.Automatic_controls_never_become_human_tasks` y `Prerequisite_parameters_preserve_every_policy_parameter`; el E2E comprueba owner `issuer + client_id`, parámetros/digest de control y bloqueo de completion (`WAITING`). | Revisor independiente |
| CA-04 | Cumplido | `PolicyApprovalAdapterTests.Dependencies_follow_stage_and_confirmed_targets` verifica edges `Approval|External` completos por target y paralelismo intra-fase; el E2E confirma `DEPARTMENT` + prerequisite `PRE_PROCUREMENT`. | Revisor independiente |
| CA-05 | Cumplido | `PolicyApprovalWorkflowContractE2ETests` rechaza usuario en la submission de excepción (`403`), exige role/authority, cobertura por identidad/version/digest y exclusión de requester/originator/workload; `PolicyExceptionGoldenTests.Binding_rejects_repeated_lines_and_invalid_reduction`. | Revisor independiente |
| CA-06 | Cumplido | `HttpQuotationWaiverVerifierTests` (payload exacto, service JWT, incompleto/malformado/5xx fail-closed, `404/409/422` sin autoridad) y `PolicyApprovalWorkflowContractE2ETests` que atraviesa evaluación→caso→decisión→endpoint real→reevaluación sin verifier controlado. | Revisor independiente |
| CA-07 | Cumplido | `PolicyExceptionGoldenTests` reproduce byte a byte los tres vectores propios (target/binding/evidence); el E2E verifica replay idéntico (una sola verificación y una sola reevaluación) y conflicto al reutilizar nonce/decisión tras revocación. | Revisor independiente |
| CA-08 | Cumplido | `ApprovalSupersessionService` excluye requisitos con extensión del carry-forward; el E2E revoca la evidencia y comprueba que la verificación futura falla sin reducir el control ni reescribir historia; `PolicyExceptionHealthCheck` distingue default-deny, owner y credencial ausentes; el E2E del caso previo cubre la ruta legacy `403`. | Revisor independiente |

## Desviaciones y bloqueos

- Ninguno.

## Verificación independiente

> **Resultado:** Pendiente (ronda 1 BLOCK corregida; pendiente ronda diferencial)
> **Rondas:** 1/2
> **Triaje:** R7–R13 aceptados como bugs reales y corregidos con regresiones; R1–R6 permanecen resueltos.
> **Modelo efectivo:** sdd-implementation-reviewer · openai-codex/gpt-5.6-sol · effort high
> **Método:** revisión de implementación sobre el commit candidato `d15ecdd` (base `18977ee`), árbol limpio; suites reutilizadas del mismo árbol.
> **Fecha:** 2026-09-13

### Ronda 1 — BLOCK (commit d15ecdd)

- **R7 [blocker]:** la submission de excepción aceptaba `target_requirement_key` y cobertura sin cotejarlos. Corregido:
  `PolicyExceptionSubmissionService` rehidrata el bundle y exige el control `RequireQuotations` exacto, la reducción
  dentro del allowance publicado y cobertura idéntica a la proyección material; `PolicyEvaluationService` compara
  además por identidad/version/digest y no solo por id. Regresión: caso adulterado en
  `PolicyApprovalWorkflowContractE2ETests` (`409`).
- **R8 [blocker]:** prerequisites incompletos. Corregido: budget exige Cost Centers, importe y moneda y proyecta
  `cost_center_refs` versionados desde el snapshot; supplier exige exactamente una referencia `SUPPLIER` versionada y
  proyecta `supplier_ref`; sin datos, falla cerrado. Regresión: `Incomplete_automatic_controls_fail_closed`.
- **R9 [blocker]:** todas las aprobaciones humanas eran `DEPARTMENT`. Corregido: la fase se deriva del rol
  (Department → `DEPARTMENT`; Finance/IT/Legal → `PRE_PROCUREMENT`; Procurement → `PROCUREMENT`) manteniendo el DAG por
  targets. Regresión: `Approval_phase_follows_the_role_and_procurement_waits_for_earlier_stages`.
- **R10 [blocker]:** el wire no era estricto. Corregido: `PolicyExceptionTarget` declara `type`, `id`, `version` y
  `material_snapshot_digest` y oculta `canonicalIdentity`; el verifier exige `correlation_reference` opaca.
  Regresión ampliada en `HttpQuotationWaiverVerifierTests.Request_payload_is_the_exact_wire_contract`.
- **R11 [blocker]:** `TokenResponse` no mapeaba `access_token`/`expires_in`. Corregido con `JsonPropertyName`; el
  E2E conserva la sustitución de credencial como configuración de entorno, no como contrato.
- **R12 [blocker]:** faltaba FK al requirement y el `Down` era destructivo. Corregido: FK
  `PolicyExceptionRequests.RequirementId → ApprovalRequirements.Id` (migración
  `Spec05PolicyExceptionRequirementFk`) y `Down` lanza `NotSupportedException` en ambas migraciones de SPEC 05.
- **R13 [blocker]:** el nonce se publicaba en trazas y la respuesta no verificada se logueaba con payload. Corregido:
  la actividad usa la correlation real y el verifier registra solo el resultado.

Árbol corregido probado: build 0 errores, Unit **142/142**, Integración **50/50**, E2E **17/17**.
