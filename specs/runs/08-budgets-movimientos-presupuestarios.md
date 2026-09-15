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
- Unitarias: `199/199` correctas, 0 con errores.
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
| T-06 | Parcial | Compensación terminal implementada en el processor (case `CANCELLED|SUPERSEDED` → `REVERSE` → `COMPENSATED`) y `BudgetReleaseService` disponible con takeover `409`. **Falta**: el consumer automático de `approval-result/v2|v3` y `approval-case-lifecycle/v1` que dispara la release, la transferencia atómica predecessor→replacement en supersesión y el camino de cancelación de PR. No probado end-to-end. | working-tree / CP-01 |
| T-07 | Parcial | `BudgetMovementProducerRegistry` (exact-one por operación+contrato+fuente, workload completo, 0/2/deshabilitado fail-closed) y `BudgetTransitionService` (COMMIT/CONSUME/REVERSE con matching de fuente en REVERSE) implementados y compilando. **Falta**: la superficie HTTP workload-only y sus pruebas con producers controlados. Sin registros productivos, como exige el contrato. | working-tree / CP-01 |
| T-08 | Parcial | `BudgetHealthCheck` (`BUDGET_OK`/`BUDGET_PROCESSOR_*`/`BUDGET_PRODUCER_*`/`BUDGET_ATTEMPT_OVERDUE`/`BUDGET_LEDGER_CORRUPTED`), endpoint `/health/budget`, processor integrado en el worker productivo, `docs/budget-operations.md` y el recorrido E2E real PR→Policy→precheck→Approval→owner. **Falta**: fault injection explícita, escenarios de dos instancias/lease expirado y la prueba de rollback. | working-tree / CP-01 |

## Checkpoints

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
| CA-06 | Pendiente | Falta el release automático (consumer de resultados/lifecycle), la transferencia en supersesión y la cancelación de PR. | Automática pendiente |
| CA-07 | Parcial | Los cinco vectores publicados se reproducen; falta el registry 0/1/2 de producers y el golden de `release_fingerprint`. | Automática pendiente |
| CA-08 | Parcial | Health, runbook y visibilidad base implementados; faltan fault injection, dos instancias/lease expirado y rollback. | Automática pendiente |

## Desviaciones y bloqueos

- **T-06/T-07/T-08 parciales.** El ledger, el precheck, el adapter v3 y el owner real están
  implementados y probados end-to-end. Quedan tres bloques de trabajo identificados: el consumer
  automático de resultados/lifecycle que dispara la release, la transferencia atómica
  predecessor→replacement en supersesión con el camino de cancelación de PR, y la superficie
  workload-only de transiciones con sus pruebas de fault injection, dos instancias y rollback.
  El contrato no se relaja: esas partes están trazadas a T-06/T-07/T-08 y a CA-06/CA-07/CA-08 y no
  se declaran verificadas.
- **Runner `dotnet test`.** Igual que en SPEC 06/07, el wrapper de Microsoft.Testing.Platform
  reporta 0 pruebas en este entorno; toda la evidencia usa las asambleas compiladas directamente.

## Verificación independiente

> **Resultado:** Pendiente
> **Rondas:** 0/2
> **Triaje:** Pendiente
> **Modelo efectivo:** Pendiente
> **Método:** Pendiente
> **Fecha:** Pendiente
