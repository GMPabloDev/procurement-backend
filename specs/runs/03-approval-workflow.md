# RUN SPEC 03 — Núcleo de casos y decisiones de aprobación

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/03-approval-workflow.md
> **Revisión contractual:** 3
> **Commit de la spec:** 4c933edbac7113573bb8eb38712702d5bca9fd27
> **Blob aprobado:** 8636c4e2b5d79c1de3b3432f90335146792c10e1
> **Digest contractual:** f0b61a917c4c4b4adcaee598c75352ef93d03c3bf25b76d15f80177ba3446234
> **Rama base:** main
> **Commit base:** 0e25ab77a8df32183215c4af68494776798e2541
> **Rama de implementación:** spec-03-approval-workflow
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** balanced
> **Iniciado:** 2026-09-11 04:45 -05
> **Actualizado:** 2026-09-13 01:21 -05
> **HEAD verificado:** Pendiente
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
- **Realineado 2026-09-12:** la revisión 3 quedó aprobada (digest `f0b61a917c4c…`, PASS delta de la constancia) y commiteada en `4c933ed`; el run se realinea a esa revisión y reanuda en modo `balanced`. La revisión 2 y su evidencia histórica se conservan arriba.

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
| T-01 | Verificada | Adapters exact-one, idempotencia, `decision-scope/v1`, targets, agrupación, límites y validaciones. Evidencia: 17 unitarias de contrato (`ApprovalContractTests`, incluidos los vectores exactos de límite: 2.000 requirements y 10.000 enlaces aceptan, +1 rechaza; y el borde exacto de 5 MiB canónicos ±1 byte), 3 de integración SQL (persistencia/replay/conflicto/concurrencia), 1 de contrato del adapter controlado y 2 API/E2E (`403` de workload no allowlisted sin persistir caso, `413` de límites canónicos). | working-tree (CP-06) |
| T-02 | Verificada | DAG con dependencias y prerrequisitos externos, señales idempotentes por `signal_key`+`expected_version`, propagación, cancelación del owner, state machines y exactly-one task por requirement. Evidencia: 7 unitarias de grafo, integración de señal/outbox y 2 API/E2E (señales `SATISFIED`/`FAILED` con `403` de workload no propietario, replay idéntico, `409` por key reutilizada con otro contenido, `409` sobre prerequisite terminal, y cancelación con `403`/`409` de versión obsoleta/`409` de doble cancelación). | working-tree (CP-06) |
| T-03 | Verificada | Routing determinista y reconciliación. Implementado: `ApprovalScopeResolver` (`decision-scope/v1` → `AuthorizationScopeSet` con validación de catálogo activo + versión congelada, fail-closed), `ApprovalRoutingPolicy` (política pura de menor carga y desempate por UUID canónico), `ApprovalAssignmentEngine` (asignación y reconciliación), `ApprovalReconciliationService` (idempotente, presupuesto de 60 s), `ApprovalOperationsQueryService`, endpoints `operations/unassigned` y `operations/reconciliation`, registro DI y migración `20260911105726_ApprovalAssignmentCurrentIndex` (índice único filtrado `IX_ApprovalAssignments_TaskId_Current`). Evidencia: 7 unitarias deterministas (`ApprovalAssignmentTests`, construidas sobre el resolver de SPEC 01), 6 de integración SQL (`ApprovalAssignmentIntegrationTests`, incluida la carrera de dos DbContext y el `UNASSIGNED` sin candidato) y 2 API/E2E (`ApprovalOperationsE2ETests`). | working-tree (CP-06) |
| T-04 | Verificada | Decisiones autorizadas e inmutables. Implementado: `ApprovalDecisionService` (revalidación de autoridad en transacción, idempotencia por `decision_key`, `DecisionFingerprint` reproducible con las versiones de preimagen **almacenadas**, `AuthorityEvidenceDigest` + `WorkflowDecisionDigest`, atomicidad decisión+targets+release+audit+outbox), endpoint `POST tasks/{taskId}/decisions`, registro DI y migración `20260911111644_ApprovalDecisionEvidence` (`RequirementVersion`/`TaskVersion`). Evidencia: golden vectors de `ApprovalGoldenVectorTests` (bytes canónicos de la preimagen de submission + SHA-256 de submission, signal, authority, decisión y fingerprint, con permutación de sets, NFC y rechazo de duplicados), 6 de integración SQL (`ApprovalDecisionIntegrationTests`: atomicidad, replay/conflicto, autoridad revocada, versión obsoleta, carrera concurrente, propagación de approve y reject) y 1 API/E2E (`Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected`). **Defecto real corregido**: `ApprovalCase.ApplyDecision` no activaba los requirements dependientes, por lo que aprobar no habilitaba edges (REQ-07); ahora lo hace en la misma transición y la unitaria existente fija el invariante sin llamada auxiliar. | working-tree (CP-06) |
| T-05 | Verificada | Workers y operación. Implementado: `ApprovalTelemetry` (Actividad + Meter minimizados), contrato fail-closed `IApprovalResultConsumer`/`ApprovalResultConsumerRegistry`, `ApprovalOutboxDispatcher` (lease por UPDATE condicional atómico, entrega al menos una vez, backoff contractual y dead letter a los 10 intentos), `ApprovalOutboxAdministrationService` (backlog, dead letters y replay administrativo sin editar payload), `ApprovalHealthCheck` + `/health/approval`, registro DI y OpenTelemetry (source y meter), y migración aditiva `20260911122932_ApprovalOutboxDispatchIndex`. Evidencia: 7 de integración SQL (`ApprovalOutboxIntegrationTests`: entrega con payload intacto, dos instancias sin doble entrega, recuperación tras restart de lease abandonado, backoff 1 s y dead letter a los 10 intentos, replay administrativo, dedupe del consumer y telemetría sin PII) más 1 API/E2E (`Health_degrades_on_dead_letters_and_admin_alone_replays_them`). | working-tree (CP-06) |
| T-06 | Verificada | Bandeja, visibilidad y evidencia HTTP. Implementado: `ApprovalInboxQueryService` (bandeja del assignee, historia del actor y lecturas organizacionales de decisiones y assignments), endpoints `inbox`, `inbox/history`, `cases/{id}/decisions` y `cases/{id}/assignments`, y registro DI. Evidencia: 1 API/E2E (`Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped`) que cubre assignee, usuario sin trabajo, originador, `AUDITOR`, workload, ocultación `404` y `403` fuera de alcance, minimización sin identidad del IdP ni evidencia de elegibilidad, y `GET cases/{id}` para el originador. **Regresión real corregida**: el atributo `[HttpGet("cases/{caseId:guid}")]` había quedado dentro de la línea del comentario XML `///` (edit previo que eliminó un salto de línea), de modo que la ruta nunca se registraba y el endpoint respondía `404` con cuerpo vacío; la prueba nueva lo detectó. | working-tree (CP-06) |

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

### CP-06 — 2026-09-12 — Revalidación de la revisión 3 (Bloques 1–4)

- Tareas: T-01–T-06 (`Verificada`).
- Cambios: canonicalización `approval-canonical-json/v2`; `approval-result/v2` con `result_source` y `decision_digest=workflow_decision_digest`; unión de actor `USER/WORKLOAD/SYSTEM` con `actor_system_id`, `caused_by` y `automatic_effect_key` por entidad fuente; binding exact-one owner adapter/version → workload `issuer + client_id` persistido (`ApprovalOwnerWorkloadRegistry`); `ApprovalReconciliationRun` con trigger/clave/root atómicos, lease 30 s, renovación 10 s, fencing y cursor UUID-D; `ApprovalWorkflowWorker` (dispatcher + reconciliador) con preflight v2 fail-closed; separación `ADMIN`/`AUDITOR` en las lecturas HTTP y correlación opaca proyectada por el servidor; migración `ApprovalContractV2` aditiva con Downs no destructivos; goldens v2 y pruebas nuevas de CA-03/CA-04/CA-06/CA-07/CA-08.
- Renombrado: `ApprovalSubmissionAdapterRegistry.cs` → `ApprovalWorkloadRegistries.cs` (aloja el registry de adapters y el de owner workloads).
- Tests y checks: build `--no-restore` **0 avisos / 0 errores**; UnitTests **99/99**; IntegrationTests **39/39** (incluye `V2_preflight_blocks_a_legacy_event_baseline`, `Reconciliation_lease_is_fenced_and_reclaimed_reusing_run_root_and_cursor`, `Reserved_role_holders_are_never_candidates_even_with_the_business_role` y `A_reserved_role_granted_after_assignment_blocks_the_decision_and_reconciles`); ApiE2ETests **12/12**; `specctl run-lint 03` ✅; `git diff --check` limpio.
- Defectos reales corregidos durante la revalidación: (1) `PersistNewCase` se ejecutaba antes del motor de asignación, dejando la task persistida sin assignee; ahora el motor asigna primero y la fila se materializa con el assignee y la carga; (2) `AddOutbox` no acumulaba los ids de evento, de modo que el replay no devolvía artefactos; ahora los devuelve; (3) la migración generada reetiquetaba `RequirementId`→`SourceCommandId` y borraba `ActorId`; se sustituyó por columnas aditivas y `ActorId` nullable, preservando la historia; (4) el orden de locks permitía que dos decisiones concurrentes se bloquearan entre sí y que el perdedor agotara el timeout del applock como `503` en vez del `409` contractual; el lock de organización se adquiere ahora antes de leer filas en decisión, señal y reconciliación, y la carrera devuelve `409`; (5) la prueba de carrera capturaba cualquier `Exception`; ahora exige `DomainConflictException`.
- HEAD: working-tree sobre `spec-03-approval-workflow` (`Commit base` `0e25ab77…`).
- Próximo paso: commit del usuario y verificación independiente (ronda 2/2).

### CP-07 — 2026-09-13 — Corrección de los 9 hallazgos del gate (Bloques 1–4)

- Tareas: T-01–T-06 (`Verificada`) — correcciones post-gate sin cambio de contrato.
- Cambios (triaje de la ronda 2/2 aplicado):
  - **R2** — `ApprovalReconciliationService.ProcessAsync` usa reloj actual para claim, renovación, comprobación de holder y completion (`TimeProvider`, por defecto `TimeProvider.System`), sin reutilizar `occurredAt` como reloj de lease; el root audit de una corrida organizacional usa el instante real y `requested_at` el UTC del audit.
  - **R8** — `ApprovalGoldenVectorTests` fija los bytes literales de los cuatro preimages restantes (signal, authority evidence, workflow decision y decision fingerprint), revisados campo a campo contra la tabla REQ-08, con SHA-256 calculado por implementación independiente.
  - **R13** — `ApprovalRequirement` conserva `Actions` (dominio, `ActionsJson` persistido, hidratación y mapeo EF) y la decisión rechaza con `409` una acción fuera del set; migración aditiva `20260913064411_ApprovalRequirementActions` con `Down` no destructivo.
  - **R14** — `ValidateActors` exige la exclusión de originator/requester en cada requirement.
  - **R15** — la señal compara `ExpectedVersion` con la versión persistida y responde `409` ante versión obsoleta antes de transicionar.
  - **R16** — acciones, exclusiones, targets y targets de prerequisite rechazan duplicados sobre la secuencia original; el conteo de 10.000 vínculos ya no puede reducirse por colapso.
  - **R17** — `source_requirement_key` y las keys de prerequisite son únicas dentro del caso; se elimina la interpretación de «particiones».
  - **R18** — el worker pasa `audit.OccurredAt` como `requested_at` de la corrida por cambio organizacional.
  - **R19** — `operations/unassigned`, `operations/reconciliation`, `operations/outbox` y `operations/outbox/dead-letters` exigen `ADMIN`; E2E comprueba el `403` de `AUDITOR`.
- Defecto adicional corregido durante las pruebas: `ApprovalDecisionService` aplica la transición del agregado antes de añadir filas al change tracker (una acción rechazada no deja filas rastreadas para un reintento en el mismo scope) y persiste `RequirementVersion`/`TaskVersion` capturadas antes de la transición.
- Tests añadidos/endurecidos: `Reconciliation_holder_stops_when_the_lease_expires_mid_run`, `Organization_change_run_keeps_the_triggering_audit_utc_as_requested_at`, `A_requirement_rejects_actions_outside_its_declared_set` (unitaria), `A_decision_outside_the_declared_actions_is_a_conflict`, `Prerequisite_signal_with_a_stale_expected_version_is_a_conflict`, duplicados/SoD por requirement en `ApprovalContractTests` y `403` de `AUDITOR` en las lecturas operativas.
- Tests y checks sobre este árbol: build `--no-restore` **0 errores**; UnitTests **101/101**; IntegrationTests **43/43**; ApiE2ETests **12/12**; `specctl run-lint 03` ✅; `git diff --check` limpio.
- HEAD: working-tree sobre `spec-03-approval-workflow` (contenido base `5513497…` + correcciones sin commitear).
- Próximo paso: commit del usuario con este árbol exacto y ronda 3 autorizada de `sdd-implementation-reviewer` (delta sobre el commit).

### CP-08 — 2026-09-13 — Ronda 3 delta y cierre de sus dos residuos (Bloques 1–4)

- Tareas: T-01–T-06 (`Verificada`).
- **Ronda 3 (delta, autorizada por el usuario)** sobre `41d47f8e3297…` (delta desde `5513497fad…`): veredicto BLOCK con R8, R13, R14, R15, R17, R18 y R19 `resolved`, y dos residuos abiertos del mismo hallazgo: **R2** (el lease se comprobaba al entrar al caso, no antes de persistir) y **R16** (los targets de un edge `ApprovalDependencyRef` aún colapsaban duplicados en el hash set); R12 dependiente. Detalle en la sección Verificación independiente.
- Correcciones de los residuos (sin cambio de contrato):
  - **R2** — `ProcessAsync` revalida el lease con el reloj actual (owner, token, estado y `LockedUntil > now`) inmediatamente antes de `SaveChanges`/commit; si expiró, hace rollback con `ChangeTracker.Clear()` y la corrida queda para un reclaimer desde el cursor anterior.
  - **R16** — `ApprovalDependencyRef` materializa la secuencia y rechaza targets duplicados por identidad canónica antes del set, alineado con requirements, prerequisites, acciones y exclusiones.
- Tests añadidos: `Reconciliation_rolls_back_the_case_when_the_lease_expires_while_processing` (integración: lease vivo al entrar, expirado al persistir → sin efectos, sin cursor, tarea con su assignee previo) y el negativo de targets duplicados en un edge en `ApprovalContractTests`.
- Tests y checks sobre este árbol: build `--no-restore` **0 errores**; UnitTests **101/101**; IntegrationTests **44/44**; ApiE2ETests **12/12**; `specctl run-lint 03` ✅; `git diff --check` limpio.
- HEAD: working-tree sobre `spec-03-approval-workflow` (commit `41d47f8…` + residuos sin commitear).
- Próximo paso: commit del usuario con este árbol exacto y ronda delta de cierre (autorización solicitada).

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
| --- | --- | --- | --- |
| CA-01 | Cumplido | `Submission_ingestion_requires_an_allowlisted_workload_and_enforces_canonical_limits`, `Canonical_limits_above_the_contract_return_413_problem_details`, `Submission_persists_the_case_graph_and_replays_idempotently`, `Submission_rules_accept_exactly_the_contractual_count_limits`, `Canonical_size_limit_is_enforced_at_the_exact_byte`, `Concurrent_identical_submissions_create_a_single_case`. | Automática |
| CA-02 | Cumplido | `Decision_scope_descriptor_is_an_exact_canonical_contract`, `Scope_descriptor_rejects_non_canonical_extra_fields_and_invalid_combinations`, cardinalidad/keys en `ApprovalContractTests` y los bytes v2 del golden vector de submission. | Automática |
| CA-03 | Cumplido | `Prerequisite_signals_are_owner_scoped_idempotent_and_fail_closed` (owner exacto y 403 de workload ajeno), `Prerequisite_signal_is_idempotent_and_writes_one_outbox_event_per_target` y `ApprovalOwnerWorkloadRegistry` (ausencia/ambigüedad → 503 antes de crear el caso). | Automática |
| CA-04 | Cumplido | `Assignment_routes_to_the_lowest_load_and_breaks_ties_by_canonical_uuid`, `Exactly_one_current_assignment_per_task_is_enforced_by_the_schema`, `Reconciliation_reassigns_after_a_revoke_and_is_idempotent`, `Reconciliation_releases_a_task_when_no_candidate_remains`, `Reconciliation_lease_is_fenced_and_reclaimed_reusing_run_root_and_cursor` y `TASK_RELEASED` como efecto `SYSTEM/APPROVAL_WORKFLOW`. | Automática |
| CA-05 | Cumplido | `ApprovalAssignmentTests.Excluded_actors_are_never_candidates_even_with_full_scope`, `Originator_is_never_a_candidate_and_a_revoked_role_is_reconciled`, `Reserved_role_holders_are_never_candidates_even_with_the_business_role`, `A_reserved_role_granted_after_assignment_blocks_the_decision_and_reconciles`, `Only_the_current_assignee_decides_and_losing_eligibility_blocks_the_decision`, `Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected`; exclusión dinámica de roles reservados en `ApprovalAssignmentEngine.ReservedRoleHoldersAsync`. | Automática |
| CA-06 | Cumplido | `Rejection_cancels_only_linked_descendants_and_emits_their_targets` (2 `REJECTED` + 1 `CANCELLED` con su `result_source`), `Owner_cancellation_is_authorized_version_checked_and_preserves_history`, `V2_preflight_blocks_a_legacy_event_baseline`, `Additive_migrations_are_non_destructive_and_keep_history`, `Consumer_contract_deduplicates_at_least_once_deliveries`. | Automática |
| CA-07 | Cumplido | `Canonical_preimages_match_their_golden_bytes_and_digests` (v2: `c4f08ea7…`, `674a49d6…`, `93003e10…`, `04eddbde…`, `0ffd7bc1…`), `Approval_result_v2_publishes_exactly_its_declared_properties_and_sources`, `Approval_result_v2_rejects_invalid_source_and_result_combinations`, `Automatic_effect_keys_distinguish_source_entities_on_the_same_target`. | Automática |
| CA-08 | Cumplido | `Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped` (AUDITOR lee decisions/assignments/audit con unión de actor y `caused_by`; `ADMIN` sin `AUDITOR` recibe 403), `Unassigned_requirements_and_reconciliation_require_an_administrative_role`, `Backlog_reports_overdue_state_and_telemetry_carries_no_personal_data`, `Health_degrades_on_dead_letters_and_admin_alone_replays_them`. | Automática |

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

- **LSP de Pi Lens: captura de sesión obsoleta durante la revalidación (herramienta, no código).** Los hallazgos `CS0246/CS0103/CS0117/CS1022` que el gate reportó sobre `ApprovalSubmissionService`, `ApprovalWorkflowService` y el registry se contrastaron con: (1) `dotnet build ProcureToPay.sln --no-restore` con **0 errores** en esos archivos; (2) `lens_diagnostics(source=lsp, scope=paths)` con **0 diagnósticos** tras reiniciar `csharp-ls`; (3) una sola copia en disco. El `CS1022` de `ApprovalSubmissionAdapterRegistry.cs` se desplazaba con cada edición y caía siempre en "EOF+1"; desapareció al renombrar el archivo a `ApprovalWorkloadRegistries.cs` y reiniciar el servidor. No se alteró código de producción para satisfacer la captura.

## Conformidad contractual

> Índice de superficie explícita de `03-approval-workflow.md`, revisión 3. El resumen no sustituye el contrato. Marca con prueba/fixture o justificación de No aplica; REQ/NFR se verifican en la evidencia de aceptación, sin copiarlos aquí.

| Elemento | Contrato | Estado | Evidencia |
| --- | --- | --- | --- |
| `ApprovalCase` | organización, subject type/id/version, operation, snapshot digest, workload, key/fingerprint, estado, versión · No contiene el documento origen; esta… | Cumplido | Agregado `ApprovalCase` + `ApprovalCaseRecord`; no contiene el documento origen. `Submission_persists_the_case_graph_and_replays_idempotently` y `ApprovalGraphTests`. |
| `DecisionScopeDescriptor` | schema version, organización, scopes tipados/versionados · Contrato exacto `decision-scope/v1`; referencias validadas contra SPEC 01. | Cumplido | `Decision_scope_descriptor_is_an_exact_canonical_contract`, `Scope_descriptor_rejects_non_canonical_extra_fields_and_invalid_combinations` y `ApprovalScopeResolver` con catálogo activo y versión congelada. |
| `ApprovalRequirement` | source/workflow key, tipo, stage, role, authority, descriptor, exclusiones, dependencias, targets, estado · Keys únicas dentro del caso, uno o más ta… | Cumplido | Targets no vacíos, keys únicas y conteo exacto de vínculos en `ApprovalContractTests` (2.000/10.000 ±1) y `Submission_rules_accept_exactly_the_contractual_count_limits`. |
| `ExternalPrerequisite` | key, owner adapter/version, owner workload issuer/client id, control/digest, parámetros, targets, estado, signal key/fingerprint, versión · Key única… | Cumplido | Owner adapter/version resuelto exact-one a workload allowlisted `issuer + client_id` persistido; `Prerequisite_signals_are_owner_scoped_idempotent_and_fail_closed` y `ApprovalOwnerWorkloadRegistry`. |
| `ApprovalTarget` | type, id, version, material snapshot digest · Identidad completa usada por edges, decisiones y eventos. | Cumplido | Identidad completa en `ApprovalTarget.CanonicalIdentity`; `Submission_rules_reject_dangling_cycles_and_non_contained_edges`. |
| `ApprovalTask` | requirement, targets, estado, assignment actual, versión · `UNASSIGNED`, `PENDING` y terminales de REQ-07. | Cumplido | `ApprovalGraphTests.Decision_actions_map_to_requirement_and_task_states`; estados y efectos `TASK_ASSIGNED`/`TASK_REASSIGNED`/`TASK_RELEASED` en las pruebas de assignment. |
| `ApprovalAssignment` | task, assignee, UTC de alta/baja, carga, eligibility evidence, causa · Append-only; uno actual por task. | Cumplido | Append-only con índice único filtrado; `Exactly_one_current_assignment_per_task_is_enforced_by_the_schema` y el historial de liberación. |
| `ApprovalDecision` | task/requirement, targets, action, actor, reason, UTC, key/fingerprint, evidence, digests · Append-only; origen `HUMAN` en esta spec. | Cumplido | Append-only con unique `(OrganizationId, ActorUserId, DecisionKey)` y exactly-one por target; replay con artefactos originales en `Decision_replay_returns_the_original_and_a_changed_payload_conflicts`. |
| `ApprovalOutboxEvent` | id, contract version, caso/sujeto, `result_source {type,id,key}`, target, resultado, decisión/digest nullable, attempts/state · Contrato `approval-re… | Cumplido | `approval-result/v2` con `result_source {id,key,type}` y `decision_digest=workflow_decision_digest`: `Approval_result_v2_publishes_exactly_its_declared_properties_and_sources` y `Rejection_cancels_only_linked_descendants_and_emits_their_targets`. |
| `ApprovalAuditRecord` | actor type; actor user id, workload issuer/client id o system id nullables; `caused_by {audit_stream,audit_id}` nullable; `automatic_effect_key` null… | Cumplido | Unión `USER/WORKLOAD/SYSTEM`, `caused_by {audit_stream,audit_id}` y `automatic_effect_key` por entidad fuente: `Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped` y `Automatic_effect_keys_distinguish_source_entities_on_the_same_target`. |
| `ApprovalReconciliationRun` | id, organización, trigger, trigger audit o admin key, estado, requested/started/completed UTC, cursor, lease owner/until/fencing token, attempts/last… | Cumplido | Trigger/clave/root atómicos, lease 30 s, fencing y cursor UUID-D: `Reconciliation_lease_is_fenced_and_reclaimed_reusing_run_root_and_cursor` y la idempotencia por `reconciliation_key`. |
| `submission_fingerprint` | `adapter_id`, `adapter_version`, `canonicalization_version`, `operation`, `organization_id`, `originator_id`, `prerequisites`, `requester_id`, `requi… | Cumplido | `Canonical_preimages_match_their_golden_bytes_and_digests` fija los bytes v2 y `c4f08ea7…`. |
| `authority` | `amount_base`, `base_currency`, `kind`, `minimum_rank`, `type` | Cumplido | `ApprovalRequirementDefinition.AuthorityValue` dentro del golden v2 de submission (`c4f08ea7…`). |
| `signal_fingerprint` | `canonicalization_version`, `evidence_digest`, `evidence_reference`, `owner_client_id`, `owner_issuer`, `prerequisite_id`, `prerequisite_version`, `r… | Cumplido | `Canonical_preimages_match_their_golden_bytes_and_digests` fija `674a49d6…` y el fingerprint usa el owner persistido. |
| `decision_fingerprint` | `action`, `actor_user_id`, `canonicalization_version`, `case_id`, `reason`, `requirement_id`, `requirement_version`, `targets`, `task_id`, `task_vers… | Cumplido | `Canonical_preimages_match_their_golden_bytes_and_digests` fija `0ffd7bc1…`; el replay lo reproduce con las versiones almacenadas. |
| `authority_evidence_digest` | `canonicalization_version`, `delegation_id`, `delegation_version`, `eligibility_evidence`; en esta spec ambos campos de delegación son `null` y evide… | Cumplido | Golden v2 de authority evidence `93003e10…`. |
| `workflow_decision_digest` | `action`, `actor_user_id`, `authority_evidence_digest`, `canonicalization_version`, `case_id`, `decided_at`, `decision_id`, `decision_scope_digest`,… | Cumplido | Golden v2 de workflow decision `04eddbde…`; es el valor publicado como `decision_digest`. |
| `approval-result/v2` | `case_id`, `contract_version`, `decision_digest`, `decision_id`, `event_id`, `occurred_at`, `organization_id`, `result`, `result_source`, `subject_id… | Cumplido | `Approval_result_v2_publishes_exactly_its_declared_properties_and_sources` y `Approval_result_v2_rejects_invalid_source_and_result_combinations`. |

## Verificación independiente

> **Resultado:** Con bloqueos (ronda 3 delta = BLOCK con 2 residuos ya corregidos) — el run no queda listo para integrar todavía
> **Rondas:** 3 (presupuesto automático 2/2 consumido + ronda 3 delta autorizada por el usuario)
> **Triaje (ronda 2):** 8 detalle-contrato / 1 prueba-faltante / 1 error-del-revisor descartado / 1 hueco de proceso resuelto
> **Modelo efectivo:** `openai-codex/gpt-5.6-sol` (effort high) — coincide con el configurado en `subagents.json` y es distinto del orquestador (`deepseek-v4-pro`); sin degradación
> **Método:** Subagente `sdd-implementation-reviewer`; ronda 1 sobre `0e25ab77…fd80f4f` (histórica, abajo), ronda 2 sobre `5513497fad…` y ronda 3 (delta) sobre `41d47f8e3297…`
> **Fecha:** ronda 1: 2026-09-11 13:30 UTC · ronda 2: 2026-09-13 06:21 UTC · ronda 3: 2026-09-13 07:45 UTC

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

#### Ronda 2/2 — 2026-09-13 06:21 UTC (BLOCK)

Revisión full sobre el contrato revisión 3 y el árbol `5513497fad…` (HEAD, rama `spec-03-approval-workflow`, working tree limpio). El revisor confirmó la identidad del árbol (la spec en HEAD difiere del blob aprobado solo en `Ejecución: En implementación`; `specctl digest 03` idéntico), no reejecutó las suites (build 0/0, Unit 99/99, Integration 39/39, E2E 12/12 del árbol probado) y no usó LSP por la degradación documentada. Veredicto: **BLOCK**. Triaje del orquestador contra el contrato: los 9 hallazgos abiertos son válidos y corresponden a reglas claras del contrato o a prueba faltante; ninguno exige `/spec revisar`.

Los hallazgos de la ronda 1 quedaron resueltos en el árbol de la revisión 3: R1 (worker productivo en `Program.cs`), R3 (applock serializa selección/carga/assignment), R4 (replay con artefactos originales), R5 (`AUDITOR` para evidencia organizacional), R6 (correlation fuera de trazas), R7 (`Down` no destructivo), R9 (outbox cerrado a resultados), R10 (`ADMIN`/`AUDITOR` excluidos de candidatura y decisión), R11 (carrera exige `DomainConflictException`) y PROC-1 (candidato commiteado y limpio). Las dos ambigüedades de la ronda 1 se resolvieron en el contrato revisión 3 y no reabren.

Hallazgos abiertos (bloqueantes):

- **R2 — Reloj congelado en los leases de reconciliación** (`ApprovalReconciliationService.cs:193-298`). `ProcessAsync` reutiliza `occurredAt` para claim, renovación, comprobación de holder y completion: la renovación nunca extiende `locked_until` más allá de claim+30 s reales, la comprobación no detecta expiración y completion confirma con lease vencido si nadie reclama. REQ-10: «`locked_until=now+30s`», renovación, «cada transacción de caso comprueba owner, token y no expiración», «solo el lease vigente avanza cursor o completa». Usar reloj actual (`DateTimeOffset.UtcNow` o proveedor) en las operaciones de lease, separado de `requested_at`.
- **R8 — Preimages de golden vectors sin bytes literales** (`ApprovalGoldenVectorTests.cs:43-79`). Solo submission fija los bytes; signal, authority, workflow decision y decision fingerprint comparan únicamente SHA-256. CA-07 exige «schemas, bytes completos y los cinco SHA-256» y la fixture contractual dice que los tests «fijan los bytes completos derivados de esta fixture». Fijar los cuatro preimages literales.
- **R13 — `allowedActions` se pierde y no se aplica** (`ApprovalCase.cs:562-620,686-700`; `ApprovalDecisionService`). `ApprovalRequirement` no conserva acciones ni `ApplyDecision` las comprueba: un requirement solo-`APPROVE` acepta `REJECT`/`REQUEST_CHANGES`. REQ-02: el requirement «contiene … acciones» y «respuestas distintas requieren requirements distintos antes de decidir». Conservar acciones y rechazar la acción fuera del set.
- **R14 — SoD valida la unión, no cada requirement** (`ApprovalSubmission.cs:334-350`). Basta excluir al originator/requester en un requirement para pasar la validación y dejarlo candidato en otro. REQ-05: exclusiones por requirement, «exclusiones incompatibles con el adapter version se rechazan antes de persistir» y «un actor excluido no puede ser candidato, assignee ni decisor». Validar la exclusión en cada requirement.
- **R15 — `ExpectedVersion` de señal nunca se compara** (`ApprovalWorkflowService.cs:148-176`). Solo entra al fingerprint; una señal con versión arbitraria sobre un prerequisite `WAITING` se acepta. REQ-03 autoriza el cambio «mediante `signal_key`, fingerprint y versión esperada». Rechazar versión distinta de la actual con `409` antes de transicionar.
- **R16 — Duplicados colapsan en `ImmutableHashSet` antes de rechazarse** (`ApprovalSubmission.cs:46-66,131-167`). Acciones, exclusiones y targets duplicados se aceptan colapsados; el conteo de 10.000 vínculos se calcula después del colapso. REQ-09: «duplicarlo dentro de una entidad se rechaza»; los sets canónicos «rechazan duplicados». Detectar duplicados sobre la secuencia original y rechazarlos.
- **R17 — `source_requirement_key` duplicadas aceptadas como «particiones»** (`ApprovalSubmission.cs:359-383`). La regla «cada clase de key es única dentro de su caso» y CA-02 («keys duplicadas … fallan») lo prohíben; la resolución de dependencias usa `SingleOrDefault`/`Single` sobre esa key y puede silenciar la insatisfacción o fallar con 500. Rechazar la segunda key con `409`.
- **R18 — `requested_at` de corrida por cambio organizacional usa la hora del sweep** (`ApprovalWorkflowWorker.cs:113-137`). El worker no selecciona `audit.OccurredAt` y pasa el UTC del sweep; tras una caída se reinicia el presupuesto de 60 s (NFR-03). La regla de `ApprovalReconciliationRun` fija «`requested_at` es el UTC del audit organizacional que la disparó». Pasar el UTC del audit y separar el reloj de proceso (ver R2).
- **R19 — `AUDITOR` obtiene lecturas operativas de `ADMIN`** (`ApprovalController.cs:206-275,396-417`). `GetUnassigned`, `GetReconciliationState`, `GetOutboxBacklog` y `GetDeadLetters` usan `requireAdmin:false`. REQ-09 asigna a `ADMIN` las consultas de `UNASSIGNED`, outbox y health, declara los permisos «no intercambiables» y CA-08 fija «`ADMIN` opera `UNASSIGNED`, reconciliación y outbox». Exigir `ADMIN` en esas cuatro superficies.
- **R12 — Conformidad declarada sin sustento** mientras los anteriores sigan abiertos; se cierra al corregirlos.

Descartados: **ERR-1** (identificar al actor en audit es evidencia contractual, no fuga de telemetría) y **PROC-1** quedó resuelto (candidato commiteado, árbol limpio).

**Consecuencia de la ronda 2:** el run no alcanzó «Lista para integrar»; el presupuesto de dos rondas automáticas quedó consumido y corregir R2/R8/R13–R19 exigió tests, commit real y autorización expresa del usuario para una ronda adicional.

#### Ronda 3 (delta, autorizada) — 2026-09-13 07:45 UTC (BLOCK con 2 residuos)

Revisión delta sobre `41d47f8e3297…` (delta desde `5513497fad…`), autorizada expresamente por el usuario tras consumir el presupuesto 2/2. El revisor confirmó identidad del árbol, digest contractual y ausencia de cambios de spec; no repitió suites ni usó LSP.

- `resolved`: **R8** (cuatro preimages literales + SHA-256 independiente), **R13** (acciones persistidas y validadas antes de mutar), **R14** (exclusión por requirement), **R15** (versión esperada en señal), **R17** (keys únicas), **R18** (`requested_at` = UTC del audit), **R19** (`ADMIN` en las cuatro lecturas operativas).
- **R2 (residuo)**: el lease se comprobaba al entrar al caso, pero los efectos y el cursor se persistían tras `ReconcilePendingAsync` sin revalidar vigencia; un caso más largo que el lease podía confirmar tras expirar. Corregido con revalidación fenced inmediata antes de `SaveChanges`/commit y rollback con limpieza del change tracker.
- **R16 (residuo)**: `ApprovalDependencyRef` aún colapsaba targets duplicados en `ImmutableHashSet`; corregido rechazando duplicados sobre la secuencia original.
- **R12**: dependiente de R2/R16; se cierra con ellos. El seguimiento de cierre es la ronda delta final (autorización solicitada).

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
| `src/ProcureToPay.Domain/Modules/Approval/ApprovalEvidenceContracts.cs` | Fuentes tipadas de resultado y efecto, unión de actor, causalidad y `automatic_effect_key` | T-01, T-02, T-03, T-04 |
| `src/ProcureToPay.Domain/Modules/Approval/ApprovalReconciliationRun.cs` | Corrida durable con trigger, root audit, lease, fencing y cursor | T-03, T-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalWorkloadRegistries.cs` | Renombrado del registry de adapters; registry de owner workloads | T-01, T-02 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalInstanceIdentity.cs` | Identidad opaca del holder de leases | T-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Approval/ApprovalContractPreflight.cs` | Preflight v2 fail-closed sobre eventos v1 | T-05 |
| `src/ProcureToPay.Api/Workers/ApprovalWorkflowWorker.cs` | Worker productivo de outbox y reconciliación | T-05 |
| `docs/approval-operations.md` | Operación: preflight, migración no destructiva, worker, health y recuperación | T-05 |
| `src/ProcureToPay.Infrastructure/Persistence/Migrations/20260912064322_ApprovalContractV2*` | Migración aditiva v2 (result source, auditoría tipada, corridas) | T-01–T-05 |

## Cierre

- Verificación independiente: BLOCK ronda 2/2 sobre `5513497fad…` (detalle en la sección Verificación independiente).
- HEAD verificado: Pendiente.
- Estrategia de integración: Pendiente.
- Commit integrado en rama base: Pendiente.
- Verificación ejecutada sobre rama base: Pendiente.
- Metadatos de vigencia actualizados: Pendiente.
- Pendientes posteriores: Ninguno.
