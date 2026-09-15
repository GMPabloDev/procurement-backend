# Operación de Budgets (SPEC 08)

Runbook del ledger presupuestario por posición (`Cost Center + Fiscal Year + Spend Category`), sus
movimientos y el owner real del prerequisito `REQUIRE_BUDGET_CHECK` de Approval.

## Despliegue

1. **Schema primero.** Aplicar la migración `Spec08BudgetLedger` (tablas del schema `Budget`:
   `Positions`, `AllocationVersions`, `Balances`, `Operations`, `Movements`,
   `PrerequisiteAttempts`, `PurchaseRequestReleaseAttempts`, `MovementProducerRegistrations`,
   `PrerequisiteProcessorRegistrations`, `AuditRecords`). Es aditiva y su `Down` lanza
   `NotSupportedException`: revertir la aplicación conserva el ledger.
2. **Registros exact-one después.** Sin owners que sostengan el prerequisito, un submit que requiera
   presupuesto responde `503` sin crear caso. Registrar:
   - `Approval:OwnerWorkloads:N:AdapterId=budget-check-owner` con `AdapterVersion=v1` y el
     `Issuer`/`ClientId` del workload que ejecuta el processor.
   - Ese mismo workload en `Approval:Workloads:M`.
3. **Asignaciones antes del tráfico.** Ninguna posición existe hasta que un `ADMIN` organizacional
   registra su primera asignación con
   `PUT /api/v1/budgets/positions/{costCenterId}/{fiscalYear}/{spendCategoryCode}`. Una posición sin
   asignación responde `UNFUNDED` en el precheck, nunca `AVAILABLE`.
4. **Adapters.** Las submissions nuevas usan `policy-approval-adapter/v3`; los attempts persistidos
   con `v2` siguen reanudándose con `v2`.
5. **Worker.** El worker productivo ejecuta el processor de prerequisitos budget en el mismo sweep
   que el dispatcher y las reconciliaciones. Sin worker no hay señal: el prerequisito queda
   `WAITING` y el caso no completa.

## Identidades y contratos

- Posición: `budget-position-key/v1` sobre `policy-canonical-json/v1`; su SHA-256 es único por
  organización.
- Parámetros del prerequisito: `budget-check-owner/v1`
  (`{base_currency,contract_version,demands}`), un demand por target con importe, Cost Center,
  Fiscal Year, Spend Category, línea origen y target material completo.
- Evidence: `budget-check-evidence/v1`; su SHA-256 es lo que se publica a Approval como
  `evidence_digest`, y `evidence_reference` es `budget://<attempt_id>`.
- Transiciones: `budget-transition-command/v1` (`COMMIT|CONSUME|REVERSE`), release
  `budget-release-command/v1`.
- Vectores dorados: `tests/ProcureToPay.UnitTests/Budget/BudgetCanonicalizationGoldenTests.cs`.

## Diagnóstico

- `GET /health/budget` devuelve `BUDGET_OK` o el código de degradación:
  `BUDGET_PROCESSOR_UNAVAILABLE`, `BUDGET_PROCESSOR_AMBIGUOUS`, `BUDGET_PRODUCER_AMBIGUOUS:<OP>`,
  `BUDGET_PRODUCER_INVALID:<OP>`, `BUDGET_ATTEMPT_OVERDUE`, `BUDGET_LEDGER_CORRUPTED`.
- `BUDGET_ATTEMPT_OVERDUE` significa que un prerequisito debía señalar o compensar hace más de 60 s.
  Revisar el worker y la ventana de lease; un attempt reclamado por un lease expirado se reintenta
  solo, reutilizando operaciones y signal key.
- `BUDGET_LEDGER_CORRUPTED` significa que la proyección mutable (`Balances`) no coincide con el
  último movimiento. No editar saldos a mano: comparar con la reconstrucción del ledger
  (`BudgetPersistenceService.RebuildAsync`) y corregir hacia adelante.
- `BUDGET_PRODUCER_*` significa que la configuración `Budget:MovementProducers` tiene una entrada
  duplicada, vacía o con operación/fuente fuera de la tabla
  (`COMMIT+PURCHASE_ORDER`, `CONSUME+INVOICE`, `REVERSE` con la fuente de su padre).

## Recuperación

- **Insuficiencia empresarial.** El precheck queda auditado como operación `PRECHECK` y el submit
  responde `422 /problems/budget-insufficient` sin caso. Corregir la asignación (o el importe) y
  reintentar con la misma submission key: el attempt se reanuda.
- **Fallo técnico del owner.** El prerequisito permanece `WAITING` y el attempt en `PENDING` con
  `last_error_code` y `next_attempt_at`. El retry reutiliza la misma operación y signal key, así que
  no duplica reservas.
- **Crash entre reserva y señal.** El attempt queda `RESERVED`; el siguiente sweep reutiliza la
  reserva y señala. Si el caso ya terminó, el attempt compensa con `REVERSE` y pasa a
  `COMPENSATED`.
- **Cancelación con reserva viva.** Una reserva ya `COMMITTED|CONSUMED` es un takeover downstream:
  el revisor o el intento de cancelación recibe `409` hasta que el dominio propietario revierta.
- **Reversión de la aplicación.** Detener submissions y nuevos movimientos; mantener readers,
  worker, compensador y consumers hasta drenar attempts y reservas. Conservar la historia; no hay
  down migration destructiva ni edición manual de saldos, punteros o digests.
