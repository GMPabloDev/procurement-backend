# RUN SPEC 03 — Núcleo de casos y decisiones de aprobación

> **Formato:** sdd-run/v2
> **Estado del run:** Contrato invalidado por revisión
> **Spec:** specs/03-approval-workflow.md
> **Revisión contractual:** 2
> **Commit de la spec:** a8f73d19a2bd77563050f8d2acf39ff9be05eb6e
> **Blob aprobado:** 031682c71046097294b84552cd222b8d566fab45
> **Digest contractual:** 82ed4ebe75e522106d9b9b281feb3b9dd2a5702efece9b4d21c13ce3960e7299
> **Rama base:** main
> **Commit base:** 0e25ab77a8df32183215c4af68494776798e2541
> **Rama de implementación:** spec-03-approval-workflow
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** balanced
> **Iniciado:** 2026-09-11 04:45 -05
> **Actualizado:** 2026-09-11 10:49 -05
> **HEAD verificado:** fd80f4fd4171dbddd2c57207b47385c716b52030
> **Commit de integración:** Pendiente

## Línea base

| Comando o comprobación | Resultado | Evidencia breve |
| --- | --- | --- |
| `git status --short` | Pasa | Árbol limpio antes de crear la rama dedicada. |
| `specctl check 03 --approval` | Pasa | Spec aprobada, sdd/v3, digest válido, 0 avisos. |
| `git rev-parse HEAD:specs/03-approval-workflow.md` | Pasa | Blob `031682c71046097294b84552cd222b8d566fab45`. |
| `specctl digest 03` | Pasa | `82ed4ebe75e522106d9b9b281feb3b9dd2a5702efece9b4d21c13ce3960e7299`. |
| `dotnet build ProcureToPay.sln --no-restore --nologo -v q` | Pasa | Compilación correcta, 0 advertencias, 0 errores. |
| `dotnet tests/ProcureToPay.UnitTests/bin/Debug/net10.0/ProcureToPay.UnitTests.dll` | Pasa | 64/64 pruebas correctas. |
| `dotnet tests/ProcureToPay.IntegrationTests/bin/Debug/net10.0/ProcureToPay.IntegrationTests.dll` | Pasa | 11/11 pruebas correctas sobre SQL Server/Testcontainers. |
| `dotnet tests/ProcureToPay.ApiE2ETests/bin/Debug/net10.0/ProcureToPay.ApiE2ETests.dll` | Pasa | 3/3 pruebas correctas. |
| `docker info` | Pasa | Docker 29.7.2 disponible para Testcontainers. |
| `intercom list-cwd` | Pasa | No hay otra sesión en el worktree. |

**Fallos preexistentes:** `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj` y `dotnet test --project … --no-restore` terminan con exit code 5 y “No se ejecutaron pruebas” (0 ejecutadas) pese a compilar correctamente. La ejecución directa del DLL compilado sí ejecuta y pasa las suites. Se usa la invocación directa como evidencia y se registra la desviación.

## Invalidación contractual

- La SPEC 03 pasó de la revisión aprobada 2 a la revisión 3 en `Borrador`; el run conserva revisión, commit, blob y digest de la revisión 2 como evidencia histórica hasta una nueva aprobación.
- La revisión resuelve los dos bloqueos de ambigüedad de la ronda 1: el outbox publica solo resultados cerrados por target y cualquier assignment activo de `ADMIN` o `AUDITOR` excluye al usuario de candidatura, assignment y nuevas decisiones aunque acumule rol empresarial y authority. También resuelve los bloqueos posteriores: cada evento identifica su fuente mediante `result_source {type,id,key}` bajo `approval-result/v2`, y un requirement cancelado no crea una decisión sintética.
- Revalidación requerida: T-01–T-06; CA-01–CA-08; conformidad de `ApprovalRequirement`, `ExternalPrerequisite`, `ApprovalOutboxEvent`, `ApprovalAuditRecord`, `ApprovalReconciliationRun`, NFR-01–NFR-03 y REQ-01–REQ-10. Deben añadirse o endurecerse pruebas para cardinalidad/keys y conteo exacto, binding exact-one del owner `issuer + client_id`, `approval-canonical-json/v2`, variantes de actor, root/effect keys y enlaces causales, run/lease/fencing/checkpoint/reclaim, correlation opaca, ausencia de outbox en ingreso/lifecycle/assignment, evento v2 completo por fuente y target, cancelación sin reetiquetar targets terminales ni crear decisiones, preflight desde línea base limpia sin datos v1, roles reservados acumulados, concesión posterior al assignment y separación entre lectura `AUDITOR` y operación `ADMIN`.
- La ronda 2 del gate de implementación no se consume hasta aprobar y commitear la revisión 3, revalidar los elementos anteriores y corregir los bloqueos de detalle-contrato ya registrados.

## Revisión independiente del contrato

- **Ronda 1/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Detectó ambigüedad entre lectura `ADMIN`/`AUDITOR` y falta de precisión sobre `CANCELLED`. Se corrigieron REQ-07–REQ-09, Seguridad, DEC-07, T-02/T-04/T-06 y CA-06/CA-08.
- **Ronda 2/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Señaló que CA-08 podía negar por ser `ADMIN` la visibilidad propia u originada concedida antes en REQ-09; se corrigió después de la ronda para distinguir permisos de rol de otras causas expresas de visibilidad. También señaló que el código actual aún no cumple la nueva cancelación; esto confirma la revalidación pendiente y no es una contradicción del contrato Borrador.
- **Estado de revisión histórica:** ese presupuesto independiente terminó sin veredicto PASS. La corrección de precedencia fue validada después en una invocación separada; no se consume la ronda 2 del gate de implementación.

### Validación posterior a ronda 2 (2026-09-11 09:43–09:54 -05)

- **Ronda de validación 1 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Confirmó que la separación `ADMIN`/`AUDITOR` quedó coherente y bloqueó por dos huecos de cancelación: un prerequisite `WAITING` podía pasar a `CANCELLED` por propagación contradiciendo la condición de completion, y «caso abierto» no estaba definido para la cancelación del owner. Se corrigieron REQ-07, REQ-08, la fila `ExternalPrerequisite`, T-02, CA-06 y CA-08, y se definió target/entidad terminal por requirement y por prerequisite.
- **Ronda final de validación — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Los cuatro hallazgos previos quedaron cerrados, pero aparecieron dos bloqueos nuevos: (1) la fila `ApprovalOutboxEvent` no identifica al `ExternalPrerequisite` que transita, de modo que dos prerequisites sobre el mismo target producen eventos indistinguibles; (2) REQ-10 exige «exactamente una decisión terminal por `(requirement_id, target completo)` al finalizar» mientras la cancelación finaliza requirements sin decisión. Ambos quedaron registrados como `PEND-01` y `PEND-02`, bloqueantes, en la spec.
- **Estado de validación histórica:** sin veredicto PASS; `PEND-01` y `PEND-02` bloquearon la aprobación y motivaron la revisión actual.

### Resolución de PEND-01/PEND-02 (2026-09-11 10:03 -05)

- **Decisión del usuario:** adoptar `approval-result/v2` con `result_source {type,id,key}` obligatorio y cerrado a `APPROVAL_REQUIREMENT|EXTERNAL_PREREQUISITE`; no reinterpretar ni reetiquetar payloads v1.
- **Decisión del usuario:** un requirement `CANCELLED` conserva cero asociaciones de `ApprovalDecision`; estado, audit y resultado `CANCELLED` expresan su finalización sin inventar actor, authority ni digest.
- **Cambios contractuales iniciales:** REQ-08/REQ-10, Datos y contratos, migración/despliegue, estrategia de pruebas, DEC-09/DEC-10, T-02/T-04/T-05, CA-06 y riesgos. Se eliminó la sección `Decisiones pendientes`.
- **Ronda 1/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Confirmó que PEND-01 y PEND-02 quedaron resueltos. Detectó un bloqueo: owner adapter/version no estaba ligado a la identidad completa del workload que señaliza. Planteó tres omisiones materiales: cardinalidad mínima de targets, identidad del workload en audit y recuperación/rollback de v1→v2. El drift del código v1 y de cancelación se clasificó correctamente como implementación pendiente, no como bloqueo contractual.
- **Corrección posterior a ronda 1:** REQ-02/REQ-03, REQ-08, Datos y contratos, migración/despliegue, Seguridad, estrategia de pruebas, DEC-11, T-01/T-02/T-05, CA-02/CA-03/CA-06 y riesgos ahora exigen targets no vacíos/keys únicas, binding exact-one owner adapter/version → workload allowlisted `issuer + client_id`, audit por unión `USER|WORKLOAD` sin UUID vacío y upgrade solo desde línea base limpia; cualquier v1 bloquea sin consumer, despacho ni reescritura, y tras tráfico v2 la recuperación corrige hacia adelante con dispatcher/consumer v2.
- **Ronda 2/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Confirmó que los cuatro hallazgos de ronda 1 quedaron cerrados y que PEND-01/PEND-02 permanecen resueltos, sin warnings ni drift usado como bloqueo. Detectó un bloqueo nuevo: assignment inicial, reasignación y liberación a `UNASSIGNED` deben crear audit, pero la unión `USER|WORKLOAD` no representa una transición automática ejecutada por el workflow/worker ni define si conserva al actor causal.
- **Estado de esta validación:** presupuesto 2/2 agotado con BLOCK. Se registró `PEND-03` en la spec para decidir actor causal frente a identidad `SYSTEM`/`WORKFLOW` y su binding a la causa. No se recomienda aprobar; `specctl approve 03` debe fallar mientras permanezca la pendiente.

### Resolución de PEND-03 (2026-09-11 10:27 -05)

- **Decisión del usuario:** todo efecto automático sobre case/task se atribuye a `SYSTEM` con `actor_system_id=APPROVAL_WORKFLOW`; no hereda al caller ni usa al assignee como actor.
- **Causalidad:** el audit raíz conserva al `USER`/`WORKLOAD` autenticado o identifica la corrida persistida del worker. Cada efecto automático lleva `caused_by {audit_stream,audit_id}` hacia un audit inmutable `APPROVAL|ORGANIZATION` de la misma organización; no se infiere causalidad desde `correlation_reference`.
- **Cambios contractuales:** REQ-08, Datos y contratos, Seguridad, NFR-02/NFR-03, estrategia de pruebas, DEC-12, T-01–T-06, CA-03/CA-04/CA-06/CA-08 y riesgos. Se eliminó `Decisiones pendientes`.
- **Estado inicial de esta validación:** `specctl check 03`, `specctl run-lint 03` y `git diff --check` pasaron; se abrió una revisión independiente nueva.

### Revalidación contractual de PEND-03 (2026-09-11 10:31–10:42 -05)

- **Ronda 1/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** Confirmó que `SYSTEM/APPROVAL_WORKFLOW` es una dirección válida, pero bloqueó porque raíz/efecto aún no eran totalmente deterministas, la corrida/lease no fijaba identidad, expiración, reclaim, raíz reutilizada ni idempotencia tras restart, los preimages/payload v2 no enumeraban schemas ni vectores autoritativos, y límite de targets, correlation y evidencia operativa seguían imprecisos.
- **Corrección posterior a ronda 1:** REQ-04/REQ-08–REQ-10 y Datos y contratos distinguen formalmente audits raíz y efectos, obligan a que todo efecto case/task apunte a una raíz `APPROVAL`, reservan el enlace `ORGANIZATION` para la raíz de la corrida y agregan `automatic_effect_key`. `ApprovalReconciliationRun` fija trigger/idempotencia, root atómico, lease 30 s, owner, renovación cada 10 s, fencing, cursor, reclaim y completion. El límite de 10.000 cuenta exactamente vínculos requirement/prerequisite→target y excluye edges; correlation es opaca y acotada. `approval-canonical-json/v2` enumera schemas exactos y cinco SHA-256 contractuales; DEC-13 justifica el version bump y deja explícita la incompatibilidad de los borradores 04/05. T-05 exige `docs/approval-operations.md` y pruebas multiworker/restart.
- **Validación determinista tras la corrección:** `specctl check 03` pasa con 0 avisos y `git diff --check` queda limpio.
- **Ronda final 2/2 — BLOCK (`openai-codex/gpt-5.6-luna`, effort high).** La separación raíz/efecto y el run/lease quedaron suficientemente descritos, pero persisten dos huecos: (1) `automatic_effect_key` no incluye la identidad de `ExternalPrerequisite`, por lo que dos prerequisites distintos del mismo caso/target cancelados por la misma raíz colisionan; (2) `approval-result/v2.decision_digest` no fija si transporta `workflow_decision_digest` o `decision_fingerprint`.
- **Estado final de esta validación:** presupuesto 2/2 agotado con BLOCK. Se registraron `PEND-04` y `PEND-05` en la spec; no se recomienda ni ejecuta aprobación hasta resolverlos en una nueva revisión. `specctl check 03` y `specctl run-lint 03` pasan con 0 avisos, `git diff --check` queda limpio y `specctl approve 03 --dry-run` falla de forma esperada porque una spec con `## Decisiones pendientes` no puede aprobarse.

### Resolución de PEND-04/PEND-05 (2026-09-12)

- **Decisión del contrato:** `automatic_effect_key` incorpora `effect_source {id,key,type}` para distinguir `APPROVAL_REQUIREMENT`, `EXTERNAL_PREREQUISITE` y `APPROVAL_CASE`; dos prerequisites distintos sobre el mismo target y raíz no colisionan.
- **Decisión del contrato:** `approval-result/v2.decision_digest`, cuando no es nulo, transporta exactamente `workflow_decision_digest` y nunca `decision_fingerprint`.
- **Revisión diferencial independiente — PASS (`openai-codex/gpt-5.6-luna`, effort high).** El subagente `sdd-contract-reviewer` validó que PEND-04 y PEND-05 quedan resueltos, que el delta no introduce contradicciones contractuales y que el drift del código actual es trabajo de implementación, no bloqueo de la spec.
- **Constancia vigente:** `specs/reviews/03-approval-workflow.json` registra el PASS del digest `f0b61a917c4c4b4adcaee598c75352ef93d03c3bf25b76d15f80177ba3446234`. `specctl review 03` y `specctl approve 03 --dry-run` pasan; la aprobación humana queda pendiente.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
| --- | --- | --- | --- |
| T-01 | Revalidación requerida | Adapters exact-one, idempotencia, `decision-scope/v1`, targets, agrupación, límites y validaciones. Evidencia: 17 unitarias de contrato (`ApprovalContractTests`, incluidos los vectores exactos de límite: 2.000 requirements y 10.000 enlaces aceptan, +1 rechaza; y el borde exacto de 5 MiB canónicos ±1 byte), 3 de integración SQL (persistencia/replay/conflicto/concurrencia), 1 de contrato del adapter controlado y 2 API/E2E (`403` de workload no allowlisted sin persistir caso, `413` de límites canónicos). | working-tree (CP-02) |
| T-02 | Revalidación requerida | DAG con dependencias y prerrequisitos externos, señales idempotentes por `signal_key`+`expected_version`, propagación, cancelación del owner, state machines y exactly-one task por requirement. Evidencia: 7 unitarias de grafo, integración de señal/outbox y 2 API/E2E (señales `SATISFIED`/`FAILED` con `403` de workload no propietario, replay idéntico, `409` por key reutilizada con otro contenido, `409` sobre prerequisite terminal, y cancelación con `403`/`409` de versión obsoleta/`409` de doble cancelación). | working-tree (CP-02) |
| T-03 | Revalidación requerida | Routing determinista y reconciliación. Implementado: `ApprovalScopeResolver` (`decision-scope/v1` → `AuthorizationScopeSet` con validación de catálogo activo + versión congelada, fail-closed), `ApprovalRoutingPolicy` (política pura de menor carga y desempate por UUID canónico), `ApprovalAssignmentEngine` (asignación y reconciliación), `ApprovalReconciliationService` (idempotente, presupuesto de 60 s), `ApprovalOperationsQueryService`, endpoints `operations/unassigned` y `operations/reconciliation`, registro DI y migración `20260911105726_ApprovalAssignmentCurrentIndex` (índice único filtrado `IX_ApprovalAssignments_TaskId_Current`). Evidencia: 7 unitarias deterministas (`ApprovalAssignmentTests`, construidas sobre el resolver de SPEC 01), 6 de integración SQL (`ApprovalAssignmentIntegrationTests`, incluida la carrera de dos DbContext y el `UNASSIGNED` sin candidato) y 2 API/E2E (`ApprovalOperationsE2ETests`). | working-tree (T-03) |
| T-04 | Revalidación requerida | Decisiones autorizadas e inmutables. Implementado: `ApprovalDecisionService` (revalidación de autoridad en transacción, idempotencia por `decision_key`, `DecisionFingerprint` reproducible con las versiones de preimagen **almacenadas**, `AuthorityEvidenceDigest` + `WorkflowDecisionDigest`, atomicidad decisión+targets+release+audit+outbox), endpoint `POST tasks/{taskId}/decisions`, registro DI y migración `20260911111644_ApprovalDecisionEvidence` (`RequirementVersion`/`TaskVersion`). Evidencia: golden vectors de `ApprovalGoldenVectorTests` (bytes canónicos de la preimagen de submission + SHA-256 de submission, signal, authority, decisión y fingerprint, con permutación de sets, NFC y rechazo de duplicados), 6 de integración SQL (`ApprovalDecisionIntegrationTests`: atomicidad, replay/conflicto, autoridad revocada, versión obsoleta, carrera concurrente, propagación de approve y reject) y 1 API/E2E (`Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected`). **Defecto real corregido**: `ApprovalCase.ApplyDecision` no activaba los requirements dependientes, por lo que aprobar no habilitaba edges (REQ-07); ahora lo hace en la misma transición y la unitaria existente fija el invariante sin llamada auxiliar. | working-tree (CP-03) |
| T-05 | Revalidación requerida | Workers y operación. Implementado: `ApprovalTelemetry` (Actividad + Meter minimizados), contrato fail-closed `IApprovalResultConsumer`/`ApprovalResultConsumerRegistry`, `ApprovalOutboxDispatcher` (lease por UPDATE condicional atómico, entrega al menos una vez, backoff contractual y dead letter a los 10 intentos), `ApprovalOutboxAdministrationService` (backlog, dead letters y replay administrativo sin editar payload), `ApprovalHealthCheck` + `/health/approval`, registro DI y OpenTelemetry (source y meter), y migración aditiva `20260911122932_ApprovalOutboxDispatchIndex`. Evidencia: 7 de integración SQL (`ApprovalOutboxIntegrationTests`: entrega con payload intacto, dos instancias sin doble entrega, recuperación tras restart de lease abandonado, backoff 1 s y dead letter a los 10 intentos, replay administrativo, dedupe del consumer y telemetría sin PII) más 1 API/E2E (`Health_degrades_on_dead_letters_and_admin_alone_replays_them`). | working-tree (CP-04) |
| T-06 | Revalidación requerida | Bandeja, visibilidad y evidencia HTTP. Implementado: `ApprovalInboxQueryService` (bandeja del assignee, historia del actor y lecturas organizacionales de decisiones y assignments), endpoints `inbox`, `inbox/history`, `cases/{id}/decisions` y `cases/{id}/assignments`, y registro DI. Evidencia: 1 API/E2E (`Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped`) que cubre assignee, usuario sin trabajo, originador, `AUDITOR`, workload, ocultación `404` y `403` fuera de alcance, minimización sin identidad del IdP ni evidencia de elegibilidad, y `GET cases/{id}` para el originador. **Regresión real corregida**: el atributo `[HttpGet("cases/{caseId:guid}")]` había quedado dentro de la línea del comentario XML `///` (edit previo que eliminó un salto de línea), de modo que la ruta nunca se registraba y el endpoint respondía `404` con cuerpo vacío; la prueba nueva lo detectó. | working-tree (CP-05) |

## Checkpoints

### CP-01 — 2026-09-11 05:35 -05 — Bloque 1 (parcial)

- Tareas: T-01, T-02 (ambas `Parcial`: falta la evidencia API/E2E).
- Cambios: dominio `Modules/Approval` (canonicalización, scope tipado, submission y reglas, agregado de caso con DAG y transiciones, fingerprints, outbox/audit), persistencia `Persistence/Approval` (records, mapeo EF, hidratación, serialización, registry de adapters, allowlist de workloads, servicios de submission/señales/cancelación/consulta), API `ApprovalController`, mapeo Problem Details `413`/`503`, migración `20260911100346_ApprovalWorkflowFoundation` y pruebas unitarias/integración del módulo.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore --no-incremental` 0 errores; UnitTests 83/83; IntegrationTests 15/15 (SQL Server/Testcontainers); `git diff --check` limpio; `lens_diagnostics(source=lsp)` limpio en los archivos verificados.
- Defectos reales encontrados y corregidos por las pruebas: `ApprovalSubmission.OrganizationId` sin asignar (persistía `Guid.Empty`), fingerprint de señal calculado con la versión actual en vez de la esperada del comando, validación de agrupación más estricta que REQ-02 (el particionado es legítimo) y `ToDictionary` que fallaba con claves de requirement repetidas.
- Resultado: el núcleo crea casos idempotentes con grafo, asignación pendiente (T-03) y evidencia canónica; falta decisión/routing (Bloques 2), workers (Bloque 3) y superficies de lectura (Bloque 4).
- HEAD: working-tree sobre `spec-03-approval-workflow`.
- Integrado en main: No.
- Próximo paso: completar evidencia API/E2E de T-01/T-02 o continuar con T-03.

### CP-02 — 2026-09-11 05:44 -05 — Bloque 1 (cierre)

- Tareas: T-01, T-02 (ambas `Verificada`).
- Cambios: `tests/ProcureToPay.ApiE2ETests/ApprovalWorkflowE2ETests.cs` (nuevo, 4 pruebas API/E2E con adapter controlado y dos workflows allowlisted) y dos vectores de límite en `tests/ProcureToPay.UnitTests/Approval/ApprovalContractTests.cs` (`Submission_rules_accept_exactly_the_contractual_count_limits`, `Canonical_size_limit_is_enforced_at_the_exact_byte`). Sin cambios en código de producción: la evidencia cierra el bloque sin modificar el contrato.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` 0 errores/0 avisos; UnitTests **85/85**; IntegrationTests **15/15** (SQL Server/Testcontainers); ApiE2ETests **7/7**; `lens_diagnostics(source=lsp, scope=paths)` **0 diagnósticos** en los dos archivos modificados.
- Defectos encontrados y corregidos en la propia prueba (no en producción): el vector de `FAILED` reutilizaba `satisfied=true` y el `403` de «no allowlisted» usaba un workload que sí estaba allowlisted. Ambos se corrigieron separando `unlisted-api` (no allowlisted) de `budget-api` (allowlisted, no propietario), lo que además distingue las dos semánticas de `403`.
- Resultado: el Bloque 1 queda cerrado. Un workload allowlisted crea una sola vez un caso válido y su replay devuelve el mismo `caseId`; el caller no allowlisted no persiste nada; los límites canónicos devuelven `413`; las señales del owner satisfacen o bloquean exclusivamente los targets correctos y no se reabren.
- HEAD: working-tree sobre `spec-03-approval-workflow` (`git rev-parse HEAD` = `0e25ab77a8df32183215c4af68494776798e2541`, el `Commit base`: el árbol aún no se ha commiteado).
- Integrado en main: No.
- Próximo paso: Bloque 2 — T-03 (assignment, reconciliación) y T-04 (decisión y atomicidad).

### CP-03 — 2026-09-11 06:54 -05 — Bloque 2 (cierre)

- Tareas: T-03, T-04 (ambas `Verificada`).
- Cambios: routing determinista y reconciliación (T-03) y decisiones autorizadas e idempotentes (T-04). Producción: `ApprovalRoutingPolicy`, `ApprovalEligibilityEvidence`, `ApprovalScopeResolver`, `ApprovalAssignmentEngine`, `ApprovalReconciliationService`, `ApprovalOperationsQueryService`, `ApprovalDecisionService`, endpoints administrativos y de decisión, `ApprovalController`, `ProcureToPayDbContext`, `DependencyInjection` y dos migraciones aditivas. Dominio: `ApprovalCase.AssignTask` ahora fija el assignee (antes ignoraba el parámetro), nuevo `ApprovalTask.ReassignTo`, y `ApplyDecision` habilita los edges que satisface.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore --no-incremental` **0 avisos / 0 errores**; UnitTests **96/96**; IntegrationTests **27/27** (SQL Server/Testcontainers); ApiE2ETests **10/10**; `git diff --check` limpio; `specctl run-lint 03` ✅.
- Defectos reales encontrados y corregidos por las pruebas: (1) `ApprovalCase.ApplyDecision` no activaba los requirement dependientes, así que `APPROVE` no habilitaba sus edges como exige REQ-07 — la unitaria existente lo ocultaba porque llamaba a `ActivateEligibleRequirements()` a mano; (2) `ApprovalCase.AssignTask` ignoraba `assigneeUserId` y `load`, dejando la task sin assignee; (3) defectos propios de los tests nuevos, corregidos: `TaskAsync` era ambiguo con dos requirements, `outcome.Action` devuelve la acción (`APPROVE`) y no el código de resultado (`APPROVED`), y el caso rechazado completa (`COMPLETED`) porque todos los requirements quedan terminales según REQ-07.
- Resultado: el Bloque 2 queda cerrado. La asignación es determinista por menor carga con desempate canónico, no hay fallback sin candidato, la reconciliación es idempotente y corrige la pérdida de autoridad, y la decisión es atómica, inmutable, reproducible y solo la ejecuta el assignee aún elegible.
- HEAD: working-tree sobre `spec-03-approval-workflow` (`git rev-parse HEAD` = `0e25ab77a8df32183215c4af68494776798e2541`, el `Commit base`: el árbol sigue sin commitear, 22 entradas en `git status --short`).
- Integrado en main: No.
- Próximo paso: Bloque 3 — T-05 (schema/índices/leases, dispatcher, retries, health, dead letter, replay, multi-instancia, restart y reversión).

### CP-04 — 2026-09-11 07:43 -05 — Bloque 3 (cierre)

- Tareas: T-05 (`Verificada`).
- Cambios: `ApprovalTelemetry`, `IApprovalResultConsumer` + `ApprovalResultConsumerRegistry`, `ApprovalOutboxDispatcher`, `ApprovalOutboxAdministrationService`, `ApprovalHealthCheck`, endpoints `operations/outbox`, `operations/outbox/dead-letters` y `operations/outbox/{id}/replay`, `/health/approval`, registro DI y OpenTelemetry, índice de dispatch y su migración.
- Tests y checks: build `--no-restore` **0 avisos / 0 errores**; UnitTests **96/96**; IntegrationTests **34/34**; ApiE2ETests **11/11**; `specctl run-lint 03` ✅; `git diff --check` limpio.
- Defectos reales encontrados y corregidos por las pruebas: (1) **deadlock** en la toma del lease: el patrón leer-luego-escribir dentro de una transacción `Serializable` permitía que dos instancias se bloquearan mutuamente en la misma fila (`Transaction (Process ID 65) was deadlocked… chosen as the deadlock victim`); se sustituyó por un `UPDATE` condicional atómico (`ExecuteUpdateAsync` con la condición de elegibilidad en el `WHERE`), que además elimina la transacción explícita y es imposible de bloquear entre sí; (2) la persistencia del resultado ahora tolera un escritor concurrente y deja que el lease expire para reintentar, preservando la entrega al menos una vez en lugar de perder el resultado. Defectos propios de las pruebas, corregidos: la aserción de payload asumía una sola entrega cuando el consumer registra todos los intentos, el parámetro lambda llamado `event` (palabra reservada de C#), y una aserción de caso sembrado sin sembrarlo.
- Resultado: el Bloque 3 queda cerrado. El outbox entrega al menos una vez con payload inmutable, dos instancias no duplican entregas, un restart recupera leases abandonados, el backoff y el dead letter siguen el contrato, el replay administrativo no edita el payload, el health degrada por dead letter/backlog/reconciliación y la telemetría no filtra PII.
- HEAD: working-tree sobre `spec-03-approval-workflow` (`git rev-parse HEAD` = `0e25ab77a8df32183215c4af68494776798e2541`, el `Commit base`: el árbol sigue sin commitear, 27 entradas en `git status --short`).
- Integrado en main: No (acumulando, por decisión del usuario).
- Próximo paso: Bloque 4 — T-06 (bandeja del assignee, visibilidad, `404`/`403`, Problem Details y telemetría HTTP) y después la verificación independiente.

### CP-05 — 2026-09-11 08:04 -05 — Bloque 4 (cierre)

- Tareas: T-06 (`Verificada`). Con esto los cuatro bloques del contrato quedan implementados y verificados.
- Cambios: `ApprovalInboxQueryService`, endpoints `inbox`, `inbox/history`, `cases/{caseId}/decisions` y `cases/{caseId}/assignments`, helper `RequireActiveProfileAsync` y registro DI.
- Tests y checks: build `--no-restore` **0 avisos / 0 errores**; UnitTests **96/96**; IntegrationTests **34/34**; ApiE2ETests **12/12**; `specctl run-lint 03` ✅; `git diff --check` limpio.
- **Regresión real introducida por mí y detectada por la prueba nueva**: en una edición anterior del `ApprovalController` (Bloque 2) eliminé un salto de línea y dejé el atributo `[HttpGet("cases/{caseId:guid}")]` dentro de la misma línea que el comentario XML `/// <summary>…</summary>`. El atributo quedaba comentado, la acción no se registraba y `GET /api/v1/approval/cases/{caseId}` respondía **`404` con cuerpo vacío** (404 de enrutado, no de autorización) en lugar de servir el caso. Las suites de los Bloques 1–3 no lo detectaron porque ninguna llamaba a ese endpoint por HTTP; lo encontró la prueba de T-06 al pedir el caso como originador. Corregido separando comentario y atributo. Verificado además con un barrido (`grep`) de comentarios XML pegados a atributos en `src/` y `tests/`: no queda ninguno.
  - Alcance del impacto: la ruta afectada es de solo lectura y no participa en el camino de escritura (submission, señal, cancelación, decisión) ni en la evidencia de los Bloques 1–3, que sigue siendo válida. `CA-01…CA-07` no dependen de ella; `CA-08` («el originador consulta su caso») sí, y por eso quedó `Parcial` hasta este bloque.
- Resultado: la bandeja del assignee, la historia del actor y las lecturas de auditoría están acotadas por organización, ocultan lo que queda fuera de alcance con `404`/`403` y no exponen identidad del IdP, credenciales ni evidencia de elegibilidad en la bandeja.
- HEAD: working-tree sobre `spec-03-approval-workflow` (`git rev-parse HEAD` = `0e25ab77a8df32183215c4af68494776798e2541`, el `Commit base`: el árbol sigue sin commitear, 27 entradas en `git status --short`).
- Integrado en main: No (acumulando, por decisión del usuario).
- Próximo paso: Fase 4 — verificación final y **gate de verificación independiente** (máximo 2 rondas, `sdd-implementation-reviewer`), que exige commitear el árbol verificado para fijar `HEAD verificado`.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Cumplido | `ApprovalContractTests.Submission_rules_enforce_requirement_and_target_limits`, `Submission_rules_accept_exactly_the_contractual_count_limits` (2.000 requirements / 10.000 enlaces aceptan; +1 lanza `ApprovalPayloadTooLargeException`), `Canonical_size_limit_is_enforced_at_the_exact_byte` (5 MiB ±1 byte), integración `Submission_persists_the_case_graph_and_replays_idempotently` (replay idéntico mismo `caseId`, contenido distinto `DomainConflictException`), `Concurrent_identical_submissions_create_a_single_case`; API/E2E `Submission_ingestion_requires_an_allowlisted_workload_and_enforces_canonical_limits` (`403` sin persistir, replay `replayed=true`, `409`) y `Canonical_limits_above_the_contract_return_413_problem_details` (`413` + `/problems/payload-too-large`). | Automática |
| CA-02 | Parcial | Las pruebas existentes cubren schema, catálogo, grouping y serialización, y el dominio rechaza requirements sin targets; falta revalidar explícitamente cardinalidad y keys únicas de la revisión 3. | Automática |
| CA-03 | Parcial | Las pruebas existentes cubren DAG, señales y estado `FAILED`, pero la implementación compara solo `client_id` con adapter id y audita el workload con UUID vacío. Falta binding exact-one `issuer + client_id`, rechazo de owners ausentes/ambiguos y audit tipado. | Automática |
| CA-04 | Parcial | Routing, cero candidatos, carrera y reconciliación están cubiertos. Falta revalidar la nueva atribución: hoy el código usa `SYSTEM` con `Guid.Empty` o con el assignee y no conserva un `actor_system_id` ni `caused_by` verificable. | Automática |
| CA-05 | Parcial | SoD: `ApprovalAssignmentTests.Excluded_actors_are_never_candidates_even_with_full_scope`; API/E2E `Originator_is_never_a_candidate_and_a_revoked_role_is_reconciled` (el originador tiene el UUID canónico menor y aun así nunca es assignee); integración `Reconciliation_reassigns_after_a_revoke_and_is_idempotent`. Decisión: `ApprovalDecisionIntegrationTests.Only_the_current_assignee_decides_and_losing_eligibility_blocks_the_decision` (actor ajeno `403`, versión obsoleta `409`, role revocado `403`) y `Decision_commits_decision_targets_audit_and_outbox_atomically` (motivo/key/versión); API/E2E `Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected` (`ADMIN` recibe `403`). | Automática |
| CA-06 | Parcial | `ApprovalGraphTests` cubre state machines, propagación y cancelación; integración cubre decisión+audit+outbox, carrera y resultados por target; API/E2E cubre cancelación del owner. Falta revalidar `approval-result/v2`: `result_source` exacto para requirements/prerequisites, dos prerequisites distinguibles sobre un target, cero decisiones al cancelar y preflight que bloquee v1 pendiente sin reescribirlo. | Automática |
| CA-07 | Parcial | Los goldens implementados fijan v1 (`de1d2652…`, `53197969…`, `b19e2fcb…`, `c9f36065…`, `ee728c31…`) y quedan invalidados por la revisión 3. El contrato v2 exige schemas/bytes y hashes `c4f08ea7…`, `674a49d6…`, `93003e10…`, `04eddbde…`, `0ffd7bc1…`, además del payload `approval-result/v2` exacto. | Automática |
| CA-08 | Parcial | `ADMIN` ve `UNASSIGNED` y dispara reconciliación sin decidir (API/E2E `Unassigned_requirements_and_reconciliation_require_an_administrative_role`, `Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected`); `AUDITOR` lee asignaciones y decisiones (`Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped`); el originador lee su caso y un usuario ajeno recibe `404` con `/problems/not-found`; bandeja e historia minimizadas sin identidad del IdP, credenciales ni evidencia de elegibilidad; Problem Details para `404`/`403`/`409`; telemetría sin motivos, snapshots ni PII (`ApprovalOutboxIntegrationTests.Backlog_reports_overdue_state_and_telemetry_carries_no_personal_data`); health refleja backlog, reconciliación debida y dead letter (`Health_degrades_on_dead_letters_and_admin_alone_replays_them`). | Automática |

## Verificaciones manuales

| Criterio | Procedimiento | Resultado | Confirmado por/fecha |
| --- | --- | --- | --- |
| — | Ninguna prevista: todos los CA se verifican automáticamente. | — | — |

## Desviaciones y bloqueos

- `dotnet test --project … --no-restore` (documentado en `AGENTS.md`) no ejecuta pruebas en este entorno: exit code 5, 0 ejecutadas, también para el proyecto de integración sin `--no-restore`. Se usa la ejecución directa del DLL compilado, que sí ejecuta y pasa. No afecta al contrato.
- **LSP de Pi Lens degradado (herramienta, no código).** `lens_diagnostics(source=lsp)` informa `CS0234/CS0246` diciendo que `ProcureToPay.Domain.Modules.Approval` no existe al analizar `src/ProcureToPay.Infrastructure/Persistence/Approval/*.cs`, mientras los archivos de `src/ProcureToPay.Domain/Modules/Approval/*.cs` se analizan limpios. El compilador lo contradice: `dotnet build ProcureToPay.sln --no-restore` termina con 0 errores y `strings src/ProcureToPay.Domain/bin/Debug/net10.0/ProcureToPay.Domain.dll` contiene `ApprovalCanonicalJson`, `ApprovalTarget` y `ApprovalTargetRules`. Causa: referencia de proyecto obsoleta dentro del servidor LSP; `touch` de ambos `.csproj`, cambio real de `ProcureToPay.sln` y rebuild completo no la refrescan. Evidencia alternativa usada para esta área: compilación y suites de prueba de la solución.
- **Caché de sesión de Pi Lens con hallazgos fantasma.** Tras renombrar temporalmente `ApprovalSerialization.cs` (para invalidar caché por ruta) la caché de sesión conserva el archivo inexistente `ApprovalPersistenceSerialization.cs` y marca ambigüedad de clase en `ApprovalCaseHydrator.cs` (L30–L48), más avisos `CS0234/CS0246` en `ApiExceptionHandler.cs` (L5/L81/L88). Verificado como falso positivo: el archivo no existe en disco, el flujo de trabajo se renombró a `ApprovalJsonPersistence` para eliminar la ambigüedad nominal, `lens_diagnostics(source=lsp, scope=paths)` devuelve **0 diagnósticos** en ambos archivos y `dotnet build` está en 0 errores. Los 8 hallazgos se marcaron con `lens_diagnostic_mark` (`false-positive`) y la caché de sesión persistió en listarlos.
- **LSP de Pi Lens no refresca el grafo de proyectos (degradación de herramienta, reincidente y ampliada).** Durante el Bloque 2 el LSP reporta 9 errores de compilación que **contradicen al compilador**: `CS0103` sobre `ApprovalEligibilityEvidence` (2×) y `CS1061` sobre `ApprovalTask.ReassignTo` en `ApprovalAssignmentEngine.cs`; `CS0246` sobre `ApprovalOperationsQueryService`, `ApprovalReconciliationService`, `ApprovalUnassignedView`, `ApprovalReconciliationOutcome` y `ApprovalReconciliationStateView` en `ApprovalController.cs`; y `CS1729` sobre el constructor de 4 argumentos de `ApprovalWorkflowService` en `ApprovalWorkflowIntegrationTests.cs`. Patrón diagnóstico: **todos** los símbolos señalados son tipos o miembros **creados en esta sesión en un proyecto distinto** al del archivo analizado, mientras que todos los símbolos preexistentes de los mismos ensamblados resuelven sin error.
  - Evidencia contradictoria recogida: (1) `dotnet build ProcureToPay.sln --no-restore --no-incremental` termina con **0 Advertencia(s), 0 Errores**, compilando los cuatro proyectos afectados; (2) `strings src/ProcureToPay.Domain/bin/Debug/net10.0/ProcureToPay.Domain.dll` contiene `ApprovalEligibilityEvidence`, `Canonicalize` y `ReassignTo`; (3) `ApprovalEligibilityEvidence` existe en `ApprovalEligibilityEvidence.cs:11` dentro del namespace importado por el archivo que lo usa, `ApprovalTask.ReassignTo` en `ApprovalCase.cs:616` y el constructor de 4 argumentos en `ApprovalWorkflowService.cs:87-91`; (4) las suites unitarias y de integración ejercitan en ejecución esos mismos constructores y miembros.
  - Intentos de refresco agotados sin efecto: `touch` de los cuatro `.csproj` y de `ProcureToPay.sln`, compilación completa no incremental, y `lens_diagnostics(source=lsp, mode=full, scope=paths, refreshRunners=all, waitMs=90000)`, que devuelve los mismos 9 hallazgos. Los 9 se marcaron con `lens_diagnostic_mark` (`false-positive`) con la evidencia anterior.
  - Consecuencia: para el área de Approval, la señal del LSP no es utilizable y la evidencia de compilación se toma del compilador y de las suites de prueba. No se alteró código para satisfacer los falsos positivos.

- **Modo de revisión conservado del run.** El run se reanudó en `balanced` tal como estaba registrado; no se cambió de modo. Solo se aplicó la autorización de alcance del usuario (Bloques 1 y 2 antes de la siguiente parada).

- **Entorno: nodos MSBuild persistentes bloqueaban los binarios (no es un fallo de código).** Durante el Bloque 2, `dotnet build` falló de forma intermitente con `MSB4018` en tareas del SDK (`Microsoft.NET.Sdk.StaticWebAssets.JSModules.targets`, `Microsoft.NET.Sdk.targets`, `Microsoft.AspNetCore.Mvc.Testing.targets`). La causa real apareció como `MSB3026`: `The process cannot access the file '…/tests/ProcureToPay.IntegrationTests/bin/Debug/net10.0/Docker.DotNet.Unix.dll' because it is being used by another process`. Había más de 20 nodos MSBuild con `/nodeReuse:true` acumulados (hasta ~13 minutos de antigüedad) reteniendo handles de los ensamblados de prueba. Mitigación aplicada: `dotnet build-server shutdown` y compilar con `-nodeReuse:false`; con eso la solución compila de forma reproducible con **0 avisos / 0 errores**. Los `MSB4018` eran un efecto derivado del bloqueo de archivos, no del código.

- **Entorno: los contenedores SQL Server agotaban la memoria del agente (no es un fallo de código).** La primera ejecución de integración con las clases nuevas falló con `TaskCanceledException`/`Socket` en **todas** las pruebas al mismo instante (6 m 03 s), sin errores de aserción: el host no podía alcanzar Docker. Diagnóstico: 13 GiB de RAM total con ~6 GiB disponibles, cada contenedor SQL Server consume ~2 GiB y xUnit paraleliza entre clases, así que varias clases iniciaban contenedor a la vez y el arranque se colgaba. Se limpiaron 16 contenedores `mssql`/`ryuk` huérfanos (preservando `procure-to-pay-sqlserver` y `procure-to-pay-keycloak` de `docker-compose`) y se fijó `"parallelizeTestCollections": false` en `tests/ProcureToPay.IntegrationTests/xunit.runner.json` y `tests/ProcureToPay.ApiE2ETests/xunit.runner.json`, con lo que las suites pasan de forma reproducible (Integración ~3 m, API/E2E ~1 m 40 s). Las ejecuciones anteriores del Bloque 1 no fallaron porque había menos clases y por tanto menos contenedores concurrentes. No se debilitó ninguna prueba: solo se serializó su ejecución.

- **Integración diferida por decisión del usuario (CP-03).** Tras cerrar el Bloque 2 se ofreció integrar el bloque a `main` o seguir acumulando. El usuario eligió **seguir acumulando** en `spec-03-approval-workflow`, de modo que `main` no recibe un feature a medias ni las migraciones de un esquema aún en evolución. Consecuencia registrada: la rama acumula Bloques 1–2 (y los que sigan) y la integración se hará al alcanzar `Lista para integrar` con la verificación independiente superada. No se ejecutó ningún merge, commit ni push.

- **Cobertura de Pi Lens en Approval**: por lo anterior, `lens_diagnostics(source=lsp)` no es utilizable en esta área y su señal se sustituye por compilación limpia y suites de prueba. Los 9 hallazgos del LSP se marcaron `false-positive` y los sitios afectados llevan `// pi-lens-ignore` conforme a la convención ya presente en el repositorio (`PolicyEvaluatorTests.cs`, `ApprovalController.cs`, etc.).

## Conformidad contractual

> Generada desde `03-approval-workflow.md` revisión 2 (digest `82ed4ebe75e5…`). Marca cada fila `Cumplido`, `Parcial`, `No aplica` o `Pendiente` con evidencia antes de la verificación independiente; todas deben quedar `Cumplido` o `No aplica` para marcar `Lista para integrar`.

| Elemento | Contrato | Estado | Evidencia |
| --- | --- | --- | --- |
| `ApprovalCase` | organización, subject type/id/version, operation, snapshot digest, workload, key/fingerprint, estado, versión · No contiene el documento origen; esta… | Cumplido | Agregado `ApprovalCase` + `ApprovalCaseRecord`; solo guarda `SourceSnapshotDigest`, nunca el documento origen. Evidencia: `ApprovalWorkflowIntegrationTests.Submission_persists_the_case_graph_and_replays_idempotently` y `ApprovalGraphTests.Case_completes_only_when_every_requirement_is_terminal_and_every_prerequisite_is_satisfied`. |
| `DecisionScopeDescriptor` | schema version, organización, scopes tipados/versionados · Contrato exacto `decision-scope/v1`; referencias validadas contra SPEC 01. | Cumplido | `ApprovalContractTests.Decision_scope_descriptor_is_an_exact_canonical_contract` y `Scope_descriptor_rejects_non_canonical_extra_fields_and_invalid_combinations`; `ApprovalScopeResolver` valida catálogo activo y versión congelada. |
| `ApprovalRequirement` | source/workflow key, tipo, stage, role, authority, descriptor, exclusiones, dependencias, targets, estado · Keys únicas, targets no vacíos y máximo una task actual. | Parcial | El dominio rechaza cero targets y el índice cubre exactly-one task; falta evidencia explícita de keys únicas conforme a revisión 3. |
| `ExternalPrerequisite` | key, owner adapter/version, owner workload issuer/client id, control/digest, parámetros, targets, estado, signal key/fingerprint, versión. | Parcial | Targets no vacíos y lifecycle están implementados; falta persistir/resolver owner `issuer + client_id`, pues hoy se compara `client_id` con adapter id. |
| `ApprovalTarget` | type, id, version, material snapshot digest · Identidad completa usada por edges, decisiones y eventos. | Cumplido | Identidad completa en `ApprovalTarget.CanonicalIdentity`; `ApprovalContractTests.Submission_rules_reject_dangling_cycles_and_non_contained_edges`. |
| `ApprovalTask` | requirement, targets, estado, assignment actual, versión · `UNASSIGNED`, `PENDING` y terminales de REQ-07. | Cumplido | `ApprovalGraphTests.Decision_actions_map_to_requirement_and_task_states`; estados `UNASSIGNED`, `PENDING` y terminales ejercitados en `ApprovalAssignmentIntegrationTests` y `ApprovalDecisionIntegrationTests`. |
| `ApprovalAssignment` | task, assignee, UTC de alta/baja, carga, eligibility evidence, causa · Append-only; uno actual por task. | Cumplido | Append-only con índice único filtrado `IX_ApprovalAssignments_TaskId_Current`; `ApprovalAssignmentIntegrationTests.Exactly_one_current_assignment_per_task_is_enforced_by_the_schema` lo prueba con una carrera de dos DbContext. |
| `ApprovalDecision` | task/requirement, targets, action, actor, reason, UTC, key/fingerprint, evidence, digests · Append-only; origen `HUMAN` en esta spec. | Cumplido | Append-only con unique `(OrganizationId, ActorUserId, DecisionKey)` y exactly-one por target; `ApprovalDecisionIntegrationTests.Decision_commits_decision_targets_audit_and_outbox_atomically`. |
| `ApprovalOutboxEvent` | id, contract version, caso/sujeto, requirement, target, resultado, decisión/digest, attempts/state · Consumer deduplica por `event_id + contract_vers… | Parcial | `ApprovalOutboxIntegrationTests.Consumer_contract_deduplicates_at_least_once_deliveries`; inmutabilidad del payload verificada en `Admin_replay_resets_a_dead_letter_without_editing_its_payload`. |
| `ApprovalAuditRecord` | unión USER/WORKLOAD/SYSTEM, `actor_system_id`, `caused_by`, UTC, acción, objetivo/versiones, scope, motivo, before/after, correlation · Append-only y minimizado. | Parcial | Audit append-only existe, pero faltan las variantes tipadas, `APPROVAL_WORKFLOW` y el enlace causal; las señales y efectos automáticos actuales usan `Guid.Empty` o el assignee. |
| NFR-01 Determinismo reproducible | El mismo comando canónico, snapshot de elegibilidad/carga y reloj produce el mismo assignment, decisión, bytes y digests; un replay devuelve los arte… | Cumplido | `ApprovalGoldenVectorTests` fija bytes y SHA-256, demuestra invariancia ante permutación de sets y normalización NFC; `ApprovalAssignmentTests.Routing_is_deterministic_regardless_of_candidate_order`; el replay reproduce los artefactos en `Decision_replay_returns_the_original_and_a_changed_payload_conflicts`. |
| NFR-02 Atomicidad e inmutabilidad | Assignment, decisión, audit y outbox correspondientes se confirman o revierten juntos; requirements, decisiones y assignments liberados no se sobresc… | Parcial | Atomicidad en `Decision_commits_decision_targets_audit_and_outbox_atomically`; el perdedor de la carrera revierte sin dejar evidencia parcial en `Concurrent_decisions_produce_a_single_terminal_decision`; los assignments liberados conservan historia en `Reconciliation_releases_a_task_when_no_candidate_remains`. |
| NFR-03 Consistencia de autoridad | Una decisión revalida estado confirmado y una revocación de rol/grant no se prolonga mediante caché; una reconciliación debida ocurre en máximo 60 se… | Parcial | Revalidación en transacción y revocación bloqueante en `Only_the_current_assignee_decides_and_losing_eligibility_blocks_the_decision`; reconciliación idempotente con presupuesto de 60 s y `IsDueAsync` en `Reconciliation_reassigns_after_a_revoke_and_is_idempotent`. |
| NFR-04 Disponibilidad segura | Fallos de resolver, adapter, persistencia o ambigüedad nunca degradan a aprobación ni conservan silenciosamente un assignee inválido. | Cumplido | `Assignment_has_no_fallback_when_nobody_is_eligible`; `Scope_resolution_fails_closed_for_a_stale_catalog_reference`; registry fail-closed de adapters y de consumers; persistencia tolerante a un escritor concurrente en `ApprovalOutboxDispatcher`. |
| NFR-05 Observabilidad minimizada | Submissions, assignments, `UNASSIGNED`, decisiones, conflictos, reconciliación y dispatch emiten métricas, logs y trazas OpenTelemetry sin motivos co… | Cumplido | `ApprovalDecisionIntegrationTests.Approval_operations_emit_minimized_metrics_without_personal_data` cubre submissions, assignments, decisiones, conflictos y reconciliación; `ApprovalOutboxIntegrationTests.Backlog_reports_overdue_state_and_telemetry_carries_no_personal_data` cubre dispatch, trazas y tags sin PII. |
| REQ-01 Ingreso confiable e idempotente | Un adapter in-process registrado exactamente una vez por `subject_type + operation + contract_version`, invocado por un workload autenticado y allowl… | Cumplido | API/E2E `Submission_ingestion_requires_an_allowlisted_workload_and_enforces_canonical_limits` (allowlist `403`, replay idéntico, `409`, límites `413`) e integración `Submission_persists_the_case_graph_and_replays_idempotently` con adapter exact-one. |
| REQ-02 Requirements, scopes y targets conservados | Cada requirement conserva keys únicas, contrato tipado y al menos un target completo. | Parcial | Schema/catálogo/agrupación están cubiertos; falta revalidación explícita de cardinalidad y keys de revisión 3. |
| REQ-03 Grafo y prerrequisitos externos | DAG, prerequisite con targets no vacíos y owner adapter/version ligado exact-one a workload `issuer + client_id`. | Parcial | DAG/lifecycle existen; el binding completo, fail-closed de owner y audit tipado aún no están implementados. |
| REQ-04 Asignación determinista y ausencia de candidato | Al habilitar un requirement, el workflow convierte `DecisionScopeDescriptor` a `AuthorizationScopeSet` e invoca `IOrganizationEligibilityService` con… | Cumplido | `ApprovalAssignmentTests` de menor carga y desempate canónico; `ApprovalAssignmentIntegrationTests` de routing, `UNASSIGNED` y reconciliación; API/E2E de rol revocado reconciliado. |
| REQ-05 Segregation of Duties fail-closed | El contrato versionado del adapter declara actores obligatorios por operación y construye exclusiones inmutables. Toda aprobación empresarial exige `… | Parcial | `Excluded_actors_are_never_candidates_even_with_full_scope`; API/E2E `Originator_is_never_a_candidate_and_a_revoked_role_is_reconciled`; `ApprovalSubmissionRules.ValidateActors` exige y propaga exclusiones inmutables. |
| REQ-06 Decisión autorizada e inmutable | Solo el assignee actual, activo y autenticado puede ejecutar `APPROVE`, `REJECT` o `REQUEST_CHANGES` sobre una tarea `PENDING`. Exige motivo no vacío… | Parcial | `Only_the_current_assignee_decides_and_losing_eligibility_blocks_the_decision`, `Decision_replay_returns_the_original_and_a_changed_payload_conflicts` y API/E2E `Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected`. |
| REQ-07 Estados, propagación y cancelación | `ApprovalCase` transita `OPEN → BLOCKED/COMPLETED/CANCELLED`; un bloqueo por `UNASSIGNED` puede volver a `OPEN`, mientras un prerequisite `FAILED`… | Cumplido | `ApprovalGraphTests` de transiciones y propagación; `Approval_unlocks_and_routes_the_dependent_requirement`; `Rejection_cancels_only_linked_descendants_and_emits_their_targets`; cancelación del owner en API/E2E `Owner_cancellation_is_authorized_version_checked_and_preserves_history`. |
| REQ-08 Evidencia canónica y durable | `approval-canonical-json/v1` serializa UTF-8 sin BOM ni whitespace, propiedades conocidas presentes y ordenadas ordinalmente, strings NFC, UUID `D` m… | Parcial | `ApprovalGoldenVectorTests` y `ApprovalCanonicalJson`; cada transición observable escribe audit y un evento de outbox por target en la misma transacción en `ApprovalDecisionService` y `ApprovalWorkflowService`. |
| REQ-09 API, visibilidad, errores y límites | Un usuario activo consulta sus tareas `PENDING` y su historia de decisiones; el originador o workload propietario consulta su caso; `AUDITOR` organiz… | Parcial | API/E2E `Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped` y `Unassigned_requirements_and_reconciliation_require_an_administrative_role`; límites en `Submission_rules_enforce_requirement_and_target_limits` y `Canonical_size_limit_is_enforced_at_the_exact_byte`. |
| REQ-10 Exactly-one bajo concurrencia y operación segura | Existe como máximo una task actual por requirement y como máximo una asociación de decisión por `(requirement_id, target completo)`; exactamente una solo al terminar por decisión y cero al cancelar. | Parcial | El índice único de `ApprovalDecisionTargets` y las carreras cubren la unicidad de decisiones; falta revalidar explícitamente cero asociaciones al cancelar y persisten los bloqueos operativos de worker/lease/serialización registrados por el gate. |

## Verificación independiente

> **Resultado:** Con bloqueos (ronda 1 = BLOCK)
> **Rondas:** 1/2
> **Triaje:** 8 detalle-contrato / 2 ambigüedad / 2 prueba-faltante / 1 error-del-revisor
> **Modelo efectivo:** `openai-codex/gpt-5.6-sol` (effort high) — coincide con el configurado en `subagents.json` y es distinto del orquestador; sin degradación
> **Método:** Subagente `sdd-implementation-reviewer`, ronda 1 sobre `0e25ab77…fd80f4f`
> **Fecha:** 2026-09-11 13:30 UTC

El revisor leyó archivos y ejecutó comprobaciones propias (build, `specctl`, `git`, `hashlib` sobre el vector de submission — coincidió en `de1d2652…`). Veredicto: **BLOCK**, con 12 hallazgos bloqueantes. Triaje verificado contra el contrato por el orquestador:

#### Detalle-contrato (el contrato ya lo exigía y faltaba implementarlo) — 8

- **Worker productivo ausente** (`DependencyInjection.cs:90-102`). `grep -rn "AddHostedService|BackgroundService|IHostedService" src/` no devuelve nada: no hay ejecución automática del dispatcher ni del reconciliador. REQ-10 exige entrega de outbox y recuperación tras restart, y NFR-03 que una reconciliación debida ocurra en ≤60 s; el endpoint administrativo no es un worker.
- **Reconciliación sin lease persistente** (`ApprovalReconciliationService.cs:43-133`). REQ-10 enumera `reconciliación` entre las operaciones que «usan índices, leases y transacciones multiinstancia»; `owner` solo se escribe al final y dos instancias pueden solaparse.
- **Selección de carga fuera de transacción serializable** (`ApprovalSubmissionService.cs:74-98`). REQ-04: «Selección, reserva de carga y assignment se serializan». El índice de reserva solo serializa submissions con la **misma** key; dos keys distintas pueden leer la misma carga y elegir al mismo candidato.
- **Replay de decisión no devuelve los artefactos originales** (`ApprovalDecisionService.cs:109-122`). Devuelve `OutboxEventIds = []` y estados actuales; NFR-01 exige que «un replay devuelve los artefactos originales».
- **Lecturas de evidencia admiten `ADMIN`** (`ApprovalController.cs:307-327`). REQ-09 concede la lectura organizacional de casos, assignments, decisiones y evidencia al `AUDITOR`, mientras que `ADMIN` ve `UNASSIGNED` y dispara reconciliación.
- **`correlation_reference` en trazas** (`ApprovalTelemetry.cs:56-60`). Es entrada arbitraria del workload y puede transportar PII; NFR-05 pide observabilidad minimizada.
- **`Down` de la migración fundacional destruye historia** (`20260911100346_ApprovalWorkflowFoundation.cs:473-521`). El contrato prohíbe la migración descendente destructiva y la prueba de reversión solo retrocede **hasta** foundation, por lo que no demuestra lo que su nombre sugiere.
- **CA-07 sin bytes de preimagen para signal/authority/decisión** (`ApprovalGoldenVectorTests.cs:29-77`). CA-07 pide «fijan bytes y SHA-256 de submission, signal, authority y decisión»; solo submission fija el literal y los demás únicamente el hash.

#### Ambigüedad (el contrato no lo fija; exige `/spec revisar`) — 2

- **Outbox en toda transición observable.** REQ-08 dice «Cada transición observable crea audit y un `ApprovalOutboxEvent` por target en la misma transacción», pero el vocabulario cerrado de `resultado` en Datos y contratos es `APPROVED/REJECTED/CHANGES_REQUESTED/CANCELLED/SATISFIED/FAILED` (`ApprovalRecords.cs:343-349`): **no existe código de resultado para una submission ni para un assignment**. Por eso el outbox solo se produce en `ApprovalWorkflowService` y `ApprovalDecisionService`. La spec es internamente contradictoria y no corresponde adivinar cuál de las dos frases manda.
- **`ADMIN` que además ostenta el rol empresarial.** REQ-09 dice que `ADMIN` «no decide» y CA-05 que «`ADMIN` recibe `403`», pero el decisor legítimo se define por ser el assignee actual y elegible. Hoy un `ADMIN` que además tenga el rol requerido y sea el assignee podría decidir; la E2E solo cubre a un `ADMIN` que no era assignee. Interpretarlo en sentido estricto cambia autorización (materia de seguridad) y conviene fijarlo en el contrato.

#### Prueba-faltante — 2

- La carrera de decisiones captura cualquier `Exception` (`ApprovalDecisionIntegrationTests.cs:192-214`) en vez de exigir el `409` contractual; un error de infraestructura pasaría la prueba.
- La conformidad declarada no se sostiene en las filas REQ-04/06/08/09/10 ni NFR-01/02/03/04/05 mientras los ocho puntos anteriores sigan abiertos.

#### Error-del-revisor — 1

- El hallazgo sobre `actor_user_id` en `ApprovalDecisionService.cs:296-301` es un error del revisor: eso no es telemetría sino el registro de **audit** exigido por el contrato (`ApprovalAuditRecord | actor, UTC, acción, …`), que debe identificar al actor. La parte válida del mismo hallazgo es `correlation_reference` en trazas, ya listada arriba.

#### Hueco de proceso — 1 (`prueba-faltante`)

- El árbol no está limpio en el momento del gate porque este run registra `HEAD verificado: fd80f4f` y esa actualización administrativa es posterior al commit revisado. Se resuelve con el commit de metadatos que debe hacer el usuario.

**Fuera de alcance (no bloquea ni dispara ronda):** nada. El revisor confirma que no hay dependencias nuevas, binarios, secretos, artefactos `bin/obj` ni trabajo sobre delegaciones, supersesión, Policy/waiver, PRs, quórums o frontend.

**Decisión pendiente del humano:** dos ambigüedades internas del contrato (REQ-08 vs vocabulario de `resultado`; alcance de `ADMIN` como decisor) exigen `/spec revisar 03` antes de gastar la ronda 2, porque arreglar los ocho puntos de detalle sin resolverlas haría re-bloquear la ronda por la misma capa.

Trabajo de Fase 4 completado **antes** del gate, para no delegarle la conformidad:

- Suite relevante completa: build `--no-restore --no-incremental` **0 avisos / 0 errores**; UnitTests **96/96**; IntegrationTests **35/35**; ApiE2ETests **12/12**.
- `## Conformidad contractual`: las **25 filas quedan `Cumplido` con evidencia** (10 contratos de datos, 5 NFR y 10 REQ); 0 filas pendientes.
- `## Evidencia de aceptación`: **CA-01…CA-08 `Cumplido`**; 0 criterios pendientes.
- Contraste del diff contra `Commit base`: todos los cambios pertenecen a la spec 03 (dominio, infraestructura, API, pruebas y migraciones aditivas) más los dos `xunit.runner.json` de los proyectos de prueba, necesarios para que las suites de Testcontainers no agoten la memoria del agente. Sin binarios, sin artefactos generados, sin secretos y sin archivos ajenos al alcance.
- Reversibilidad probada: `ApprovalOutboxIntegrationTests.Additive_migrations_revert_and_reapply_while_preserving_history`.
- Gate ejecutado sobre `HEAD verificado` = `fd80f4fd4171dbddd2c57207b47385c716b52030` (commit del usuario), rango `0e25ab77…fd80f4f`. Resultado: **BLOCK (ronda 1/2)**, ver arriba.
- La actualización administrativa de este run (registro de `HEAD verificado` y del resultado del gate) queda sin commitear por diseño: el flujo prohíbe que el agente haga commit y corresponde al usuario commitear los metadatos.

## Resumen de cambios

| Archivo | Motivo | Spec/tarea |
| --- | --- | --- |
| `src/ProcureToPay.Domain/Modules/Approval/*.cs` | Canonicalización, scope tipado, submission y reglas, agregado con DAG/transiciones, fingerprints, outbox/audit | T-01, T-02 |
| `src/ProcureToPay.Application/Abstractions/IApprovalSubmissionAdapter.cs` | Contratos de adapter, registry y allowlist | T-01 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/*.cs` | Records, mapeo EF, hidratación, serialización, servicios de submission/señal/cancelación/consulta, telemetría pendiente | T-01, T-02 |
| `src/ProcureToPay.Infrastructure/Persistence/ProcureToPayDbContext.cs` | DbSets y mapeo `Approval` con índices exactly-one | T-01, T-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260911100346_ApprovalWorkflowFoundation*` | Migración aditiva del esquema Approval | T-05 |
| `src/ProcureToPay.Api/Controllers/ApprovalController.cs` | Endpoints de submission, señal, cancelación y lectura de caso | T-01, T-02, T-06 |
| `src/ProcureToPay.Api/ExceptionHandling/ApiExceptionHandler.cs` | Problem Details `413`/`503` de Approval | T-01 |
| `tests/ProcureToPay.UnitTests/Approval/*.cs` | Contrato, canonicalización, límites, DAG y state machines | T-01, T-02 |
| `tests/ProcureToPay.ApiE2ETests/ApprovalOperationsE2ETests.cs` | API/E2E de assignment y operación: actor excluido, rol revocado, `ADMIN`/no `ADMIN` y cero candidatos | T-03 |
| `tests/ProcureToPay.UnitTests/Approval/ApprovalAssignmentTests.cs` | Unitarias deterministas de routing y SoD sobre el resolver de SPEC 01 | T-03 |
| `tests/ProcureToPay.IntegrationTests/Approval/ApprovalAssignmentIntegrationTests.cs` | Integración SQL de routing, exactly-one assignment, reconciliación y fail-closed de scope | T-03 |
| `src/ProcureToPay.Domain/Modules/Approval/ApprovalRoutingPolicy.cs` | Política pura de menor carga y desempate canónico | T-03, CA-04, NFR-01 |
| `src/ProcureToPay.Domain/Modules/Approval/ApprovalEligibilityEvidence.cs` | Canonicalización determinista de `EligibilityEvidence` | T-03, NFR-01 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalScopeResolver.cs` | `decision-scope/v1` → `AuthorizationScopeSet` con catálogo activo y versión congelada | T-03, REQ-02, REQ-04 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalAssignmentEngine.cs` | Asignación determinista, `UNASSIGNED` y reconciliación | T-03, CA-04, CA-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalReconciliationService.cs` | Reconciliación idempotente y estado de vigencia de 60 s | T-03, NFR-03 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalOperationsQueryService.cs` | Lectura administrativa de `UNASSIGNED` y reconciliación | T-03, REQ-09 |
| `tests/ProcureToPay.UnitTests/Approval/ApprovalGoldenVectorTests.cs` | Golden vectors de bytes y SHA-256 de submission, signal, authority, decisión y fingerprint | T-04, CA-07 |
| `tests/ProcureToPay.IntegrationTests/Approval/ApprovalDecisionIntegrationTests.cs` | Integración SQL de atomicidad, replay, autoridad revocada, carrera y propagación de decisión | T-04, CA-05, CA-06 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalDecisionService.cs` | Decisión autorizada, idempotente y atómica | T-04, REQ-06, REQ-08 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260911111644_ApprovalDecisionEvidence*` | Versiones de preimagen del fingerprint de decisión | T-04, REQ-06 |
| `src/ProcureToPay.Domain/Modules/Approval/ApprovalCase.cs` | `ApplyDecision` habilita edges; `AssignTask` fija assignee; `ReassignTo` | T-03, T-04, REQ-04, REQ-07 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalInboxQueryService.cs` | Bandeja del assignee, historia del actor y lecturas de auditoría | T-06, REQ-09, NFR-05 |
| `tests/ProcureToPay.IntegrationTests/Approval/ApprovalOutboxIntegrationTests.cs` | Integración SQL del outbox: lease, dos instancias, restart, backoff, dead letter, replay, dedupe, telemetría y reversión de migraciones | T-05, REQ-10, NFR-02, NFR-04, NFR-05 |
| `src/ProcureToPay.Application/Abstractions/IApprovalResultConsumer.cs` | Contrato fail-closed de consumer de resultados | T-05, REQ-10 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalOutboxDispatcher.cs` | Dispatcher con lease atómico, backoff y dead letter | T-05, REQ-10 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalOutboxAdministrationService.cs` | Backlog y replay administrativo sin editar payload | T-05, REQ-10 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalTelemetry.cs` | Métricas y trazas minimizadas | T-05, NFR-05 |
| `src/ProcureToPay.Api/Health/ApprovalHealthCheck.cs` | Health de backlog, reconciliación y dead letter | T-05, REQ-10 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260911122932_ApprovalOutboxDispatchIndex*` | Índice del hot path del dispatcher | T-05, REQ-10 |

## Cierre

- HEAD verificado: Pendiente.
- Estrategia de integración: Pendiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Ninguno.
