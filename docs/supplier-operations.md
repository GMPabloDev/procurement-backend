# Operación de Supplier Master y catálogo homologado (SPEC 09)

Este runbook cubre el despliegue, la operación diaria y la recuperación del módulo Supplier: maestro
de proveedores versionado, datos bancarios cifrados y catálogo de proveedores homologados con sus
acuerdos externos. No sustituye al contrato: cualquier discrepancia se resuelve con
`specs/09-supplier-master-catalogo-proveedores-homologados.md`.

## 1. Superficie y dependencias

| Superficie | Ruta |
|---|---|
| Supplier Master | `api/v1/suppliers` |
| Approval del cambio | `api/v1/approval/tasks/{taskId}/decision` |
| Catálogo homologado | `api/v1/approved-supplier-catalog` |
| Readiness | `health/supplier` (`SUPPLIER_OK` o el primer código degradado) |

Dependencias de despliegue, todas obligatorias antes de habilitar tráfico:

- **Key provider bancario.** Fuera de SQL: `Supplier:Banking:KeyBase64` (clave AES-256 de 32 bytes en
  base64) y `Supplier:Banking:KeyVersion`. Sin clave, sin clave válida o con longitud distinta de 32
  bytes, la resolución del proveedor falla cerrada con `503`; el servicio **nunca** degrada a
  plaintext. La rotación crea una versión nueva de la cuenta (ver §4).
- **Storage de acuerdos.** `Storage:S3:*` para los attachments del acuerdo. El bucket no es público;
  la descarga usa una URL temporal (máximo 15 minutos) y queda auditada.
- **Workloads.** `Approval:Workloads` debe allowlistear `internal://procure-to-pay|supplier-domain`
  (y el `client_id` que use el catálogo si se despliega como workload separado) y
  `Approval:OwnerWorkloads` debe resolver exactamente uno para `active-supplier-owner|v1` con
  `ClientId = supplier-domain`.
- **Registro del processor.** La migración `Spec09SupplierMaster` inserta exactamente una fila en
  `Supplier.SupplierPrerequisiteProcessorRegistrations`
  (`active-supplier-owner`, `v1`, `supplier-domain`). 0/2 filas o un `ProcessorId` distinto del
  workload configurado degradan readiness con `ACTIVE_SUPPLIER_PROCESSOR_UNAVAILABLE` y el processor
  no reclama attempts.
- **Autoridad de aprobación.** `PROCUREMENT_APPROVER` con un único grant vigente de tipo
  `SUPPLIER_MASTER`. El rank mínimo aceptado es `Supplier:Governance:MinimumSupplierMasterRank`
  (por defecto 1, es decir, cualquier nivel activo).

## 2. Modelo operativo

- **Raíz y punteros.** Cada proveedor tiene un UUID estable, una identidad fiscal reservada
  (`country_code` + Tax ID normalizado) y dos punteros: el operacional aprobado y el de trabajo. Las
  versiones son append-only; los punteros nunca retroceden y un **pending** no reemplaza a la versión
  aprobada.
- **Clasificación de cambios.** Identidad fiscal, nombre legal, payment terms, monedas, categorías,
  riesgo, performance, Banking Details y estado son sensibles y exigen aprobación. Trade name,
  direcciones y contactos son cosméticos: `PROCUREMENT_BUYER` los aplica con motivo y audit, sin caso
  de aprobación. La clasificación la calcula el servidor desde el diff persistido; el payload no
  puede declarar un cambio como cosmético.
- **Estados.** `DRAFT → PENDING_APPROVAL → ACTIVE → SUSPENDED | BLOCKED`, y desde `SUSPENDED` o
  `BLOCKED` solo se vuelve a `ACTIVE` mediante otra aprobación.
- **Catálogo.** Una entrada identifica `(supplier, spend_category, product?)`. Una entrada
  producto-específica prevalece sobre la general de la misma categoría; para un mismo selector no
  puede haber dos versiones `ACTIVE` efectivas. `valid_to` vencido deriva `EXPIRED` por reloj, sin
  mutar la versión.

## 3. Operación diaria

- **Alta de proveedor**: crear borrador (`POST /api/v1/suppliers`), revisar, presentar
  (`POST .../versions/{v}/submit`) y esperar la decisión del aprobador. Un mismo usuario que edite y
  apruebe es imposible: el requirement excluye al editor.
- **Cambio cosmético**: `PUT /api/v1/suppliers/{id}` con `expectedVersion`; la respuesta indica
  `requiresApproval=false` y la versión sucesora queda operacional con el mismo estado.
- **Cambio sensible**: la misma llamada devuelve `requiresApproval=true` y la lista de campos
  sensibles; después se presenta y decide.
- **Homologación**: subir y confirmar el attachment (`POST .../attachments`,
  `POST .../attachments/{id}/confirm`), guardar la entrada (`POST .../entries`) y presentarla
  (`POST .../entries/{id}/versions/{v}/submit`). Publicar exige un attachment **confirmado**; un
  staging nunca es publicable.
- **Banking**: `POST /api/v1/suppliers/{id}/banking` crea la versión cifrada; el cambio se vuelve
  operacional solo cuando la versión del proveedor que la referencia queda aprobada. La lectura
  ordinaria muestra banco, moneda, tipo y últimos cuatro caracteres. El plaintext solo se obtiene por
  `POST .../banking/{detailId}/versions/{v}/reveal` con `purpose` tipado, y solo para
  `AP_SPECIALIST` (versión operacional) o el `PROCUREMENT_APPROVER` que tenga la task viva. Cada
  reveal queda auditado.
- **Fallo esperado**: `409` conflicto de versión o unicidad, `422` referencia o transición inválida,
  `503 /problems/supplier-dependency-unavailable` cuando falta o es ambiguo un owner, catálogo,
  lookup, key, storage o adapter.

## 4. Rotación de claves bancarias

1. Publicar la nueva clave como `Supplier:Banking:KeyBase64` con una `KeyVersion` nueva y reiniciar.
2. El descifrado de versiones antiguas sigue funcionando mientras la clave anterior siga disponible
   en el despliegue; por eso la rotación se hace en dos fases: primero se habilita una clave que
   pueda descifrar ambas generaciones (o se conserva la anterior como fallback) y luego se crea la
   cuenta con la clave nueva.
3. Crear una **versión nueva** de cada cuenta (`POST .../banking` con `bankingDetailId` y el mismo
   contenido revisado) para re-cifrar con la clave vigente; no se reescribe historia.
4. Un `Tag` o `Nonce` alterado hace fallar la autenticación con `503` y **nunca** revela bytes; el
   trigger append-only impide reescribir el sobre en su lugar.

## 5. Diagnóstico y recuperación

| Código de readiness | Causa | Acción |
|---|---|---|
| `PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:SUPPLIER` | Falta el owner del slot o su identidad/contrato no coincide | Verificar el registro `SupplierReferenceOwner` en el despliegue |
| `POLICY_REFERENCE_CATALOG_UNAVAILABLE:SUPPLIER` | Falta o está duplicado el catálogo Policy `SUPPLIER` | Revisar el registro exact-one del catálogo |
| `SUPPLIER_ENCRYPTION_UNAVAILABLE` | Key provider ausente, malformado o de longitud incorrecta | Configurar `Supplier:Banking:*` (§1) |
| `SUPPLIER_ATTACHMENT_STORAGE_UNAVAILABLE` | Storage de acuerdos no resuelto | Revisar `Storage:S3:*` |
| `SUPPLIER_APPROVAL_ADAPTER_UNAVAILABLE` | Falta o está duplicado un adapter de gobierno | Verificar los dos adapters `supplier-governance-approval-adapter/v1` |
| `ACTIVE_SUPPLIER_PROCESSOR_UNAVAILABLE` | 0/2 registros del processor o workload distinto | Corregir la fila de registro y `Approval:OwnerWorkloads` |
| `ACTIVE_SUPPLIER_BACKLOG` | Attempt debido hace más de 60 s sin señal | Revisar el worker, la conectividad de Approval y los errores registrados |
| `SUPPLIER_DATA_CORRUPTED` | Puntero operacional sin versión existente | Restaurar desde backup o resolver hacia adelante con una nueva aprobación |
| `APPROVED_SUPPLIER_CATALOG_CORRUPTED` | Puntero de catálogo sin versión o lookup de hechos ausente | Igual que el anterior |

- **Attempt atascado.** El attempt es recuperable: un error técnico deja el prerequisite `WAITING`
  con `LastErrorCode` y `NextAttemptAt`. No se edita la fila a mano: se corrige la dependencia y se
  deja que el worker reintente, o se ejecuta el barrido de `ProcessDueAsync`.
- **Crash entre el check y la señal.** El attempt conserva estado y lease; al recuperar el lease se
  consulta la señal registrada y se finaliza con la misma clave, sin señal doble.
- **Attachment huérfano.** Un staging sin confirmar puede quedar sin referencia; es seguro eliminarlo
  del bucket porque ninguna versión publicada lo referencia (`State = STAGED`). Nunca se borra un
  attachment confirmado mientras esté referenciado.
- **Rollback.** Detener mutaciones y submissions, mantener readers, consumer de resultados y
  processor hasta drenar proposals y attempts. No existe down migration destructiva: la recuperación
  es hacia adelante.
- **Prohibido en operación**: editar punteros, ciphertext, estados, digests o audit a mano; extraer
  datos bancarios por consultas directas; publicar una entrada de catálogo sin aprobación; revivir un
  attempt `COMPLETED` (el trigger lo rechaza).

## 6. Verificación

- `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`
  cubre canonicalización, matching de catálogo, clasificación de cambios y el sobre cifrado.
- Las suites de integración y E2E (Testcontainers) recorren activación, cambios cosméticos y
  sensibles, rechazo, bloqueo y reactivación, catálogo, banking y el recorrido real
  PR → Policy → `policy-approval-adapter/v4` → `active-supplier-owner/v1` → señal.
- Antes de habilitar tráfico en un entorno nuevo: `health/supplier` en `SUPPLIER_OK`, una activación
  completa con aprobación real y un reveal bancario auditado.
