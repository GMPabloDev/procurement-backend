# Operación del módulo Purchase Requests

## Orden de despliegue

1. Aplicar la migración `Spec06PurchaseRequests` antes de publicar la API:
   `dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api`.
   El esquema `PurchaseRequest` es aditivo y append-only (versiones, líneas, deltas, attestations,
   manifests, attempts, inbox y lifecycle). No modifica contenido de Policy/Approval histórico ni
   crea FKs hacia dominios de owners futuros.
2. Desplegar primero el schema y los consumers (`approval-result/v2|v3` y
   `approval-case-lifecycle/v1`) —el worker de Approval los resuelve exact-one—, después el soporte
   de supersesión v3 (`approval-canonical-json/v3`), y por último el provider real y las APIs de
   create/revision/submit/cancel.
3. Comprobar `/health/purchase-request`: si el provider, el adapter `policy-approval-adapter/v2`, la
   allowlist de workloads o un slot de owner de referencia falta o es ambiguo, la health queda
   `Degraded` con el código exacto y `submit` responde `503`; nunca se degrada a facts incompletos
   ni se registran catálogos ficticios.
4. Los owners externos pueden no estar desplegados: un request sin Cost Center, Spend Category,
   producto, proveedor o FX declarados no los necesita, pero declararlos sin owner disponible falla
   cerrado. Corregir el registro de owner y reintentar la presentación; no se edita una attestation
   histórica.

## Contratos y determinismo

- Todos los digests de este dominio usan `policy-canonical-json/v1` + SHA-256 en minúsculas:
  `line_content_digest`, `request_content_digest`, fingerprints de create/revision/submit,
  `reference_attestation_digest`, `policy_manifest_digest`, `domain_attestation_digest` y
  `purchase_request_materiality_digest`. Los vectores dorados viven en
  `tests/ProcureToPay.UnitTests/PurchaseRequests/PurchaseRequestCanonicalGoldenTests.cs`.
- La supersesión de Approval usa `approval-supersession-delta/v1` con
  `approval-canonical-json/v3` y entradas cerradas `RETAINED|ADDED|REMOVED`; su vector dorado está
  en `tests/ProcureToPay.UnitTests/Approval/ApprovalSupersessionDeltaTests.cs`. Las filas y digests
  v1/v2 históricos permanecen inmutables y verificables: no se reetiquetan ni se migran.
- `policy-approval-adapter/v2` declara `requester_id` obligatorio, admite
  `requester_id=originator_id` (excluido una sola vez y sin poder decidir) y ofrece
  `APPROVE|REJECT|REQUEST_CHANGES` en cada requirement humano.
- El carry-forward entre versiones solo ocurre si coinciden el materiality proof verificado por el
  dominio, el contrato completo del requirement y el conjunto cubierto; cualquier diferencia o duda
  crea una task nueva. `ADDED` nunca hace carry-forward y `REMOVED` no reaparece.

## Presentación y recuperación

- `POST /v1/purchase-requests/{id}/submission` es idempotente por
  `(organization_id, requester_id, submission_key)` y por `(request_id, request_version)`. La
  operación avanza una fila durable `PurchaseRequestSubmissionAttempt` con estados
  `PENDING → POLICY_CONFIRMED → APPROVAL_CONFIRMED`, o `BLOCKED`/`DEPENDENCY_FAILED`.
- Las keys internas son deterministas por request/versión
  (`pr-submit-<id>-v<n>`, `pr-supersede-<id>-v<n>`, `pr-submit-<id>-v<n>-policy`): reintentar la
  misma versión recupera el bundle de Policy y el caso de Approval ya confirmados; otra carga con
  la misma key y distinto contenido responde `409`.
- Un fallo entre módulos **no** declara éxito ni deja datos parciales: el attempt conserva la etapa
  confirmada y el `error_code`, y el reintento continúa desde ahí. `Policy BLOCKED` no crea caso y
  responde `422`; una dependencia ausente, ambigua o corrupta responde
  `503 /problems/purchase-request-dependency-unavailable`.
- Una revisión presentada supersede el caso anterior completo: el caso previo y sus nodos no
  terminales quedan `SUPERSEDED`, se emiten eventos `approval-case-lifecycle/v1` por target y los
  resultados tardíos del caso previo se conservan en el inbox pero nunca alteran la proyección
  actual.

## Proyección de estado

- El consumer deduplica por `event_id + contract_version` y por target, verifica organización, caso,
  subject/versión y obligación contra el manifest persistido, y solo proyecta la versión actual.
- Por línea: todos los requirements que la cubren deben terminar `APPROVED` y todos los
  prerequisites `SATISFIED`; `CHANGES_REQUESTED` y `REJECTED` prevalecen, y `CANCELLED`/`SUPERSEDED`
  nunca se convierten en aprobación.
- La request queda `APPROVED` si todas las líneas lo están, `REJECTED` si todas están rechazadas,
  `PARTIALLY_APPROVED` con al menos una aprobada y otra no terminal o rechazada,
  `CHANGES_REQUESTED` si alguna línea vigente lo está, e `IN_APPROVAL` en los demás casos abiertos.
- Cancelar no borra versiones, evaluaciones, casos, decisiones ni resultados; un caso no terminal se
  cancela por el workload owner antes de confirmar `CANCELLED` (SPEC 06 REQ-10).

## Health, telemetría y límites

- `/health/purchase-request` devuelve `{status, code}` con códigos acotados:
  `PURCHASE_REQUEST_OK`, `PURCHASE_REQUEST_DEGRADED` (con la lista de razones:
  `..._PROVIDER_UNAVAILABLE`, `..._ADAPTER_UNAVAILABLE`, `..._ADAPTER_INCOMPATIBLE`,
  `..._WORKLOAD_UNAVAILABLE`, `..._OWNER_UNAVAILABLE:<REFERENCE>`, `..._SUBMISSION_STUCK`,
  `..._SUBMISSION_FAILED`, `..._ATTESTATION_MISSING`, `..._MANIFEST_CORRUPTED`) y
  `PURCHASE_REQUEST_UNAVAILABLE`.
- Un attempt en `PENDING|POLICY_CONFIRMED` o `DEPENDENCY_FAILED` con más de 15 minutos sin avanzar
  degrada la health. El backlog del outbox de Approval sigue reportándose en `/health/approval`.
- Logs, métricas, trazas y health correlacionan request/versión/etapa/resultado sin contenido de
  línea, justificaciones, respuestas de riesgo, user ids, tokens ni snapshots.
- Límites: 500 líneas por request, 256 respuestas de riesgo por línea, snapshot ≤ 5 MiB, summary
  1–500 scalars, motivo 1–1.000, keys 1–128 `[A-Za-z0-9._:-]`; se verifican antes de persistir y
  responden `413`/`400` sin filtrar datos.

## Rollback

- Deshabilitar primero los nuevos create/submit; los readers, el inbox y los consumers siguen siendo
  compatibles con `approval-result/v2|v3` y `approval-case-lifecycle/v1`.
- Tras crear eventos con `approval-canonical-json/v3` no se vuelve a una versión incapaz de leerlos:
  se corrige hacia adelante. Datos y eventos no se borran; las migraciones de este módulo no tienen
  descenso destructivo.

## Verificación

- `dotnet build ProcureToPay.sln --no-restore`
- Unitarias: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore`
- Integración (Testcontainers/SQL Server): `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore`
- API/E2E (Docker): `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore`
- En este repositorio el runner es Microsoft.Testing.Platform; si `dotnet test` reporta 0 pruebas,
  ejecutar el binario de pruebas compilado (`tests/<proyecto>/bin/Debug/net10.0/<proyecto>`) o
  recompilar el proyecto de pruebas antes de sospechar un fallo.
