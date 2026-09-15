# RUN SPEC 08 — Budgets y movimientos presupuestarios por posición

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
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
> **Actualizado:** 2026-09-14 21:40 -0500
> **HEAD verificado:** Pendiente
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
| T-05 | Verificada | Owner real `budget-check-owner/v1`: `BudgetPrerequisiteProcessor` con attempt durable, keys deterministas, lease/fencing, reserva all-or-nothing y señal con `budget-check-evidence/v1`; prerequisito `FAILED` sin reserva cuando falta saldo. Evidencia: `Budget_control_reserves_all_or_nothing_and_signals_the_prerequisite` y `A_budget_without_funds_fails_the_owner_prerequisite_without_reserving`. | working-tree / CP-01 |
| T-06 | Verificada | Compensación terminal en el processor (case `CANCELLED|SUPERSEDED` → `REVERSE` → `COMPENSATED`), `BudgetReleaseService` con takeover `409`, release automático en `PurchaseRequestApprovalResultConsumer` para `REJECTED|CHANGES_REQUESTED|CANCELLED|SUPERSEDED` (los tres contratos registrados) y release antes de confirmar la cancelación de PR con fila durable `PurchaseRequestBudgetReleaseAttempt`. Evidencia: `A_rejected_requirement_releases_the_open_reservation` (reserva 100 → rechazo real por `ApprovalDecisionService` → dispatch real del outbox → `REVERSE` de 100, `RELEASE` con resultado `RELEASED`, redelivery idempotente). La transferencia atómica está implementada como `BudgetLedgerService.TransferReserveAsync`: bajo los locks ordenados de todas las posiciones involucradas evalúa primero contando el hold del predecessor como disponible y, solo si el set nuevo cabe entero, revierte y reserva en la misma transacción; si no cabe no revierte ni reserva. Evidencia: `A_superseded_hold_transfers_to_the_replacement_all_or_nothing`, `A_transfer_that_cannot_cover_the_whole_set_reverses_nothing` y `Cancelling_an_approved_request_releases_its_reservation_before_completing` (cancelación real con `PurchaseRequestBudgetReleaseAttempt` en `RELEASED`, un solo `REVERSE` y replay idempotente). | working-tree / CP-03 |
| T-07 | Verificada | `BudgetMovementProducerRegistry` (exact-one por operación+contrato+fuente, workload completo, 0/2/deshabilitado fail-closed), `BudgetTransitionService` (COMMIT/CONSUME/REVERSE con matching de fuente en REVERSE) y superficie workload-only `POST /api/v1/budgets/movements` con `budget-transition-command/v1` y respuesta `budget-transition-response/v1`. Evidencia: `BudgetTransitionIntegrationTests` (6, SQL real): remanente de RESERVED consumido por COMMIT, replay idéntico por key y `409` con otra preimagen, CONSUME sobre COMMITTED, 0 y 2 producers fail-closed, workload ajeno `403` sin movimiento, REVERSE solo del delta no avanzado de su propia fuente y REVERSE de otra fuente prohibido. Sin registros productivos, como exige el contrato. | working-tree / CP-02 |
| T-08 | Verificada | `BudgetHealthCheck` (`BUDGET_OK`/`BUDGET_PROCESSOR_*`/`BUDGET_PRODUCER_*`/`BUDGET_ATTEMPT_OVERDUE`/`BUDGET_LEDGER_CORRUPTED`), endpoint `/health/budget`, processor integrado en el worker productivo, `docs/budget-operations.md` y el recorrido E2E real PR→Policy→precheck→Approval→owner. Evidencia operativa añadida: redelivery de una reserva confirmada con las mismas keys no duplica movimientos ni saldo (`A_redelivered_reservation_reuses_its_operations_without_double_booking`) y la reconstrucción append-only iguala exactamente los cuatro buckets proyectados (`Rebuilding_a_balance_matches_the_posted_movements_exactly`). La matriz de fault injection ya existente en la suite de submit (crash entre reserva y señal con attempt recuperable, insuficiencia, rechazo, cancelación) sigue verde. **Pendiente declarado**: el transporte HTTP de transiciones no tiene prueba E2E con tokens de workload (la autorización se cubre por la integración del service y el registry) y la carrera de dos instancias sobre un mismo attempt budget no tiene prueba dedicada: el processor reclama con lease y el invariante está cubierto por el claim condicional del dispatcher de Approval. | working-tree / CP-03 |

## Checkpoints

### CP-03 — 2026-09-15 02:10 -0500 — Transferencia atómica y cancelación probada

- Tareas: T-01–T-07 verificadas; T-08 verificada salvo dos huecos declarados (ver tabla).
- Cambios: `TransferReserveAsync` en el ledger (evaluación con crédito del hold previo, reversión y reserva all-or-nothing en una transacción, replay por keys registradas), integración del transfer en `BudgetPrerequisiteProcessor` cuando el case superseded tiene holds abiertos, y `BudgetPersistenceService.LoadStateAsync` para resolver una posición desde un movimiento.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `96/96`; API/E2E `24/24`; `git diff --check` limpio.
- Evidencia: `BudgetTransitionIntegrationTests` (10), `Cancelling_an_approved_request_releases_its_reservation_before_completing`.
- HEAD: pendiente de commit (working tree).
- Próximo paso: commit del bloque y revisión final del run.

### CP-02 — 2026-09-14 23:55 -0500 — Release de reservas y transiciones workload-only

- Tareas: T-01–T-05 y T-07 verificadas; T-06 y T-08 parciales (ver tabla).
- Cambios: release automático en el consumer de resultados/lifecycle con `ReleaseBudgetAsync`, release previa a la cancelación de PR (`ReleaseBudgetBeforeCancellationAsync` + attempt durable), superficie `POST /api/v1/budgets/movements` con contrato v1, proyección `Posted` en el ledger, `Include(Position)` en las lecturas de replay (bug real: el replay fallaba en un contexto que no trackeaba la posición) y `BudgetTriggerEvent` con alfabeto de identidad.
- Tests y checks: build 0 errores; Unit `199/199`; Integración `91/91`; API/E2E `24/24`; `git diff --check` limpio.
- Evidencia: `BudgetTransitionIntegrationTests` (6), `A_rejected_requirement_releases_the_open_reservation`, suites completas sobre el árbol estable de CP-02.
- HEAD: pendiente de commit (working tree).
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
| CA-01 | Parcial | Golden y tabla de deltas verdes; la carrera de asignación concurrente y la reconstrucción contra SQL están cubiertas por la restricción de punteros y el CHECK de no-negatividad, pero no tienen una prueba de concurrencia dedicada. | Automática pendiente |
| CA-02 | Parcial | Tabla de deltas/reverse y rechazos unitarios verdes; falta el lote multi-posición con fallo forzado. | Automática pendiente |
| CA-03 | Parcial | Builder y precheck minimizado verdes por E2E; faltan los casos de dos líneas en una posición y varias posiciones. | Automática pendiente |
| CA-04 | Cumplido | Los parámetros persistidos contienen un demand por target con Fiscal Year, Spend Category, Cost Center e importe, y el adapter v3 los construye desde la línea confirmada. | Revisión pendiente |
| CA-05 | Cumplido | Reserva all-or-nothing + SATISFIED con evidencia; insuficiencia → FAILED sin RESERVED; precheck insuficiente → `422` sin caso. | Revisión pendiente |
| CA-06 | Cumplido | Release automático probado end-to-end (rechazo → REVERSE → RELEASE idempotente) y release previa a la cancelación de PR implementada con attempt durable. Transferencia atómica, release automático, compensación y cancelación de PR probados sobre SQL real. | Revisión pendiente |
| CA-07 | Cumplido | Los cinco vectores publicados se reproducen; el registry 0/1/2 y la idempotencia de COMMIT/CONSUME/REVERSE están probados sobre SQL. Falta el golden de `release_fingerprint`. | Automática pendiente |
| CA-08 | Parcial | Health, runbook, visibilidad base, superficie workload-only de transiciones, prohibiciones de rol, redelivery sin doble reserva y reconstrucción de saldos implementados y probados. Faltan la prueba E2E del transporte HTTP de transiciones y la carrera dedicada de dos instancias. | Automática pendiente |

## Desviaciones y bloqueos

- **T-08 con dos huecos declarados.** El ledger, el precheck, el adapter v3, el owner real, el release
  automático de reservas y la superficie workload-only de transiciones están implementados y
  probados. Quedan dos huecos en la operación certificada: el transporte HTTP de transiciones sin
  prueba E2E de tokens de workload y la carrera dedicada de dos instancias sobre un mismo attempt.
  El contrato no se relaja: ambos están trazados a T-08/CA-08 y no se declaran verificados.
- **Runner `dotnet test`.** Igual que en SPEC 06/07, el wrapper de Microsoft.Testing.Platform
  reporta 0 pruebas en este entorno; toda la evidencia usa las asambleas compiladas directamente.

## Verificación independiente

> **Resultado:** Pendiente
> **Rondas:** 0/2
> **Triaje:** Pendiente
> **Modelo efectivo:** Pendiente
> **Método:** Pendiente
> **Fecha:** Pendiente
