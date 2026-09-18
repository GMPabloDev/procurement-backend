# RUN SPEC 11 — Purchase Orders, amendments y compra directa

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/11-purchase-orders-amendments-compra-directa.md
> **Revisión contractual:** 1
> **Commit de la spec:** 722419986d17ea46031cce4ce17d2c9a19df42a4
> **Blob aprobado:** 08cb2d2fe24828e4585594cc59b80cc8fbd6c792
> **Digest contractual:** 3df90fea3a48e59ec84b57a248c6d29cd5c7eae2a10b865ea44f7d69aa3f5cf9
> **Rama base:** main
> **Commit base:** 722419986d17ea46031cce4ce17d2c9a19df42a4
> **Rama de implementación:** spec-11-purchase-orders-amendments-compra-directa
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-18 09:40 -0500
> **Actualizado:** 2026-09-18 14:40 -0500
> **HEAD verificado:** Pendiente
> **Commit de integración:** Pendiente

## Línea base

- `dotnet build ProcureToPay.sln --nologo -v q` → 0 errores, 35 avisos preexistentes (xUnit/EF1002/EF1003 en suites de Suppliers, ReferenceCatalogs, Sourcing y Budget).
- `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` → 293/293 correctos antes de tocar código.
- `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore --filter-class "*Sourcing*"` → 51/51 correctos (Docker/Testcontainers, migración real) tras el cambio de takeover; línea base completa no re-ejecutada.
- `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` → 33/33 correctos.
- Nota: `ProcureToPayDbContextFactory` (diseño) usa autenticación integrada y no conecta en Linux; `dotnet ef migrations add` funciona sin conexión, `remove` requiere `PROCURETOPAY_DESIGN_CONNECTION` (parche local no versionado, revertido).

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Núcleo PO, claim y takeover por línea: dominio `src/ProcureToPay.Domain/Modules/PurchaseOrders/{PurchaseOrderCodes,PurchaseOrderEnums,PurchaseOrderModels,VendorTermsSnapshot,PurchaseOrderClaim,PurchaseOrderCanonicalizer,PurchaseOrderFingerprints,AcceptanceResponsibilityBuilder}.cs`, persistencia `src/ProcureToPay.Infrastructure/Persistence/PurchaseOrders/{PurchaseOrderPersistenceModels,PurchaseOrderModelConfiguration,PurchaseOrderSerialization,PurchaseRequestLineTakeoverService,PurchaseOrderClaimService}.cs`, migración `20260918153719_Spec11PurchaseOrderCore` (constraints, triggers append-only, expansión 1:1 de takeovers legacy), cableado de SPEC 10 al takeover por línea (`SourcingProcessService`, `SourcingAwardService`, `SourcingPrerequisiteProcessor`) y guarda de PR (`PurchaseRequestPersistenceService`: `PR_REQUEST_CONSUMED`). Pruebas: `PurchaseOrderContractTests` (22 unitarias) y `PurchaseOrderClaimIntegrationTests` (8 SQL: claim único, replay, conflicto de preimagen, cobertura parcial, release pre-issue con re-claim, recovery REOPEN/CANCEL, append-only, carrera concurrente). | working-tree sobre 7224199 · CP-01 |
| T-02 | Verificada | Acceptance Responsibilities y términos: `AcceptanceResponsibilityBuilder` (kinds exactos por purchase type, default Requested For, override con motivo, rechazo de tipo ajeno), `VendorTermsSnapshot` (`vendor-terms-snapshot/v1` desde Supplier/award/catálogo con restatement del award y 422 por término contradictorio o moneda no soportada) y `PurchaseOrderDraftService` (sucesora DRAFT con delivery/assignments, usuarios activos de la organización, CAS de versión y punteros). Pruebas: unitarias de responsabilidades/términos y `PurchaseOrderDraftIntegrationTests` (3 SQL: sucesora con assignments, usuario inactivo/ajeno rechazado, bytes publicados no reescribibles). | working-tree sobre 7224199 · CP-01 |
| T-03 | Verificada | Código implementado y compilando: `PurchaseRequestOrderingEvidenceService` (produce `purchase-request-ordering-evidence/v1` server-side desde el case completado, sus requirements/decisions y los prerequisites satisfechos; persiste y rehashea), `PurchaseOrderApprovalAdapter` (descriptor `purchase-order-approval-adapter/v1`, requirement único `PROCUREMENT_APPROVER + PROCUREMENT` con scope ORGANIZATION, acciones APPROVE/REJECT/REQUEST_CHANGES y Buyer/requester excluidos, snapshot `purchase-order-approval-snapshot/v1`, exige evidencia financiera aprobada y cobertura exacta de targets), `PurchaseOrderIssuanceService.SubmitAsync` (DRAFT→PENDING_APPROVAL con caso de Approval y CAS de versión) y `PurchaseOrderResultConsumer` (APPROVED→APPROVED con `approval_ref`, REQUEST_CHANGES→DRAFT, resto→CANCELLED; ignora eventos de versiones superseded). Verificación: `PurchaseOrderIssuanceIntegrationTests` (submit real con el adapter: caso `ISSUE_PURCHASE_ORDER` con un requirement `PROCUREMENT_APPROVER` sobre la línea exacta, estado `PENDING_APPROVAL` sin `approval_ref`, resultado `APPROVED` proyectado por el consumer, y rechazo del submit cuando el caso de la PR no está completado). | working-tree sobre 7224199 · CP-03 |
| T-04 | Verificada | Código implementado y compilando: `PurchaseOrderBudgetProducer` (resolver de reserva exact-one por target con remanente, `COMMIT` all-or-nothing vía `BudgetTransitionService` con `command_version=budget-transition-command/v1` y source id/version/digest de la PO, `REVERSE` del remanente con las mismas keys, `PurchaseOrderBudgetAttempt` durable por operación con replay) y `PurchaseOrderIssuanceService.IssueAsync` (exige APPROVED + claim vivo, emite `ISSUED` con refs de presupuesto e `issued_at`, avanza punteros de línea, proyecta `ORDERED` sin retroceso y publica outbox; recupera por key). Registro productivo en `appsettings.json` (`Approval:Workloads` y `Budget:MovementProducers` COMMIT/REVERSE `PURCHASE_ORDER`). Verificación: `PurchaseOrderIssuanceIntegrationTests` con reserva real (`REQUESTED`+`RESERVED` sobre la posición de cost center/spend category del catálogo): un `COMMITTED` por el importe exacto, `REVERSE` del remanente (`50 − 20`), balance `Committed=20/Reserved=0`, claim `ISSUED`, proyección `ORDERED` y refs `COMMIT`+`REVERSE` en la versión `ISSUED`. | working-tree sobre 7224199 · CP-03 |
| T-05 | Pendiente | — | — |
| T-06 | Pendiente | — | — |
| T-07 | Pendiente | — | — |
| T-08 | Pendiente | — | — |
| T-09 | Pendiente | — | — |

## Checkpoints

### CP-03 — (2026-09-18 14:40 -0500) · Bloque 2 (T-03, T-04) verificado

árbol probado: `working-tree sobre 7224199` (sin commit).
  - Tests ejecutados sobre ese árbol: unitarias 316/316; integración `*PurchaseOrder*` 13/13 (SQL Server 2022 con Testcontainers y migración real); `dotnet build ProcureToPay.sln --no-restore` → 0 errores.
  - Defectos corregidos durante el bloque: la preimagen de `purchase-order-version/v1` serializaba `delivery` como cadena embebida en vez de objeto (rompía la reproducción del documento y el digest); `checked_at` de la evidencia de ordenamiento usaba el reloj del comando (dos producciones del mismo caso daban digests distintos) y ahora se deriva del instante persistido del caso; el adapter exigía requester≠originator cuando el Buyer es ambos (se resuelve con exclusión explícita en el requirement); el requirement de Procurement necesita `AuthorityRequirement` del tipo `Procurement` con el importe base de la orden; el `REVERSE` del remanente reutiliza el source del padre (`PURCHASE_REQUEST`) conforme a SPEC 08, lo que exige registrar el producer de la PO también para ese source.
  - Decisión interna reversible: la evidencia de ordenamiento se produce y se liga en el submit (`ordering_evidence_ref` de la versión `PENDING_APPROVAL`), y el adapter la reproduce server-side; el `material_snapshot_digest` de cada target se resuelve desde el caso completado, nunca desde la PO.
  - Brecha conocida y acotada: los resultados de Approval del subject `PURCHASE_ORDER_AMENDMENT` se registran pero no se proyectan; pertenece a T-05 (amendments), aún pendiente.
  - Pendiente inmediato: T-05 amendments, T-06 Direct Purchase, T-07 documents owner + worker, T-08 API/límites, T-09 migración/health/runbook/E2E.

### CP-02 — (2026-09-18 13:10 -0500) · Bloque 2 (T-03, T-04) parcial

árbol probado: `working-tree sobre 7224199` (sin commit).
  - Tests ejecutados sobre ese árbol: unitarias 315/315; integración completa 189/189 (SQL Server 2022 + LocalStack con Testcontainers); API/E2E 33/33; `dotnet build ProcureToPay.sln --no-restore` → 0 errores; `git diff --check` limpio.
  - Alcance real: T-03 y T-04 tienen el código completo en compilación y cableado en DI/configuración, pero **no** tienen todavía pruebas dirigidas que los certifiquen; CA-04 y CA-05 siguen pendientes. No se declara ninguno como verificado.
  - Pendiente inmediato de T-03/T-04: prueba de integración del productor de ordering evidence, del snapshot del adapter, del submit (PENDING_APPROVAL + caso de Approval), del consumer de resultados (APPROVED/CHANGES_REQUESTED/REJECTED) y de la emisión con una reserva real (COMMIT + REVERSE del remanente, recuperación del attempt y proyección `ORDERED`).
  - Brecha conocida y acotada: los resultados de Approval del subject `PURCHASE_ORDER_AMENDMENT` se registran pero no se proyectan; la proyección pertenece a T-05 (amendments), aún pendiente.
  - Decisión interna reversible: la evidencia de ordenamiento se produce en el módulo de Purchase Orders leyendo las tablas de Approval (no se añadió un servicio nuevo a SPEC 06), y el `material_snapshot_digest` de cada target se resuelve desde el case completado, nunca desde la PO.

### CP-01 — (2026-09-18 11:30 -0500) · Bloque 1 (T-01, T-02)

árbol probado: `working-tree sobre 7224199` (sin commit).
  - Tests ejecutados: unitarias 315/315 correctos (22 nuevas de PurchaseOrders); integración `*Sourcing*` 51/51, `*PurchaseRequest*` 11/11, `*PurchaseOrder*` 11/11 sobre SQL Server 2022 con Testcontainers y la migración `Spec11PurchaseOrderCore` real; API/E2E 33/33. `dotnet build ProcureToPay.sln --no-restore` → 0 errores.
  - Decisión interna reversible: `award-consumption-claim/v1` exige cobertura exacta del award (la verificación de conjunto completo se hace contra `AwardCandidate.Lines` del award, no contra la respuesta filtrada del verifier).
  - Decisión interna reversible: el replay de un claim no vuelve a consumir el award; la response verificada se persiste en `AwardConsumptionClaims.AwardSnapshotJson` y se devuelve tal cual (evita un segundo audit `SOURCING_AWARD_CONSUMED`, que ahora es idempotente por effect key).
  - Decisión interna reversible: `purchase-request-line-takeover/v1` es autoritativo para Sourcing, el processor de prerequisites y la guarda de PR; las filas request-wide quedan históricas y la migración las expande 1:1.
  - Decisión interna reversible: forma de `policy_bundle_ref` = `policy-evaluation-ref/v1` (mismo documento que publica el award) y de `budget_operation_refs` = `{operation,operation_id,operation_key,source_digest,source_id,source_version}`; el enum de estados de PO no se publica en el wire.
  - Defectos corregidos durante el bloque: `PurchaseOrderVersion` no asignaba `PoId`; SQL de la migración con paréntesis erróneos en `LOWER(CONVERT(...,2))` y schema equivocado de `PurchaseRequestVersions`; lecturas con `FromSql` no componibles (se sustituyeron por `ExecuteSqlRaw` de fencing + LINQ); el trigger append-only de `AwardConsumptionClaims` impedía su máquina de estados (ahora guarda la identidad y permite solo el avance de estado).
  - Limitación de entorno registrada: el LSP del workspace no resuelve el namespace nuevo `ProcureToPay.Domain.Modules.PurchaseOrders` (diagnósticos obsoletos); la verificación se apoya en `dotnet build`/`dotnet test`.
  - Pendiente inmediato: Bloque 2 (T-03 adapter/lifecycle de Approval, T-04 producer Budget e issue saga).

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | `PurchaseOrderClaimIntegrationTests`: dos instancias sobre el mismo award producen un claim/PO único (carrera + índice único filtrado), replay estable, preimagen distinta `409`, cobertura parcial `409` sin segunda PO, cancelación pre-issue devuelve takeovers al award y permite un claim nuevo, recovery REOPEN/CANCEL sin claim previo, PR sin write-back de supplier. | suite integration PurchaseOrders |
| CA-02 | Cumplido | `PurchaseOrderContractTests` (DRAFT con delivery null/assignments vacíos, versiones append-only, `subtotal=source_gross_total` y taxes/charges/discounts=0 reproducibles, rechazo de override, digest estable ante permutación de set) y `PurchaseOrderClaimIntegrationTests`/`PurchaseOrderDraftIntegrationTests` (documento reproducible, trigger append-only, dos versiones sucesoras sin update/delete). | suite integration + unitarias |
| CA-03 | Cumplido | `PurchaseOrderContractTests` (GOOD/SERVICE/SUBSCRIPTION exigen sus kinds, default Requested For, override con motivo, tipo ajeno rechazado, términos reconstruidos con UTC/ISO 4217/decimales, contradicción y moneda no soportada `422`) y `PurchaseOrderDraftIntegrationTests` (usuario inactivo/ajeno/stale `422`). | suite integration + unitarias |
| CA-04 | Cumplido | `PurchaseOrderIssuanceIntegrationTests`: el adapter publica exactamente un requirement `PROCUREMENT_APPROVER` en stage `PROCUREMENT` con scope `ORGANIZATION`, acciones APPROVE/REJECT/REQUEST_CHANGES y Buyer excluido, sobre los targets resueltos desde el caso completado de la PR; el submit exige la autoridad financiera upstream (un caso no completado o una decisión no aprobada no presenta la orden) y no reutiliza una decisión de Sourcing. El snapshot `purchase-order-approval-snapshot/v1` liga PO/amendment, award, policy bundle, evidencia de ordenamiento, targets y términos. | suite integration PurchaseOrders |
| CA-05 | Cumplido | `PurchaseOrderIssuanceIntegrationTests`: la emisión ejecuta un `COMMIT` all-or-nothing del importe ordenado y libera por `REVERSE` el remanente reservado antes de crear la versión `ISSUED`; el `PurchaseOrderBudgetAttempt` durable registra ids/keys por operación, el resolver exige exactamente una cadena `RESERVED` abierta por target con remanente suficiente, y la proyección `ORDERED` y el outbox se escriben en la misma transacción. | suite integration PurchaseOrders |
| CA-06 | Pendiente | — | — |
| CA-07 | Pendiente | — | — |
| CA-08 | Pendiente | — | — |
| CA-09 | Pendiente | — | — |
| CA-10 | Pendiente | — | — |

## Desviaciones y bloqueos

- Ninguno.

## Verificación independiente

> **Resultado:** Pendiente
> **Rondas:** 0/2
> **Triaje:** Pendiente
> **Modelo efectivo:** Pendiente
> **Método:** Pendiente
> **Fecha:** Pendiente
