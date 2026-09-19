# Operación de Purchase Orders (SPEC 11)

Guía de despliegue, configuración y recuperación del módulo **Purchase Orders, amendments y compra
directa**. Complementa `docs/approval-operations.md`, `docs/budget-operations.md` y
`docs/purchase-request-operations.md`; no repite la operación de esos módulos.

## Componentes

| Componente | Rol operativo |
|---|---|
| `PurchaseOrderClaimService` | Consume un award current de SPEC 10 (`award-consumption-claim/v1`), transfiere takeovers de línea y crea la PO `DRAFT`. `award-recovery/v1` (`REOPEN`/`CANCEL`) sólo opera sin claim. |
| `PurchaseOrderDraftService` / `PurchaseOrderIssuanceService` | Sucesoras DRAFT, submit, cancelación pre-issue e `ISSUED` con `COMMIT` de Budget. |
| `PurchaseOrderAmendmentService` | Amendments append-only: reducción (`REVERSE` del compromiso), aumento (award sucesor + `COMMIT` del delta) y cancelación total. |
| `DirectPurchaseService` | Autoriza un máximo sin `COMMIT`; cancela con `budget-release-command/v1` y `trigger=DIRECT_PURCHASE_CANCELLED`. |
| `SupportingDocumentService` | Stage/confirmación de documentos (SHA-256 server-side) y URL temporal ≤15 min. |
| `SupportingDocumentOwnerProcessor` + `SupportingDocumentOwnerWorker` | Owner real de `supporting-document-owner/v1`: cuenta bytes únicos por target y emite `SATISFIED`. Worker dedicado, separado de Sourcing. |
| `PurchaseOrderBudgetProducer` | Producer exact-one `COMMIT/REVERSE + PURCHASE_ORDER`, intentos durables por key. |

## Configuración obligatoria

- `Approval:Workloads:n:{Issuer,ClientId}` debe incluir el workload del dominio
  (`internal://procure-to-pay` + `purchase-order-domain`) y el owner de documentos
  (`supporting-document-owner`). Ausencia o ambigüedad → `503`, nunca `ALLOW`.
- `Budget:MovementProducers:n` debe registrar `COMMIT` y `REVERSE` con
  `contract_version=v1`, `source_type=PURCHASE_ORDER`, `producer_id=purchase-order-domain` y el
  workload del dominio. El mismo producer se registra para `source_type=PURCHASE_REQUEST` porque el
  `REVERSE` del remanente reutiliza la identidad del padre (SPEC 08).
- `Storage:S3:*` (bucket, extensión y `TemporaryUrlLifetimeMinutes` ≤ 15) para documentos.
- `PurchaseOrders:SupportingDocumentWorkerIntervalSeconds` (default 5) controla el barrido del worker.

## Orden de despliegue

1. Migración `Spec11PurchaseOrderCore` aplicada (crea schema `PurchaseOrders`, triggers append-only,
   constraints de versión monotónica y expansión 1:1 de takeovers legacy). La migración **no** crea POs
   desde awards históricos.
2. Contratos y readers (versiones, claims, amendments, authorizations) sin habilitar commands.
3. `PurchaseOrderApprovalAdapter` / `PurchaseOrderAmendmentApprovalAdapter` / verificador de awards /
   owner de documentos registrados pero sin commands expuestos.
4. `SupportingDocumentProcessorRegistration` con `{adapter_id=supporting-document-owner,
   adapter_version=v1, processor_id, workload_issuer, workload_client_id}` exactamente una vez y
   coincidente con el `owner_workload` que Approval ya resolvió.
5. Budget producer habilitado.
6. Commands de draft/documentos; después issue/amendment/Direct Purchase.
7. Worker de documentos (`SupportingDocumentOwnerWorker`) y health checks.

Hasta habilitar cada pieza, su ausencia devuelve `503` (`/problems/purchase-order-dependency-unavailable`),
nunca `ISSUED`/`AUTHORIZED`/`SATISFIED`.

## Health y preflight

- `GET /health/ready` incluye `supporting-documents`: storage accesible, registro exact-one del
  processor, binding al owner workload resuelto y backlog ≤ 60 s.
- Códigos publicados: `SUPPORTING_DOCUMENT_STORAGE_UNAVAILABLE`,
  `SUPPORTING_DOCUMENT_PROCESSOR_REGISTRATION_INVALID`, `SUPPORTING_DOCUMENT_PROCESSOR_BINDING_MISMATCH`,
  `SUPPORTING_DOCUMENT_ATTEMPT_OVERDUE`. Nunca incluyen ids, rutas, importes ni digests.
- Antes de habilitar commands verifica: SPEC 01–10 integradas, migraciones aplicadas, cero claims
  duplicados por award/línea, cero cadenas `RESERVED` ambiguas por target y expansión 1:1 de takeovers
  legacy sin solapamiento.

## Recuperación operativa

- **Claim / takeover**: un replay del mismo `claim_key` devuelve el claim registrado; otra preimagen,
  award ya reclamado o cobertura parcial → `409` sin segunda PO. Cancelación pre-issue devuelve los
  takeovers al award (permite re-claim). `award-recovery/v1` sólo aplica **sin** claim.
- **COMMIT / REVERSE**: `PurchaseOrderBudgetAttempt` es durable por `(po, versión, operación)`; un
  intento `PENDING/COMMITTING/RELEASING` se recupera por su key y nunca repite el efecto confirmado. Un
  `OperationId`/`MovementRefsJson` presente indica efecto confirmado: se finaliza, no se re-postea.
- **Signal de documentos**: un crash entre el signal confirmado y el checkpoint terminal se recupera
  leyendo `ApprovalPrerequisiteSignals` por el `SignalKey` del intento; el signal no se reenvía.
- **Leases**: lease 30 s con heartbeat 10 s y `FencingToken`. Un intento cuyo `LeaseExpiresAt` venció
  es reclamable por otra instancia; el holder antiguo no puede escribir (fence + recarga bajo gate).
- **Ambigüedad de reservas**: el resolver exige exactamente una cadena `RESERVED` abierta por target con
  remanente suficiente. Cero o dos cadenas, movement de otra organización/source o suma insuficiente
  fallan cerrado (`503`) y no se resuelven “eligiendo la más reciente”.
- **Evidencia corrupta**: un documento cuyo `content_digest` no se reproduce deja el intento sin señal
  (`FAILED`, `DOCUMENT_CORRUPTED`) y el prerequisite en `WAITING`. **No** edites filas ni digests para
  desbloquear: corrige el origen (nuevo documento confirmado) y deja que el worker reprocese.

Ninguna recuperación requiere `UPDATE` manual de filas históricas: todas las fronteras usan
keys/fingerprints, attempts con lease/fencing, inbox/outbox y CAS de pointers.

## Rollback

- Detén los commands (claims, issue, amendments, Direct Purchase, stage/confirm) y conserva readers,
  worker y recuperación de intentos ya confirmados.
- Tras el primer `COMMIT`, `signal` o `authorization` **no** hay downgrade destructivo: se corrige hacia
  adelante manteniendo los consumers de contratos v1.
- La migración no elimina filas históricas; un rollback de esquema deja la historia inconsistente y no
  está soportado.

## Rotación de credenciales

- Los workloads se identifican por `issuer + client_id` allowlisted y estables ante rotación: rota la
  credencial del IdP, no el par `issuer/client_id`.
- `SupportingDocumentProcessorRegistration` y los `owner_workload` persistidos en
  `ApprovalPrerequisites` deben seguir coincidiendo; un cambio de `processor_id` o workload exige
  registrarlo de nuevo y desplegar el owner correspondiente.
- Rotar las claves S3 no cambia object keys ni digests; los documentos confirmados no se reescriben.

## Límites y errores contractuales

- 500 líneas por PO/takeover, 100 documentos por caso, 20 MiB por archivo, 5 MiB por documento
  canónico, keys de 1–128 `[A-Za-z0-9._:-]`, motivos/ubicación de 1–1.000 Unicode scalars, URL de
  descarga ≤15 min.
- Problem Details: `400` JSON/contrato, `401`, `403` autorización/rol, `404` fuera de scope,
  `409` key/versión/takeover/estado, `413` límites, `422` elegibilidad/términos/assignments/evidencia/importe,
  `503 /problems/purchase-order-dependency-unavailable`.

## Telemetría

Logs y trazas no incluyen Supplier, importes, ubicación de entrega, nombres de archivo, motivos,
user ids, tokens, URLs, payloads ni digests completos. Si necesitas correlación usa ids opacos, etapa,
estado, retry y duración.
