# RUN SPEC 08 — Budgets y movimientos presupuestarios por posición

> **Formato:** sdd-run/v2
> **Estado del run:** Lista para integrar
> **Spec:** specs/08-budgets-movimientos-presupuestarios.md
> **Revisión contractual:** 1
> **Commit de la spec:** 534a7c83b42f2727a143298d77b2a2fd47d0536d
> **Blob aprobado:** 6c1e1e922a9d67e599a6237eec51481368c47236
> **Digest contractual:** f381a0a03d09ebf344504cc47a64ac6ebcbe20078d143b57af4226012c883326
> **Rama base:** main
> **Commit base:** 534a7c83b42f2727a143298d77b2a2fd47d0536d
> **Rama de implementación:** spec-08-budgets-movimientos-presupuestarios
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-14 18:50 -0500
> **Actualizado:** 2026-09-15 10:20 -0500
> **HEAD verificado:** 95b3012bcfd540a0210d186c987951c9c9eb5418
> **Commit de integración:** Pendiente

## Línea base

Ejecutada sobre el commit base `534a7c8` antes de cualquier edición, con el árbol limpio:

- `dotnet build ProcureToPay.sln --nologo -v q` → 0 errores (14 advertencias preexistentes).
- `dotnet test --project … --no-restore` (documentado en `AGENTS.md`) no ejecuta pruebas en este
  entorno: se usa la ejecución directa de las asambleas compiladas.
- Unitarias: `173/173` correctas, 0 con errores (línea base previa a la implementación).
- Integración (Testcontainers + Docker): `82/82` correctas, 0 con errores.
- API/E2E (Testcontainers + Docker): `24/24` correctas, 0 con errores.
- Fallos preexistentes: ninguno.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio `ProcureToPay.Domain/Modules/Budget`: posición (`budget-position-key/v1`), revisión de asignación, proyección de cuatro buckets, calculadora de movimientos con transiciones parciales y REVERSE, contratos canónicos y parámetros cerrados del owner. Evidencia: `BudgetCanonicalizationGoldenTests` (10, incluidos los cinco SHA-256 publicados), `BudgetMovementCalculatorTests` (16). | working-tree / CP-01 |
| T-02 | Verificada | Persistencia y administración: schema `Budget` con triggers append-only/predecessor/punteros, `BudgetPersistenceService` con locks `sp_getapplock` ordenados por digest y proyección atómica, `BudgetController` (ADMIN asigna, lectura por scope, `404` fuera de scope). Evidencia: build 0 errores y suites verdes. | working-tree / CP-01 |
| T-03 | Verificada | Precheck por referencia: `PurchaseRequestBudgetDemandBuilder` (contrato `purchase-request-budget-demand-build/v1`), `BudgetLedgerService.PrecheckAsync` (operación `REQUESTED`, sin tocar buckets, replay por key) y `POST /api/v1/purchase-requests/{id}/versions/{v}/budget-precheck`. Evidencia: E2E real del harness de catálogos. | working-tree / CP-01 |
| T-04 | Verificada | `policy-approval-adapter/v3` con proyección budget completa (Fiscal Year, Spend Category, importe y target por línea) vía `PolicyApprovalBudgetProjection`; v2 permanece registrado. Evidencia: parámetros persistidos comprobados en el E2E (`fiscal_year`, `spend_category_ref`, Cost Center, categoría). | working-tree / CP-01 |
| T-05 | Verificada | Owner real `budget-check-owner/v1`: `BudgetPrerequisiteProcessor` con attempt durable, keys deterministas, lease renovado cada ≤10 s por un heartbeat independiente, titularidad validada antes de cada checkpoint y fence transaccional (`BudgetLeaseFence` con fencing token y lease owner), reserva all-or-nothing y señal con `budget-check-evidence/v1`; prerequisito `FAILED` sin reserva cuando falta saldo. La revisión independiente detectó que el lease no se renovaba, que un worker obsoleto podía confirmar movimientos o sobrescribir la fila del nuevo dueño y que un fallo tras confirmar la operación no liberaba el lease: corregido en CP-05. Evidencia: `Budget_control_reserves_all_or_nothing_and_signals_the_prerequisite`, `A_budget_without_funds_fails_the_owner_prerequisite_without_reserving`, `The_lease_is_renewed_while_an_effect_is_blocked`, `A_reclaimed_lease_is_never_overwritten_by_a_stale_worker`, `An_expired_lease_of_a_dead_worker_is_reclaimed_without_double_booking`, `A_crash_after_the_confirmed_signal_is_finished_from_the_recorded_signal` y `A_stale_fence_cannot_confirm_a_movement`. | working-tree / CP-05 |
| T-06 | Verificada | Compensación terminal en el processor (case `CANCELLED|SUPERSEDED` → `REVERSE` → `COMPENSATED`), `BudgetReleaseService` con takeover `409`, release automático en `PurchaseRequestApprovalResultConsumer` para `REJECTED|CHANGES_REQUESTED|CANCELLED|SUPERSEDED` (los tres contratos registrados) y release antes de confirmar la cancelación de PR con fila durable `PurchaseRequestBudgetReleaseAttempt`. Evidencia: `A_rejected_requirement_releases_the_open_reservation` (reserva 100 → rechazo real por `ApprovalDecisionService` → dispatch real del outbox → `REVERSE` de 100, `RELEASE` con resultado `RELEASED`, redelivery idempotente). La transferencia atómica está implementada como `BudgetLedgerService.TransferReserveAsync`: bajo los locks ordenados de todas las posiciones involucradas evalúa primero contando el hold del predecessor como disponible y, solo si el set nuevo cabe entero, revierte y reserva en la misma transacción; si no cabe no revierte ni reserva. Evidencia: `A_superseded_hold_transfers_to_the_replacement_all_or_nothing`, `A_transfer_that_cannot_cover_the_whole_set_reverses_nothing` y `Cancelling_an_approved_request_releases_its_reservation_before_completing` (cancelación real con `PurchaseRequestBudgetReleaseAttempt` en `RELEASED`, un solo `REVERSE` y replay idempotente). La recuperación de un crash entre señal confirmada y checkpoint terminal se cierra en CP-05: `ProcessDueAsync` reclama attempts no terminales aunque el prerequisito ya haya decidido y consulta la señal registrada antes de compensar. | working-tree / CP-05 |
| T-07 | Verificada | `BudgetMovementProducerRegistry` (exact-one por operación+contrato+fuente, workload completo, 0/2/deshabilitado fail-closed), `BudgetTransitionService` (COMMIT/CONSUME/REVERSE con matching de fuente en REVERSE) y superficie workload-only `POST /api/v1/budgets/movements` con `budget-transition-command/v1` y respuesta `budget-transition-response/v1`. Evidencia: `BudgetTransitionIntegrationTests` (6, SQL real): remanente de RESERVED consumido por COMMIT, replay idéntico por key y `409` con otra preimagen, CONSUME sobre COMMITTED, 0 y 2 producers fail-closed, workload ajeno `403` sin movimiento, REVERSE solo del delta no avanzado de su propia fuente y REVERSE de otra fuente prohibido. Sin registros productivos, como exige el contrato. | working-tree / CP-02 |
| T-08 | Verificada | `BudgetHealthCheck` (`BUDGET_OK`/`BUDGET_PROCESSOR_*`/`BUDGET_PRODUCER_*`/`BUDGET_ATTEMPT_OVERDUE`/`BUDGET_LEDGER_CORRUPTED`), endpoint `/health/budget`, processor integrado en el worker productivo, `docs/budget-operations.md` y el recorrido E2E real PR→Policy→precheck→Approval→owner. Evidencia operativa: redelivery de una reserva confirmada con las mismas keys no duplica movimientos ni saldo (`A_redelivered_reservation_reuses_its_operations_without_double_booking`), la reconstrucción contractual (allocation current + deltas) iguala exactamente los cuatro buckets proyectados (`Rebuilding_a_balance_matches_the_posted_movements_exactly`) y la matriz de fault injection de la suite de submit (insuficiencia, rechazo, cancelación) sigue verde. Los dos huecos declarados quedan cerrados: (a) transporte HTTP de transiciones con tokens de workload: `BudgetTransitionE2ETests` (3, HTTP real + SQL real) prueba COMMIT de un producer registrado con respuesta canónica y buckets, replay por key, `409` por preimagen divergente, `403` de workload ajeno y de token de usuario sin identidad de workload, `400` de versión de contrato no reconocida, `503` de producer ausente o deshabilitado, siempre sin movimiento, y health sano tras una revisión legítima de allocation (`Health_rebuilds_the_projection_instead_of_reading_a_revision_as_corruption`); (b) carrera dedicada de dos instancias sobre un mismo attempt: `Two_instances_racing_the_same_attempt_reserve_and_signal_once` fuerza con un lock compartido que ambas lean la misma fila y compitan por el claim, y verifica un único outcome, `FencingToken`/`Attempts` = 1, una sola reserva y una sola señal. | working-tree / CP-05 |

## Checkpoints

### CP-05 — 2026-09-15 09:35 -0500 — Correcciones de la revisión independiente

- Tareas: T-01–T-08 verificadas; CA-01..CA-08 cumplidos.
- Revisión: primera ronda independiente (`sdd-implementation-reviewer`, `openai-codex/gpt-5.6-sol`, BLOCK) con hallazgos R8–R11; todas las correcciones son internas y no cambian el contrato ni la superficie HTTP.
- Cambios: `BudgetPrerequisiteProcessor` reclama attempts no terminales aunque el prerequisito ya haya decidido, resuelve la señal registrada antes de compensar, mantiene el lease fresco con un heartbeat independiente cada ≤10 s (`LeaseHeartbeat`: conexión dedicada; toma el lock de la fila del attempt en una sentencia propia y luego renueva con el reloj del servidor, de modo que una renovación retrasada nunca revive un lease vencido; serializado con el gate de checkpoints), valida dentro del gate la titularidad exacta del lease antes de cada mutación (owner, fencing token y vigencia, de modo que un worker obsoleto nunca sobrescribe la fila del nuevo dueño), pasa un `BudgetLeaseFence` (attempt + fencing token + lease owner) a cada operación del ledger y registra el fallo posterior a una operación confirmada conservando estado y liberando lease; `BudgetLedgerService` valida el fence también en los retornos de replay e insuficiencia y con `UPDLOCK` dentro de la transacción de reserva, transferencia y release, y el replay de reserva recupera la request operation y los movimientos REQUESTED originales; `BudgetPersistenceService.RebuildAsync` reconstruye como allocation current + deltas y `BudgetHealthCheck` compara contra esa reconstrucción; tests de CA-01 (carrera, reducción, refs inválidas y organización ajena), CA-02 (lote multi-posición), CA-03 (agrupación y agregado por posición), CA-06 (lease renovado con el efecto bloqueado, reclaim que no sobrescribe, heartbeat que no revive un lease vencido, lease vencido, crash tras señal, fence obsoleto y replay con artefactos) y CA-07 (golden de release).
- Tests y checks: build 0 errores; Unit `200/200`; Integración `107/107`; API/E2E `27/27`; `git diff --check` limpio; `specctl run-lint 08` válido. Suites completas ejecutadas una vez sobre el árbol estable de este checkpoint. La regresión de renovación vencida se comprobó en negativo: con un reloj capturado antes del lock el test falla; con el reloj del servidor tras el lock pasa.
- Evidencia: `Two_concurrent_allocation_revisions_leave_one_current_version`, `An_allocation_reduction_below_held_funds_and_invalid_references_are_rejected`, `A_multi_position_batch_with_one_failing_movement_confirms_nothing`, `A_precheck_groups_targets_per_position_and_never_changes_buckets`, `A_stale_fence_cannot_confirm_a_movement`, `The_lease_is_renewed_while_an_effect_is_blocked`, `A_reclaimed_lease_is_never_overwritten_by_a_stale_worker`, `The_heartbeat_does_not_revive_an_expired_lease`, `A_redelivered_reservation_reuses_its_operations_without_double_booking` (con request operation y movimientos REQUESTED reutilizados), `An_expired_lease_of_a_dead_worker_is_reclaimed_without_double_booking`, `A_crash_after_the_confirmed_signal_is_finished_from_the_recorded_signal`, `Release_fingerprint_is_reproducible_from_the_contract_fixture`, `Health_rebuilds_the_projection_instead_of_reading_a_revision_as_corruption`. La negativa cross-tenant ejercita una organización declarada distinta con el Cost Center real, porque el schema admite una sola organización (SingletonKey único).
- HEAD: `10c06df` (código probado commiteado).
- Próximo paso: verificación independiente final sobre este árbol.

### CP-04 — 2026-09-15 09:04 -0500 — Huecos de T-08 cerrados: transporte HTTP y carrera de dos instancias

- Tareas: T-01–T-08 verificadas; sin pendientes declarados.
- Cambios: `BudgetTransitionE2ETests` (nuevo, `tests/ProcureToPay.ApiE2ETests/Budget/`): factory con la tabla cerrada `Budget:MovementProducers`, token de workload y token de usuario administrativo, y seed de posición 1000 PEN + hold real de 100 PEN para ejercer COMMIT/REVERSE por HTTP. `Two_instances_racing_the_same_attempt_reserve_and_signal_once` en la suite de catálogos, con `ProcessBudgetInstanceAsync` (contexto y lease por instancia) y orquestación determinista del claim. Arreglos internos en `BudgetPrerequisiteProcessor`: `ClaimAsync` trata `DbUpdateConcurrencyException` como un claim perdido (devuelve `false` en lugar de abortar el barrido) y `RecordReserveAsync` conserva el lease hasta el estado terminal, de modo que solo un lease vencido es reclamable.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `97/97`; API/E2E `26/26`; `git diff --check` limpio. Suite completa ejecutada una vez sobre el árbol estable de este checkpoint; el test de carrera se repitió tres veces con resultado estable.
- Evidencia: `A_registered_workload_commits_over_http_and_replays_by_key`, `An_absent_disabled_or_stale_transition_fails_closed_over_http`, `Two_instances_racing_the_same_attempt_reserve_and_signal_once`.
- HEAD: `8ceec72` (código probado commiteado; el transporte en `7a529a8`).
- Próximo paso: revisión independiente sobre este árbol.

### CP-03 — 2026-09-15 02:10 -0500 — Transferencia atómica y cancelación probada

- Tareas: T-01–T-07 verificadas; T-08 verificada salvo dos huecos declarados (ver tabla).
- Cambios: `TransferReserveAsync` en el ledger (evaluación con crédito del hold previo, reversión y reserva all-or-nothing en una transacción, replay por keys registradas), integración del transfer en `BudgetPrerequisiteProcessor` cuando el case superseded tiene holds abiertos, y `BudgetPersistenceService.LoadStateAsync` para resolver una posición desde un movimiento.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `96/96`; API/E2E `24/24`; `git diff --check` limpio.
- Evidencia: `BudgetTransitionIntegrationTests` (10), `Cancelling_an_approved_request_releases_its_reservation_before_completing`.
- HEAD: `ad7ec4c` (código probado commiteado).
- Próximo paso: commit del bloque y revisión final del run.

### CP-02 — 2026-09-14 23:55 -0500 — Release de reservas y transiciones workload-only

- Tareas: T-01–T-05 y T-07 verificadas; T-06 y T-08 parciales (ver tabla).
- Cambios: release automático en el consumer de resultados/lifecycle con `ReleaseBudgetAsync`, release previa a la cancelación de PR (`ReleaseBudgetBeforeCancellationAsync` + attempt durable), superficie `POST /api/v1/budgets/movements` con contrato v1, proyección `Posted` en el ledger, `Include(Position)` en las lecturas de replay (bug real: el replay fallaba en un contexto que no trackeaba la posición) y `BudgetTriggerEvent` con alfabeto de identidad.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `91/91`; API/E2E `24/24`; `git diff --check` limpio.
- Evidencia: `BudgetTransitionIntegrationTests` (6), `A_rejected_requirement_releases_the_open_reservation`, suites completas sobre el árbol estable de CP-02.
- HEAD: `5ca454c` (código probado commiteado; el release de reservas quedó en `57239a5`).
- Próximo paso: transferencia atómica en supersesión y prueba de cancelación de PR con presupuesto; después T-08 (fault injection, dos instancias, rollback).

### CP-01 — 2026-09-14 21:40 -0500 — Ledger, precheck, adapter v3 y owner real

- Tareas: T-01–T-05 verificadas; T-06–T-08 parciales (ver tabla).
- Cambios: dominio Budget, migración `Spec08BudgetLedger` con triggers, servicios de ledger/precheck/release/transición, builder de demands, adapter v3, processor real, health, endpoint de precheck, worker y runbook.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `84/84`; API/E2E `24/24`; `git diff --check` limpio. Suite completa ejecutada una vez sobre el árbol estable `89af55e`.
- Evidencia: `BudgetCanonicalizationGoldenTests`, `BudgetMovementCalculatorTests`, `Budget_control_reserves_all_or_nothing_and_signals_the_prerequisite`, `A_budget_without_funds_fails_the_owner_prerequisite_without_reserving`.
- HEAD: `89af55e` (código probado commiteado).
- Próximo paso: cerrar T-06 (release automático, transferencia en supersesión, cancelación PR), T-07 (superficie workload-only) y T-08 (fault injection, dos instancias, rollback).

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | Golden y tabla de deltas verdes; `Two_concurrent_allocation_revisions_leave_one_current_version` deja una sola allocation current e historia intacta, `An_allocation_reduction_below_held_funds_and_invalid_references_are_rejected` rechaza la reducción bajo fondos retenidos y refs/FY/moneda inválidos, y `Rebuilding_a_balance_matches_the_posted_movements_exactly` reconstruye contra SQL. | Revisión pendiente |
| CA-02 | Cumplido | Tabla de deltas/reverse y rechazos unitarios verdes; `A_multi_position_batch_with_one_failing_movement_confirms_nothing` confirma que un lote en dos posiciones con un movimiento fallido no deja operación ni movimiento, y `A_commit_consumes_the_reserved_remainder_and_is_idempotent_by_key` cubre el consumo parcial idempotente. | Revisión pendiente |
| CA-03 | Cumplido | `A_precheck_groups_targets_per_position_and_never_changes_buckets` agrupa dos líneas de una posición y una de otra, crea un `REQUESTED` por target, no toca buckets y rechaza tanto la línea única insuficiente como dos líneas que por separado cubren pero suman de más (agregado por posición); el precheck por referencia y su respuesta minimizada se cubren por E2E. | Revisión pendiente |
| CA-04 | Cumplido | Los parámetros persistidos contienen un demand por target con Fiscal Year, Spend Category, Cost Center e importe, y el adapter v3 los construye desde la línea confirmada. | Revisión pendiente |
| CA-05 | Cumplido | Reserva all-or-nothing + SATISFIED con evidencia; insuficiencia → FAILED sin RESERVED; precheck insuficiente → `422` sin caso. | Revisión pendiente |
| CA-06 | Cumplido | Release automático probado end-to-end (rechazo → REVERSE → RELEASE idempotente) y release previa a la cancelación de PR implementada con attempt durable. Transferencia atómica, release automático, compensación y cancelación de PR probados sobre SQL real. El lease se renueva antes de cada efecto y el ledger verifica el fencing token con `UPDLOCK` dentro de la misma transacción: `A_stale_fence_cannot_confirm_a_movement` rechaza un fence obsoleto sin movimiento, `An_expired_lease_of_a_dead_worker_is_reclaimed_without_double_booking` recupera un lease vencido sin doble reserva y `A_crash_after_the_confirmed_signal_is_finished_from_the_recorded_signal` cierra un crash entre señal y checkpoint consultando la señal registrada. | Revisión pendiente |
| CA-07 | Cumplido | Los cinco vectores publicados se reproducen; el registry 0/1/2 y la idempotencia de COMMIT/CONSUME/REVERSE están probados sobre SQL; `Release_fingerprint_is_reproducible_from_the_contract_fixture` fija bytes y SHA-256 del release. | Revisión pendiente |
| CA-08 | Cumplido | Health, runbook, visibilidad base, superficie workload-only de transiciones, prohibiciones de rol, redelivery sin doble reserva y reconstrucción de saldos implementados y probados. El transporte HTTP de transiciones se prueba con tokens de workload reales (`BudgetTransitionE2ETests`: COMMIT canónico, replay, `409`, `403` de workload ajeno y de usuario sin identidad de workload, `400` de contrato y `503` de producer ausente/deshabilitado, sin movimientos), health reconstruye contra allocation current + deltas sin falso `BUDGET_LEDGER_CORRUPTED` tras una revisión de allocation, y la carrera dedicada de dos instancias sobre un mismo attempt reserva y señala una sola vez. | Revisión pendiente |

## Desviaciones y bloqueos

- **Rondas adicionales autorizadas: 7.** El usuario autorizó la ronda final tras el residual de la
  ronda 4 ("dale una ronda final", 2026-09-15). Las rondas 5, 6 y 7 se ejecutaron como
  continuación del mismo mandato de cierre, cada una verificando solo el hallazgo residual de la
  anterior (heartbeat, titularidad del checkpoint y reloj del servidor tras el lock). El contrato,
  el digest y la superficie HTTP no cambiaron.
- **Causa raíz de las rondas 5–7 y vectores que la fijan.** El lease se implementó por capas
  (claim→terminal, renovación, fencing, heartbeat) sin una única comprobación de titularidad en la
  frontera de escritura, y cada revisión expuso la siguiente ventana. Cada comportamiento queda
  ahora fijado por una regresión: `Two_instances_racing_the_same_attempt_reserve_and_signal_once`
  (claim concurrente), `A_stale_fence_cannot_confirm_a_movement` (fence obsoleto),
  `A_redelivered_reservation_reuses_its_operations_without_double_booking` (artefactos de replay),
  `The_lease_is_renewed_while_an_effect_is_blocked` (heartbeat), `A_reclaimed_lease_is_never_overwritten_by_a_stale_worker`
  (titularidad del checkpoint tras reclaim) y `The_heartbeat_does_not_revive_an_expired_lease`
  (reloj del servidor tras el lock), esta última validada en negativo contra el reloj previo.
- **Correcciones exigidas por la revisión independiente (rondas 1–7).** Los hallazgos R8–R11
  señalaron: un attempt podía quedar `SIGNALLING` para siempre si el proceso moría tras confirmar
  la señal; el lease de 30 s no se renovaba cada ≤10 s ni se comprobaba antes de escribir; health
  comparaba con el snapshot del último movimiento en lugar de la reconstrucción contractual (falso
  `BUDGET_LEDGER_CORRUPTED` tras una revisión de allocation); y CA-01/CA-02/CA-03/CA-07 seguían
  parciales. Todo se corrigió dentro del contrato en CP-05, con regresiones dedicadas; el request
  HTTP y los digests no cambian.
- **Dos defectos internos corregidos al cerrar la carrera de T-08.** La prueba dedicada de dos
  instancias reprodujo que el perdedor del claim abortaba con `DbUpdateConcurrencyException` en
  lugar de omitir el attempt, y mostró que el lease se liberaba antes de la señal, dejando el
  attempt en vuelo reclamable por otra instancia. Ambos son corregidos dentro del contrato (el
  request HTTP no cambia): el claim perdido devuelve sin postear y el lease cubre claim→terminal.
  La recuperación de un worker muerto sigue siendo por vencimiento de lease (30 s, dentro del
  presupuesto de 60 s del processor).
- **Runner `dotnet test`.** Igual que en SPEC 06/07, el wrapper de Microsoft.Testing.Platform
  reporta 0 pruebas en este entorno; toda la evidencia usa las asambleas compiladas directamente.

## Verificación independiente

> **Resultado:** Sin bloqueos
> **Rondas:** 7/7
> **Triaje:** R8 (recuperación de señal confirmada) y R10 (health falso corrupto) resueltos en la ronda 2; R11 (CA-01/02/03/07 sin evidencia) resuelto entre las rondas 2 y 4; R9 resuelto tras la ronda 6 (heartbeat independiente con reloj del servidor leído tras el lock, titularidad validada antes de cada checkpoint, fence transaccional en todos los retornos y artefactos de replay completos). Sin hallazgos abiertos; ninguno descartado.
> **Modelo efectivo:** `openai-codex/gpt-5.6-sol` (subagente `sdd-implementation-reviewer`, effort high; 8 + 6 + 7 + 9 + 11 + 7 + 7 turnos).
> **Método:** revisión full base `534a7c83b42f2727a143298d77b2a2fd47d0536d` → `329e842` y diferenciales `329e842` → `013da5f`, `013da5f` → `b645b3f`, `b645b3f` → `4ab7041`, `4ab7041` → `bbd255f`, `bbd255f` → `f44c1bb`, `f44c1bb` → `10c06df` y PASS sobre el candidato `95b3012`, con las suites completas de cada candidato aportadas por el orquestador.
> **Fecha:** 2026-09-15
