# SPEC 08 — Budgets y movimientos presupuestarios por posición

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** No iniciada
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** f381a0a03d09ebf344504cc47a64ac6ebcbe20078d143b57af4226012c883326
> **Fecha:** 2026-09-14
> **Actualizada:** 2026-09-14
> **Aprobada el:** 2026-09-14
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Administrar asignaciones y saldos por Cost Center + Fiscal Year + Spend Category, registrar movimientos reproducibles y reservar presupuesto de forma atómica mediante el owner real de `REQUIRE_BUDGET_CHECK` antes de habilitar Approval.
> **Depende de:** SPEC 07
> **Modifica:** SPEC 03, SPEC 05, SPEC 06
> **Reemplaza:** Ninguna

## Contexto

SPEC 07 dejó `budget-check-owner/v1` deliberadamente fail-closed. El código actual de `PolicyApprovalAdapter` reconoce `REQUIRE_BUDGET_CHECK`, pero solo persiste `amount_base`, `base_currency` y Cost Centers; no conserva Fiscal Year ni Spend Category por target, y el owner se resuelve únicamente desde configuración, sin implementación que compruebe, reserve o señale el prerequisite. Como resultado, una Purchase Request real que requiera presupuesto no puede completar Approval y tampoco existe una fuente de verdad para asignación, saldo o movimientos presupuestarios.

## Alcance

### Incluye

- Posiciones presupuestarias organizacionales identificadas por Cost Center + Fiscal Year + Spend Category y expresadas exclusivamente en la moneda base de la organización.
- Asignaciones administradas mediante revisiones append-only y saldos `ALLOCATED`, `RESERVED`, `COMMITTED` y `CONSUMED`, con `AVAILABLE` derivado.
- Movimientos append-only `REQUESTED`, `RESERVED`, `COMMITTED`, `CONSUMED` y `REVERSE`, operaciones por lote e idempotencia.
- Precheck no vinculante, auditable y all-or-nothing sobre una o varias líneas/posiciones.
- Adapter in-process real `budget-check-owner/v1`, worker durable y señalización de prerequisites de Approval únicamente después de reservar o de confirmar insuficiencia.
- Proyección completa por target desde Purchase Requests hacia `policy-approval-adapter/v3`, incluido Fiscal Year y Spend Category.
- Liberación o sustitución de reservas ante rechazo, cambios solicitados, cancelación y supersesión.
- Comandos workload-only para comprometer, consumir y revertir, aunque los dominios futuros que los invoquen no formen parte de esta entrega.
- Persistencia SQL Server, auditoría, health, observabilidad, migración, operación y pruebas del recorrido real Purchase Request → Policy → precheck → Approval → Budget owner.

### No incluye

- Reglas o thresholds de Policy, routing de aprobadores ni cambios a la semántica de approvals humanas.
- Purchase Orders, sourcing, invoices, payments o los adapters reales que decidirán cuándo comprometer o consumir; esta spec solo publica y prueba sus contratos workload-only.
- Forecast, rollover entre ejercicios, transferencias entre posiciones, jerarquías presupuestarias, tolerancias, sobregiro o presupuesto multimoneda.
- Importación/sincronización con ERP, conciliación contable, centros de beneficio, proyectos o dimensiones distintas de la posición definida.
- Cost Center Owner funcional, dashboards analíticos, frontend ni visibilidad de saldos para cualquier usuario empresarial.
- Editar o borrar asignaciones, movimientos, reservas, evidencias o audits históricos.

## Comportamiento esperado

- **REQ-01 — Identidad de posición y asignación versionada.** Una `BudgetPosition` pertenece a una organización y se identifica de forma única por `(cost_center_id, fiscal_year, spend_category_code)`. Cost Center id y Spend Category code son identidades estables; cada alta o revisión conserva además las refs current exactas que se validaron contra SPEC 07. Solo `ADMIN` con scope `ORGANIZATION` crea o cambia el importe `ALLOCATED` mediante `allocation_key`, motivo y `expected_version`; la primera asignación crea versión 1 y cada cambio una única sucesora append-only. Importe negativo, moneda distinta de la base, ref inexistente/inactiva/stale, Fiscal Year fuera de 2000–2100, reducción por debajo de `RESERVED + COMMITTED + CONSUMED`, replay distinto o carrera se rechazan sin cambiar saldo ni audit de éxito. No existe movimiento `ALLOCATE`: la historia de asignaciones es la fuente de `ALLOCATED`.

- **REQ-02 — Saldos e invariantes.** Para cada posición, `ALLOCATED` es el importe de su versión current; `RESERVED`, `COMMITTED` y `CONSUMED` son la suma neta de movimientos confirmados; `AVAILABLE = ALLOCATED - RESERVED - COMMITTED - CONSUMED`. Todos usan `decimal(38,12)`, moneda base ISO 4217 y nunca son negativos; `AVAILABLE` tampoco puede ser negativo. Un movimiento confirmado y la proyección de todos los saldos afectados se escriben en la misma transacción serializable. Las posiciones se bloquean por `position_key_digest` ascendente para que dos instancias y una revisión de asignación concurrente produzcan un único orden sin deadlock ni sobregiro.

- **REQ-03 — Operaciones y movimientos auditables.** Cada `BudgetOperation` contiene key/fingerprint, tipo de operación, actor workload o usuario permitido, fuente tipada/versionada, UTC servidor, correlation opaca y uno o más movimientos. `REQUESTED` registra demanda y no modifica buckets; `RESERVED` aumenta `RESERVED`; `COMMITTED` reduce `RESERVED` y aumenta `COMMITTED`; `CONSUMED` reduce `COMMITTED` y aumenta `CONSUMED`; `REVERSE` referencia un único movimiento previo y aplica el inverso de su delta todavía no avanzado ni revertido. Una transición puede ser parcial, pero su importe debe ser positivo y no superar el remanente de su movimiento padre; cada importe hijo queda ligado a ese padre y no puede usarse dos veces. Revertir un `CONSUMED` lo devuelve a `COMMITTED`, revertir un `COMMITTED` lo devuelve a `RESERVED`, revertir un `RESERVED` lo libera a `AVAILABLE` y revertir `REQUESTED` solo cancela demanda sin saldo. Para liberar desde un estado posterior se encadenan reversas en orden inverso; no existe salto, importe negativo, update ni delete.

- **REQ-04 — Precheck all-or-nothing y no vinculante.** El puerto in-process exact-one `purchase-request-budget-demand-builder/v1` recibe del dominio Purchase Requests organización, request id/version, manifest/attestation confirmados y el set opcional de targets Policy; carga cada `PurchaseRequestLineVersion` y produce los demands de Datos y contratos sin aceptar posición, importe o moneda del usuario. Si aún no existe manifest, la superficie de precheck ejecuta o recupera primero la misma attestation idempotente de SPEC 06; refs inválidas/indisponibles conservan sus `422/503` sin operación Budget parcial. Budget agrupa los demands por posición y evalúa un snapshot consistente: persiste una operación `REQUESTED` por lote y un movimiento por target aun cuando sea insuficiente, sin modificar buckets ni garantizar disponibilidad futura. Devuelve `AVAILABLE` solo si todas las posiciones cubren simultáneamente el total, o `INSUFFICIENT|UNFUNDED` sin reserva parcial. `POST /api/v1/purchase-requests/{request_id}/versions/{request_version}/budget-precheck` exige requester activo, versión current visible y body exacto `{contract_version,precheck_key}`; actor, organización y source se derivan server-side. La respuesta exacta `budget-precheck-response/v1` de Datos y contratos no expone allocations ni saldos. Replay idéntico recupera operación, movimientos, manifest y respuesta originales; otra preimage con la misma key devuelve `409`.

- **REQ-05 — Proyección contractual completa de Policy a Approval.** El descriptor exacto `policy-approval-adapter/v3` es `{adapter_id=policy-approval-adapter,contract_version=v3,subject_type=PURCHASE_REQUEST,operation=SUBMIT_PURCHASE_REQUEST,requester_required=true,allows_requester_as_originator=true,supersession_delta_supported=true}`. Conserva la tabla, DAG, acciones y snapshot `policy-evaluation-snapshot/v1` de v2; solo reemplaza la proyección budget. Por cada control `REQUIRE_BUDGET_CHECK`, pasa sus `SubjectIds` y targets materiales al mismo builder de REQ-04; este carga una `PurchaseRequestLineVersion` exacta por target y deriva `amount_base=BaseAmount`, `cost_center_ref=CostCenterRef`, `fiscal_year=FiscalYear` y `spend_category_ref=SpendCategoryRef`. Coteja request id/version, manifest/attestation, target id/version/material digest y que el set producido sea exactamente el set del control, y persiste los parámetros exactos de `budget-check-owner/v1`. No se agregan esos datos a `PolicyGeneratedControl` ni se usa su actual `AmountBase/CostCenterIds` como fuente v3. Los targets que comparten posición conservan un demand e importe por línea. Mismatch, línea ausente, duplicidad/omisión, moneda distinta, campo extra, forma v2 incompleta o dato no reproducible devuelve `503` sin caso. `source_control_type=REQUIRE_BUDGET_CHECK`; `source_control_digest` liga ese código, requirement key, phase, parámetros y targets completos. El `submission_fingerprint` sigue el preimage v2 de SPEC 03, pero contiene `adapter_version=v3` y el prerequisite v3 completo; no cambia `approval-canonical-json/v2`.

- **REQ-06 — Submit con precheck y reserva autoritativa.** Cuando Policy produce `REQUIRE_BUDGET_CHECK`, el attempt de submit de SPEC 06 ejecuta el precheck sobre los demands de REQ-05 antes de abrir/superseder Approval. Solo `AVAILABLE` permite continuar; insuficiencia devuelve `422 /problems/budget-insufficient`, conserva evaluación y precheck para retry/auditoría, y no crea caso. El precheck sigue sin ser garantía: al persistirse el caso, el owner real procesa el prerequisite `WAITING` y crea atómicamente una operación `REQUESTED` más movimientos `RESERVED` para todos los demands o ninguno. Si logra reservar, señala `SATISFIED`; si la disponibilidad real es insuficiente, no crea `RESERVED` y señala `FAILED`. La señal usa la identidad `issuer + client_id` exacta resuelta para `budget-check-owner/v1`, `signal_key` determinista y evidencia de REQ-10; un error técnico deja el prerequisite `WAITING` para retry, nunca lo señala como satisfecho ni como insuficiencia empresarial.

- **REQ-07 — Worker durable, replay y compensación entre módulos.** Cada prerequisite budget crea o recupera un `BudgetPrerequisiteAttempt` único con `attempt_id`, `due_at=prerequisite.created_at`, cuatro keys deterministas (`budget:<prerequisite_id>:request|reserve|signal|compensate`) y los ids de operación una vez confirmados. Su máquina es `PENDING → PROCESSING → RESERVED|INSUFFICIENT → SIGNALLING → COMPLETED`; `RESERVED|SIGNALLING → COMPENSATING → COMPENSATED` solo si el case terminó antes de una señal confirmada. `COMPLETED` conserva `signal_result=SATISFIED|FAILED`; `COMPENSATED` conserva el reverse operation id y ambos son terminales. Un worker reclama `PENDING|RESERVED|INSUFFICIENT|SIGNALLING|COMPENSATING` cuando `next_attempt_at<=now` mediante lease de 30 segundos, renovación máxima cada 10 segundos y fencing token. Un fallo antes de confirmar operación devuelve `PROCESSING→PENDING`; después conserva `RESERVED|INSUFFICIENT|SIGNALLING|COMPENSATING`, fija `last_error_code`, `next_attempt_at` y libera lease sin borrar ids. Antes de compensar desde `SIGNALLING`, consulta/reintenta la misma signal key para distinguir respuesta perdida de señal ausente. Reserva y señal son transacciones locales recuperables: retry reutiliza attempt, request/reserve/reverse operation ids, movimientos, evidence digest y signal key. Timeout, error no minimizado o lease perdido no confirma movimiento ni checkpoint. Readiness degrada si `now-due_at>60s` sin `COMPLETED|COMPENSATED`.

- **REQ-08 — Evolución y liberación de reservas.** Un consumer Budget exact-one de `approval-result/v2|v3` toma cualquier `REJECTED|CHANGES_REQUESTED|CANCELLED` por target, y otro de `approval-case-lifecycle/v1` toma `SUPERSEDED`; ambos verifican organization/case/subject/target y crean o recuperan `budget-release-command/v1` con key derivada del event id. La release referencia las `RESERVED` abiertas del mismo case+target, crea sus `REVERSE` y deduplica por `event_id+contract_version`; un resultado de otra fuente dentro del mismo case puede disparar la release del target, pero nunca libera otro case/version. Al superseder, el owner serializa predecessor y replacement: bajo los locks de ambas operaciones revierte reservas abiertas anteriores y reserva el set nuevo all-or-nothing, contando el hold anterior como disponible; si no hay nuevo prerequisite, lifecycle solo libera. Para cancelar una Purchase Request `APPROVED` con case ya `COMPLETED` —situación sin evento Approval— Purchase Requests debe ejecutar `budget-release-command/v1` con su workload exacto, `trigger=PURCHASE_REQUEST_CANCELLED`, case y targets del manifest antes de confirmar `CANCELLED`; una fila `PurchaseRequestBudgetReleaseAttempt` persiste key/estado para retry entre las dos transacciones. El schema, auth y binding están en Datos y contratos. Una reserva ya `COMMITTED|CONSUMED` indica takeover downstream y responde `409` sin cancelar/revisar hasta que el owner la revierta. Eventos/comandos duplicados, tardíos o de otro subject no afectan saldo.

- **REQ-09 — Commit, consume y reverse workload-only.** Cada `BudgetMovementProducerRegistration` liga exactamente `{operation,contract_version,source_type,producer_id,workload_issuer,workload_client_id}`. El registry resuelve exactamente uno por `(operation,contract_version,source_type)` y exige coincidencia completa de workload; 0/2, identidad parcial o registration inválido devuelve `503/403` sin operación. La tabla v1 es: `COMMIT + PURCHASE_ORDER`, `CONSUME + INVOICE`; `REVERSE` usa el mismo `source_type` y workload productor de la operación padre. `budget-transition-command/v1` liga source, parents versionados, amounts y key/fingerprint; COMMIT consume remanente de RESERVED, CONSUME de COMMITTED y REVERSE el delta aún no avanzado/revertido. `BudgetOperation` y `BudgetMovement` son append-only version 1; concurrencia se controla por `parent_movement_version=1`, remanente confirmado y balance rowversion, no por una versión mutable de operación. `ADMIN`, `AUDITOR`, requester y budget owner no adquieren facultad financiera por su rol. Hasta specs de PO/invoice, no existen registrations productivas de COMMIT/CONSUME/REVERSE; tests usan producers controlados y el adapter real solo reserva/libera.

- **REQ-10 — Evidencia, API, visibilidad y errores.** Toda asignación, precheck, movimiento, señal, compensación, replay conflictivo y fallo empresarial crea audit append-only con actor union `USER|WORKLOAD|SYSTEM`, causa tipada, versiones, deltas y correlation, sin snapshots completos. La evidencia `budget-check-evidence/v1` permite reproducir request/result/movimientos y su SHA-256, y `evidence_reference=budget://<attempt_id>` no contiene posición ni importe. `ADMIN` organizacional administra asignaciones, consulta saldos y operación; `AUDITOR` con scope `ORGANIZATION` o el Cost Center exacto lee posición, historia, movimientos y audit sin mutar; otros usuarios solo reciben su respuesta de precheck minimizada. Fuera de scope responde `404`. Problem Details usa `400`, `401`, `403`, `404`, `409`, `413`, `422 /problems/budget-insufficient|budget-reference-invalid` y `503 /problems/budget-dependency-unavailable`. Un lote admite 1–500 demands, un motivo 1–1.000 Unicode scalars y keys 1–128 `[A-Za-z0-9._:-]`.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `BudgetPosition` | id, organization id, cost center id, fiscal year, spend category code, position key digest, current allocation version, current balance version | Unicidad por la terna; ambos pointers avanzan solo a filas existentes y nunca retroceden. |
| `BudgetAllocationVersion` | position/version, Cost Center ref, Spend Category ref, allocated amount/currency, predecessor, actor, UTC, key/fingerprint, motivo | Append-only; cambiar allocation no reinicia buckets y actualiza balance en la misma transacción. |
| `BudgetBalance` | position id, balance version, allocation version, allocated, reserved, committed, consumed, rowversion | Una proyección mutable por posición; balance version aumenta exactamente una vez por transacción que la afecta. Reconstrucción = allocation current + todos los deltas de movimientos de la posición. |
| `BudgetOperation` | id/version=1, organization, kind, key/fingerprint, source, actor, cause, UTC, correlation, result | Root append-only all-or-nothing; kinds `PRECHECK|APPROVAL_RESERVE|TRANSFER_RESERVE|COMMIT|CONSUME|REVERSE|RELEASE`. |
| `BudgetMovement` | id/version=1, operation, position, allocation version at posting, target nullable, type, amount/currency, parent/reversed movement nullable, balance version after, before/after, UTC | Append-only; todos los movimientos de todas las allocation versions alimentan el mismo balance de posición. |
| `BudgetPrerequisiteAttempt` | prerequisite/case/subject, parameters/source digest, four deterministic keys, operation/signal/reverse ids, result/state, due/next attempt, lease/fencing, attempts/error, timestamps | Único por prerequisite; máquina exacta de REQ-07. |
| `PurchaseRequestBudgetReleaseAttempt` | request/version/case, release key, Budget operation, estado, timestamps/error | Único por cancelación; `PENDING|RELEASED|COMPLETED`, donde PR solo cancela tras RELEASED. |
| `BudgetMovementProducerRegistration` | operation, contract version, source type, producer id, issuer, client id | Exact-one por triple; tabla cerrada de REQ-09. |
| `BudgetPrerequisiteProcessorRegistration` | adapter id/version, processor id | Exactamente uno para `budget-check-owner/v1`; debe usar el workload que Approval resolvió/persistió. |
| `BudgetAuditRecord` | actor, cause, action, target/version, deltas minimizados, UTC, correlation | Append-only y atómico con la mutación local; schemas cerrados abajo. |

### Identidad y referencias de posición

- La identidad usa Cost Center UUID estable y Spend Category code canónico en mayúsculas; cambios de nombre, versión o Department no crean otra posición. Una reasignación del Cost Center o revisión de categoría sí exige refs current nuevas en la próxima asignación/precheck, mientras historia conserva las anteriores.
- `budget-position-key/v1` tiene propiedades exactas `{canonicalization_version,contract_version,cost_center_id,fiscal_year,organization_id,spend_category_code}`. Su SHA-256 es `position_key_digest`, único en la organización.
- Vector mínimo, bytes UTF-8 completos sin salto final:

```json
{"canonicalization_version":"policy-canonical-json/v1","contract_version":"budget-position-key/v1","cost_center_id":"22222222-2222-2222-2222-222222222222","fiscal_year":2026,"organization_id":"11111111-1111-1111-1111-111111111111","spend_category_code":"HARDWARE"}
```

SHA-256 esperado: `b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3`.

### Parámetros `budget-check-owner/v1`

El JSON persistido en `ExternalPrerequisite.parameters` contiene exactamente `{base_currency,contract_version,demands}`. `contract_version=budget-check-owner/v1`; `demands` es un set canónico sin duplicados y cada entrada contiene exactamente `{amount_base,cost_center_ref,fiscal_year,source_line,spend_category_ref,target}`. `amount_base` es string decimal canónico positivo; `cost_center_ref` contiene `{id,version}`; `spend_category_ref`, `{catalog,code,digest,version}` con `catalog=SPEND_CATEGORY`; `source_line`, `{content_digest,id,version}`; y `target`, `{id,material_snapshot_digest,type,version}` con `type=PURCHASE_REQUEST_LINE`. `source_line.id/version` coincide con target y su content digest con la versión PR persistida.

El owner rechaza campos desconocidos, duplicate target, target no perteneciente al prerequisite, referencias de otra organización, versiones no current, digest distinto, base currency distinta, total no reproducible desde las líneas persistidas o posición sin asignación. La misma posición puede aparecer varias veces y se bloquea/reserva por su suma, pero la evidencia conserva cada demand y movimiento por target. `BudgetPrerequisiteProcessorRegistration` contiene exactamente `{adapter_id,adapter_version,processor_id}` y debe resolver uno para `budget-check-owner/v1`; 0/2 o un processor cuyo workload no coincida con `ExternalPrerequisite.owner_workload_issuer+client_id` degrada readiness y no reclama el attempt.

### Builder y respuesta de precheck

- `purchase-request-budget-demand-build/v1` es un contrato in-process exact-one por `(source_type=PURCHASE_REQUEST,contract_version=v1)`. Request exacto: `{contract_version,manifest_digest,organization_id,request_content_digest,request_id,request_version,targets}`; `targets` está presente como `null` para precheck de todas las líneas o como set de targets Approval completos para un control Policy. Response exacta: `{base_currency,contract_version,demands,manifest_digest,organization_id,request_content_digest,request_id,request_version}`; cada demand contiene `{amount_base,cost_center_ref,fiscal_year,source_line,spend_category_ref,target}`, con `target=null` solo en precheck y no nulo para el adapter v3. El builder ecoa identidad/digests, cubre exactamente manifest o target set y falla si no puede reproducir una línea.
- El body HTTP `budget-precheck-request/v1` contiene exactamente `{contract_version,precheck_key}`. Purchase Requests deriva actor/organización y crea el build request tras confirmar manifest/attestation.
- El comando interno `budget-precheck-command/v1` contiene exactamente `{command_version,demands,manifest_digest,organization_id,precheck_key,source}`; `source` liga la request version y `demands` son la respuesta del builder con target nullable.
- `budget-precheck-response/v1` contiene exactamente `{contract_version,manifest_digest,operation_id,request_id,request_version,result,targets}`. Cada target response contiene `{id,result,version}`; `result=AVAILABLE|INSUFFICIENT|UNFUNDED`, el agregado solo es `AVAILABLE` si todos lo son, y no aparecen posición, categoría, Cost Center, importes ni saldos.

### Contrato `policy-approval-adapter/v3`

- Se registran v2 y v3 por sus contract versions; nuevos `PurchaseRequestSubmissionAttempt` fijan v3 y un attempt v2 ya persistido reanuda v2. El registry sigue resolviendo exact-one por subject+operation+contract version.
- Input y output siguen `ApprovalSubmissionRequest` y `ApprovalSubmission` de SPEC 03. Adapter id, subject, operation, actores, acciones, DAG, otros controles y snapshot son los declarados en REQ-05; budget es la única proyección distinta.
- `budget_source_control_digest` usa `approval-canonical-json/v2` sobre propiedades exactas `{parameters,phase,requirement_key,targets,type}`; `parameters` es el JSON canónico anterior como string y `type=REQUIRE_BUDGET_CHECK`. El `submission_fingerprint` de SPEC 03 contiene este prerequisite, su owner `budget-check-owner/v1` y `adapter_version=v3`.

### Release y transiciones workload

- `budget-release-command/v1` contiene exactamente `{case_id,command_version,organization_id,reason_code,release_key,request_id,request_version,targets,trigger,trigger_event}`. `trigger=APPROVAL_RESULT|APPROVAL_SUPERSEDED|PURCHASE_REQUEST_CANCELLED`; `trigger_event` es `{contract_version,event_id}` para los dos primeros y `null` para cancelación PR. Targets son el set Approval exacto del case/manifest. Consumers Approval actúan como `SYSTEM/BUDGET_OWNER`; cancelación exige el workload owner exacto del case, y ambos solo pueden revertir reservas del binding declarado.
- `budget-transition-command/v1` contiene exactamente `{command_version,movements,operation,operation_key,organization_id,reason_code,source}`. Cada item contiene `{amount,parent_movement_id,parent_movement_version,target}`; parent version es 1 y target es el Approval target completo. `source` contiene `{digest,id,type,version}`. `operation=COMMIT|CONSUME|REVERSE` debe coincidir con el registration y la tabla de REQ-09.
- `budget-transition-response/v1` contiene exactamente `{contract_version,movements,operation_id,replayed}`; cada movement ref contiene `{amount,id,parent_movement_id,position_key_digest,type,version}`. No expone balances fuera de las lecturas autorizadas.
- `BudgetMovementProducerRegistration` rechaza strings vacíos, duplicados y combinaciones fuera de la tabla. `reason_code` usa 1–64 ASCII `[A-Z][A-Z0-9_.:-]*`. La key es única en `(organization_id,workload_issuer,workload_client_id,operation)`; replay idéntico devuelve la misma response y otra preimage da `409`.

### Canonicalización y evidencia

Todos los documentos Budget usan `policy-canonical-json/v1` y SHA-256: UTF-8 sin BOM/whitespace, propiedades conocidas presentes y ordenadas ordinalmente, strings NFC, UUID `D` minúsculo, timestamps UTC con siete decimales, decimales como strings invariantes y sets ordenados por bytes canónicos sin duplicados. Cambiar una preimage exige otra `canonicalization_version`; cambiar solo el schema de wire exige otra `contract_version`.

Schemas comunes exactos, siempre con propiedades nullable presentes: `actor={system_id,type,user_id,workload_client_id,workload_issuer}` donde `type=USER|WORKLOAD|SYSTEM` completa una sola variante y `system_id=BUDGET_OWNER` para automatismos; `cause={audit_id,audit_stream}` o `null`; `source={digest,id,type,version}`; `position={cost_center_ref,fiscal_year,spend_category_ref}`; `approval_target={id,material_snapshot_digest,type,version}`; `movement_command_item={amount,parent_movement_id,parent_movement_version,target}`. `audit_stream=BUDGET|APPROVAL|PURCHASE_REQUEST`; el destino causal debe existir, pertenecer a la organización y ser inmutable.

| Digest/fingerprint | Propiedades exactas del preimage |
| --- | --- |
| `allocation_fingerprint` | `actor_user_id`, `allocated_amount`, `allocation_key`, `canonicalization_version`, `currency`, `expected_version`, `organization_id`, `position`, `reason` |
| `precheck_fingerprint` | `canonicalization_version`, `command_version`, `demands`, `manifest_digest`, `organization_id`, `precheck_key`, `source` |
| `operation_fingerprint` | `actor`, `canonicalization_version`, `command_version`, `movements`, `operation`, `operation_key`, `organization_id`, `reason_code`, `source` |
| `release_fingerprint` | `case_id`, `canonicalization_version`, `command_version`, `organization_id`, `reason_code`, `release_key`, `request_id`, `request_version`, `targets`, `trigger`, `trigger_event` |
| `budget_check_evidence_digest` | `attempt_id`, `canonicalization_version`, `case_id`, `checked_at`, `contract_version`, `movements`, `organization_id`, `parameters_digest`, `prerequisite_id`, `result`, `signal_key`, `source_control_digest` |

`demands`, refs, target y movement command item usan los schemas exactos anteriores. En evidencia, `movements` contiene `{amount,id,position_key_digest,target,type,version}`; para insuficiencia solo existen `REQUESTED`, y para satisfacción existen además `RESERVED`. `parameters_digest` es SHA-256 de los bytes exactos de `ExternalPrerequisite.parameters`.

La fixture contractual usa organización `11111111-1111-1111-1111-111111111111`, actor y Cost Center `22222222-2222-2222-2222-222222222222`, request `44444444-4444-4444-4444-444444444444` v1, línea `55555555-5555-5555-5555-555555555555` v1, case `66666666-6666-6666-6666-666666666666`, prerequisite `77777777-7777-7777-7777-777777777777`, attempt `88888888-8888-8888-8888-888888888888`, movimientos REQUESTED/RESERVED `99999999-9999-9999-9999-999999999998`/`99999999-9999-9999-9999-999999999999` v1, moneda `PEN`, FY 2026, categoría `HARDWARE` v1 con digest `a×64`, content/manifest/source/parameters/control digests `b×64`/`c×64`/`d×64`/`e×64`/`f×64`, amount `100`, allocation `1000`, reloj `2026-09-14T12:00:00.0000000Z`, keys `alloc-1|precheck-1|commit-1|budget:77777777-7777-7777-7777-777777777777:signal`, workload `issuer=internal://procure-to-pay, client_id=purchase-order-domain` y target material digest `1×64`. `expected_version=null` para allocation inicial; precheck usa `source_line` con content digest y `target=null`; operation usa `COMMIT`, `reason_code=PO_ISSUED`, source `PURCHASE_ORDER` con request UUID como id y el movimiento RESERVED como parent; evidence usa resultado `SATISFIED` y ambos movimientos. Los siguientes bloques son los bytes UTF-8 completos, sin salto final.

`allocation_fingerprint`:

```json
{"actor_user_id":"22222222-2222-2222-2222-222222222222","allocated_amount":"1000","allocation_key":"alloc-1","canonicalization_version":"policy-canonical-json/v1","currency":"PEN","expected_version":null,"organization_id":"11111111-1111-1111-1111-111111111111","position":{"cost_center_ref":{"id":"22222222-2222-2222-2222-222222222222","version":1},"fiscal_year":2026,"spend_category_ref":{"catalog":"SPEND_CATEGORY","code":"HARDWARE","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","version":1}},"reason":"Initial allocation"}
```

`precheck_fingerprint`:

```json
{"canonicalization_version":"policy-canonical-json/v1","command_version":"budget-precheck-command/v1","demands":[{"amount_base":"100","cost_center_ref":{"id":"22222222-2222-2222-2222-222222222222","version":1},"fiscal_year":2026,"source_line":{"content_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","id":"55555555-5555-5555-5555-555555555555","version":1},"spend_category_ref":{"catalog":"SPEND_CATEGORY","code":"HARDWARE","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","version":1},"target":null}],"manifest_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","organization_id":"11111111-1111-1111-1111-111111111111","precheck_key":"precheck-1","source":{"digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","id":"44444444-4444-4444-4444-444444444444","type":"PURCHASE_REQUEST","version":1}}
```

`operation_fingerprint`:

```json
{"actor":{"system_id":null,"type":"WORKLOAD","user_id":null,"workload_client_id":"purchase-order-domain","workload_issuer":"internal://procure-to-pay"},"canonicalization_version":"policy-canonical-json/v1","command_version":"budget-transition-command/v1","movements":[{"amount":"100","parent_movement_id":"99999999-9999-9999-9999-999999999999","parent_movement_version":1,"target":{"id":"55555555-5555-5555-5555-555555555555","material_snapshot_digest":"1111111111111111111111111111111111111111111111111111111111111111","type":"PURCHASE_REQUEST_LINE","version":1}}],"operation":"COMMIT","operation_key":"commit-1","organization_id":"11111111-1111-1111-1111-111111111111","reason_code":"PO_ISSUED","source":{"digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","id":"44444444-4444-4444-4444-444444444444","type":"PURCHASE_ORDER","version":1}}
```

`budget_check_evidence_digest`:

```json
{"attempt_id":"88888888-8888-8888-8888-888888888888","canonicalization_version":"policy-canonical-json/v1","case_id":"66666666-6666-6666-6666-666666666666","checked_at":"2026-09-14T12:00:00.0000000Z","contract_version":"budget-check-evidence/v1","movements":[{"amount":"100","id":"99999999-9999-9999-9999-999999999998","position_key_digest":"b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3","target":{"id":"55555555-5555-5555-5555-555555555555","material_snapshot_digest":"1111111111111111111111111111111111111111111111111111111111111111","type":"PURCHASE_REQUEST_LINE","version":1},"type":"REQUESTED","version":1},{"amount":"100","id":"99999999-9999-9999-9999-999999999999","position_key_digest":"b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3","target":{"id":"55555555-5555-5555-5555-555555555555","material_snapshot_digest":"1111111111111111111111111111111111111111111111111111111111111111","type":"PURCHASE_REQUEST_LINE","version":1},"type":"RESERVED","version":1}],"organization_id":"11111111-1111-1111-1111-111111111111","parameters_digest":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","prerequisite_id":"77777777-7777-7777-7777-777777777777","result":"SATISFIED","signal_key":"budget:77777777-7777-7777-7777-777777777777:signal","source_control_digest":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"}
```

Tests independientes fijan estos bytes y los cinco SHA-256 de la tabla; los hashes se calculan desde los bloques, no con helpers de producción.

| Vector | SHA-256 esperado |
| --- | --- |
| position key | `b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3` |
| allocation fingerprint | `0c27cd8be70888473809f9f394cf9856b7bfa65cffc0a95311312a53f3bd805f` |
| precheck fingerprint | `ac788ced032349896e8ad6ca3ad534fad866b94c0de5c4fc16bb5031e19a3706` |
| operation fingerprint | `5efac07fe18e6d7dc7130c0262a4acb28c6dd8973a87e9ca293f1b318280c38e` |
| budget check evidence | `4eece706b7214a84f45756adab97043ab30b551afcff89fb480bf64c72acc51e` |

## Impacto sobre especificaciones anteriores

| Contrato anterior | Regla nueva y alcance |
| --- | --- |
| SPEC 03 REQ-03/REQ-07/REQ-08 | `budget-check-owner/v1` deja de ser un owner de configuración sin implementación: un processor exact-one puede señalar el prerequisite con su workload persistido. Approval conserva estados, auth, signal fingerprint y resultados v2; Budget añade compensación propia porque cancelar no reescribe un prerequisite ya `SATISFIED`. |
| SPEC 05 REQ-03, mapping y owner table | Para nuevas Purchase Requests, `policy-approval-adapter/v3` reemplaza la proyección budget v2: parámetros por target incluyen Cost Center, Fiscal Year, Spend Category, importe y target completo. Los demás owners y controles no cambian. |
| SPEC 06 REQ-06 | El submit attempt agrega `BUDGET_PRECHECK_CONFIRMED` después de Policy y antes de Approval cuando existe `REQUIRE_BUDGET_CHECK`; insuficiencia conserva attempt recuperable sin caso. |
| SPEC 06 REQ-08/REQ-10 | Supersesión serializa transferencia/liberación presupuestaria según REQ-08; cancelar una request aprobada libera reserva antes de confirmar y queda bloqueado si ya existe takeover `COMMITTED|CONSUMED`. |
| SPEC 06 adapter v2 | v2 permanece legible para historia; nuevas submissions usan v3. No se reetiquetan fingerprints, cases, targets ni prerequisites existentes. |

La vigencia de estas modificaciones empieza al integrar SPEC 08. Los casos v1/v2 y sus digests permanecen inmutables; ninguna fila histórica se completa infiriendo Fiscal Year o Spend Category desde estado actual.

## Migración, despliegue y reversión

- La migración crea schema Budget con posiciones, versiones de asignación, balances, operaciones, movimientos, attempts, audits, leases, índices y constraints. No modifica tablas ni preimages históricas de Organization, Policy, Approval o Purchase Requests.
- El preflight exige SPEC 03/05/06/07 integradas, catálogos Cost Center/Spend Category exact-one, un processor real `budget-check-owner/v1`, un workload exact-one allowlisted y cero prerequisites budget v1/v2 `WAITING` sin Fiscal Year/Spend Category. Un hallazgo legacy bloquea rollout; no se infiere ni se señala.
- Orden: schema y readers Budget; consumers/compensación; worker y health en pausa; soporte `policy-approval-adapter/v3` y submit precheck; registro exact-one del owner/workload; luego worker y tráfico nuevo. El owner de configuración sin processor real no satisface readiness.
- Antes de tráfico se prueba: asignación; precheck; reserva concurrente; fallo entre reserva/señal; insuficiencia; reject/change/cancel/supersede; partial commit/consume/reverse; restart/lease/fencing; y recorrido PR→Policy→Approval→Budget real sin doubles.
- Rollback detiene submissions y nuevos movimientos, mantiene readers, compensador, worker y consumers hasta drenar attempts/reservas; conserva toda historia. Tras emitir adapter v3 o movimientos no se vuelve a una versión que no pueda leerlos: se corrige hacia adelante y no se ejecuta down migration destructiva.
- `docs/budget-operations.md` documenta asignación inicial, preflight, rollout, health, backlog, retry, compensación, reconstrucción de saldos y forward-fix; ningún procedimiento edita saldos, movements, pointers o digests a mano.

## Seguridad y privacidad

- Organización y usuario se derivan del JWT; los payloads no autocertifican actor, role ni scope. Workloads se comparan por `issuer + client_id` allowlisted y estable ante rotación de credenciales.
- Solo `ADMIN` de scope `ORGANIZATION` cambia asignaciones. `AUDITOR` lee según REQ-10 y nunca muta. Ninguno puede fabricar reservas, commits, consumos, reversas ni señales.
- El requester no conoce saldos, allocations, movimientos de terceros ni la posición agregada; solo el resultado minimizado de su precheck. `Cost Center Owner` no se infiere de Department, catálogo, role assignment o grant.
- Adapter y precheck obtienen línea, refs, importe y moneda desde snapshots persistidos y atestiguados; ningún caller aporta evidencia presupuestaria, balance o resultado `SATISFIED`.
- Logs, trazas, métricas, health y Problem Details omiten tokens, nombres/códigos empresariales, importes, saldos, user ids, motivos libres, payloads, digests completos y snapshots. Audit conserva importes/deltas necesarios bajo autorización.

## Requisitos no funcionales

- **NFR-01 — Integridad contable reproducible.** Asignaciones y movimientos append-only reconstruyen exactamente cada bucket; la proyección current coincide con la reconstrucción y cualquier divergencia degrada health.
- **NFR-02 — Atomicidad y no sobregiro.** Una operación multi-posición confirma todos sus movimientos/saldos o ninguno; dos instancias, un allocation update y retries nunca hacen `AVAILABLE < 0` ni consumen dos veces un remanente.
- **NFR-03 — Exactly-once lógico entre módulos.** Keys, fingerprints, attempts, constraints y signal replay producen una reserva, señal, liberación o transición lógica aunque haya timeout/restart entre transacciones locales.
- **NFR-04 — Disponibilidad segura.** Owner, catálogo, producer, storage, payload o evidencia ausente/ambiguo/corrupto nunca habilita Approval ni modifica saldo; un fallo técnico permanece retryable y una insuficiencia empresarial es explícita.
- **NFR-05 — Operación acotada.** Un prerequisite budget debido alcanza señal o compensación en máximo 60 segundos; leases/fencing impiden que un worker obsoleto confirme, y readiness distingue backlog, owner/producer, storage y corrupción.
- **NFR-06 — Observabilidad minimizada.** Métricas, logs y trazas correlacionan operación, tipo, resultado, retry y duración sin PII, importes, saldos, códigos empresariales ni snapshots.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | posición, buckets/deltas, transición parcial/reverse, all-or-nothing, parámetros v1, preimages/goldens, permisos y errores | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | SQL Server, append-only, reconstrucción, constraints, locks, carreras, attempts, leases/fencing, compensación y migración | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Docker/Testcontainers |
| API/E2E | JWT ADMIN/AUDITOR/requester, precheck minimizado, limits, Problem Details, health y cancelación segura | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` con Docker/Testcontainers |
| Contrato cross-module | PR persistida → Policy real → precheck real → adapter v3 → Approval → owner v1 → reserva/señal → proyección PR | Suite API/E2E sobre SQL Server efímero, sin budget owner, fact provider, owner registry ni Approval doubles |
| Operación | rollout con registros 0/1/2, prerequisite legacy, crash en cada frontera, dead attempt, restart, rebuild de saldos y rollback | Pruebas automatizadas y `docs/budget-operations.md` |

Todos los criterios son automáticos. Los negativos incluyen posición sin asignar, ref stale/inactiva/de otra organización, currency/Fiscal Year incorrectos, dos lines en una posición, dos posiciones, +1 demand, carrera por último saldo, reducción de allocation concurrente, child/reverse excedido, key distinta, owner/producer 0/2, issuer o client id parcial, v2 incompleto, signal temporalmente fallida, lease expirado, fencing stale, duplicate/tardy result y cancelación con takeover.

## Decisiones

- **DEC-01 — Asignación versionada, no movimiento ALLOCATE.** `ALLOCATED` proviene de revisiones ADMIN append-only; se descarta ampliar movimientos financieros con una fuente paralela difícil de reconciliar.
- **DEC-02 — Posición por identidades estables.** Cost Center UUID + Fiscal Year + Spend Category code identifica la posición; versiones/digest atestiguan el snapshot usado sin fragmentar presupuesto por cada rename.
- **DEC-03 — Reserva al satisfacer el prerequisite.** El owner señala `SATISFIED` solo después de reservar todo; se descarta check-only porque dos casos podrían aprobar contra el mismo saldo.
- **DEC-04 — Precheck auditable pero no vinculante.** `REQUESTED` permite explicar qué se comprobó, mientras la reserva transaccional sigue siendo la autoridad ante carreras.
- **DEC-05 — Lotes all-or-nothing.** Se agrupan por posición y se reservan todas las líneas o ninguna; se descarta aprobar parcialmente un prerequisite que Approval trata como una sola obligación.
- **DEC-06 — Consumo explícito.** `CONSUMED` es una transición propia desde `COMMITTED`; se descarta hacer que COMMITTED consuma directamente porque ocultaría obligaciones pendientes.
- **DEC-07 — Compensación durable, no transacción distribuida.** Reserva y señal usan un attempt recuperable con keys compartidas; se descarta XA y se exige reverse ante caso terminal no señalizable.
- **DEC-08 — Adapter v3 para datos completos.** Añadir Fiscal Year, Spend Category y detalle por target cambia el contrato de submission; v2 histórico permanece verificable y no se rellena desde datos actuales.
- **DEC-09 — Sin autoridad financiera para roles genéricos.** ADMIN asigna presupuesto y AUDITOR observa, pero movimientos de ejecución pertenecen a workloads exact-one; se descarta usar role administrativo como bypass.

## Plan de implementación

### Bloque 1 — Posiciones, asignaciones y saldos

- **T-01 — Dominio y contratos canónicos.** Implementar posición, allocation versions, balance, operations/movements, invariantes, transiciones parciales, reverse, canonicalización y golden. Añadir unitarias de tabla de deltas y límites. Cubre: REQ-01, REQ-02, REQ-03, REQ-10, NFR-01, NFR-02, CA-01, CA-02, CA-07.
- **T-02 — Persistencia y administración.** Crear schema/migración, constraints/triggers append-only, locks ordenados, audit y API ADMIN/AUDITOR con concurrencia y visibilidad. Añadir integración multi-DbContext y API/E2E. Cubre: REQ-01, REQ-02, REQ-03, REQ-10, NFR-01, NFR-02, NFR-04, NFR-06, CA-01, CA-02, CA-08.

**Resultado verificable:** allocation y movimientos reconstruyen exactamente cuatro buckets sin sobregiro, incluso bajo carreras y replay.

### Bloque 2 — Precheck y contrato Policy/Approval

- **T-03 — Precheck por referencia.** Implementar builder server-side desde Purchase Request, agrupación, operación REQUESTED, respuesta minimizada, idempotencia y paso durable de submit. Cubre: REQ-04, REQ-06, REQ-10, NFR-02, NFR-03, NFR-04, CA-03, CA-05, CA-08.
- **T-04 — Adapter v3 y parámetros owner.** Construir/cotejar demands completos desde líneas/manifest/attestation, publicar `budget-check-owner/v1`, source digest y compatibilidad histórica v2. Añadir contrato bidireccional y negativos de combinación multi-línea. Cubre: REQ-05, REQ-06, NFR-01, NFR-04, CA-04, CA-05, CA-07.

**Resultado verificable:** una evaluación con budget control produce un precheck reproducible y un prerequisite completo; cualquier dato omitido o adulterado bloquea antes del caso.

### Bloque 3 — Owner real y lifecycle

- **T-05 — Processor, reserva y señal.** Implementar registro exact-one, attempts, leases/fencing, reserva all-or-nothing, evidencia y signal idempotente; probar crash antes/después de reserva/señal e insuficiencia. Cubre: REQ-06, REQ-07, REQ-10, NFR-02, NFR-03, NFR-04, NFR-05, CA-05, CA-06, CA-07.
- **T-06 — Liberación, supersesión y cancelación.** Consumir resultados/lifecycle, transferir reservations de predecessor a replacement, compensar attempts y bloquear cancel/revision tras takeover. Añadir matrices de resultados duplicados/tardíos y carreras. Cubre: REQ-07, REQ-08, NFR-02, NFR-03, NFR-04, CA-06, CA-08.
- **T-07 — Contratos downstream.** Implementar registry workload-only y comandos idempotentes `COMMIT|CONSUME|REVERSE`, con producers productivos deshabilitados hasta sus specs. Cubre: REQ-03, REQ-09, REQ-10, NFR-01, NFR-02, NFR-04, CA-02, CA-07, CA-08.

**Resultado verificable:** Approval solo recibe SATISFIED tras una reserva real, todo terminal negativo libera o sustituye una vez y las transiciones financieras respetan parent/remanente.

### Bloque 4 — Operación y recorrido certificado

- **T-08 — Health, telemetría, runbook y E2E real.** Implementar readiness/códigos, reconstrucción, preflight, rollout/recovery y recorrido sin doubles con fault injection y dos instancias. Cubre: REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, NFR-03, NFR-04, NFR-05, NFR-06, CA-05, CA-06, CA-08.

**Resultado verificable:** suites y runbook demuestran el recorrido PR→Policy→Budget→Approval y recovery en cada frontera sin saldo parcial ni señal falsa.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, NFR-01, NFR-02 | Alta/revisión concurrente deja una sola allocation current e historia intacta; refs/FY/currency inválidos fallan. Reducir por debajo de reserved+committed+consumed se rechaza y reconstruir desde allocations/movements iguala exactamente cada bucket/proyección. | Automática: dominio, golden e integración SQL multi-DbContext/constraints. |
| CA-02 | REQ-02, REQ-03, REQ-09, NFR-01, NFR-02 | La tabla REQUESTED→RESERVED→COMMITTED→CONSUMED y REVERSE produce los deltas definidos, admite parcial hasta el remanente y rechaza salto, doble uso, exceso, negativo y actor no registrado. Un lote en dos posiciones confirma todo o nada bajo fallo forzado. | Automática: tabla unitaria, property tests e integración transaccional. |
| CA-03 | REQ-04, NFR-02, NFR-03 | Precheck agrupa dos líneas de una posición y varias posiciones, crea REQUESTED por target y no cambia buckets. Solo devuelve AVAILABLE si todas cubren; insuficiencia no reserva parcialmente. Replay recupera snapshot y key conflictiva da `409`; requester no recibe saldos. | Automática: unitarias, SQL y API/E2E por referencia. |
| CA-04 | REQ-05, NFR-01, NFR-04 | Adapter v3 produce exactamente un demand completo por target desde líneas/manifest/attestation. Dos líneas no pierden FY, Spend Category, amount ni target al combinar; v2 incompleto, extra, mismatch o ref stale da `503` sin caso. Historia v2 conserva bytes/digest. | Automática: contrato bidireccional, fixtures independientes e integración Policy→Approval. |
| CA-05 | REQ-06, NFR-02, NFR-03, NFR-04 | Submit con control budget confirma precheck antes del caso. El owner exact-one reserva todos los demands y luego señala SATISFIED con evidencia reproducible; saldo insuficiente señala FAILED sin RESERVED; error técnico queda WAITING. Dos submissions por el último saldo producen una SATISFIED y otra FAILED, nunca sobregiro. | Automática: SQL concurrente y E2E PR→Policy→Approval→Budget sin doubles. |
| CA-06 | REQ-07, REQ-08, NFR-02, NFR-03, NFR-05 | Crash/restart entre reserva y señal reutiliza attempt/reserva/signal; lease vencido permite reclaim y fencing obsoleto no confirma. Reject/change/cancel/supersede libera una vez; replacement cuenta predecessor, transfiere all-or-nothing y no falla por su propia reserva. Caso terminal no señalizable compensa en ≤60 s. | Automática: worker multiinstancia, fault injection, outbox duplicado/tardío y fake clock. |
| CA-07 | REQ-03, REQ-05, REQ-09, REQ-10, NFR-01, NFR-03 | El golden de position key y fixtures independientes de allocation/precheck/operation/evidence fijan bytes y hashes; permutar sets no cambia y alterar binding sí. Replays devuelven artefactos originales. COMMIT/CONSUME/REVERSE exigen producer+workload exactos y no quedan habilitados productivamente sin owner. | Automática: goldens, registry 0/1/2, auth y constraints de key/fingerprint. |
| CA-08 | REQ-04, REQ-08, REQ-10, NFR-04, NFR-05, NFR-06 | ADMIN asigna/lee operación y no ejecuta movimientos; AUDITOR scoped lee sin mutar; requester solo precheck propio; fuera de scope `404`. Cancelación aprobada libera antes de confirmar y takeover la bloquea. Health/preflight distinguen owner/processor/producer 0/2, legacy, backlog, storage y corrupción sin filtrar datos; rollout/rollback conservan historia. | Automática: API/E2E JWT/workload, health, migración, telemetría capturada y runbook. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Precheck se interpreta como reserva | Dos prechecks AVAILABLE compiten por el mismo saldo | Respuesta no vinculante, reserva autoritativa y CA-03/CA-05. |
| Control combinado pierde dimensión | Varias líneas terminan con amount/CC solamente del primer control | Demand por target, adapter v3 y CA-04. |
| Reserva huérfana entre módulos | Budget confirmó y Approval sigue WAITING/terminal | Attempt durable, replay/compensación y CA-06. |
| Supersesión falla contra su propio hold | Caso nuevo ve insuficiencia mientras predecessor retiene | Transferencia bajo lock y CA-06. |
| Doble consumo o reverse | Hijos exceden remanente del movimiento padre | Constraints, lock y CA-02. |
| Allocation reduction causa sobregiro | ADMIN y owner ganan carreras incompatibles | Mismo orden serializable de posiciones y CA-01/CA-05. |
| Owner solo configurado parece real | Readiness sano pero nadie procesa prerequisite | Processor exact-one requerido por preflight/health y CA-08. |
| Saldos o importes se filtran | Precheck, logs o health exponen datos financieros | Respuesta minimizada, captura de telemetría y CA-08. |
