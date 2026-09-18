# SPEC 11 — Purchase Orders, amendments y compra directa

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** No iniciada
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** 3df90fea3a48e59ec84b57a248c6d29cd5c7eae2a10b865ea44f7d69aa3f5cf9
> **Fecha:** 2026-09-17
> **Actualizada:** 2026-09-18
> **Aprobada el:** 2026-09-18
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Emitir y modificar Purchase Orders o autorizar compras directas desde fuentes aprobadas, con responsabilidad de aceptación, evidencia, takeover y movimientos presupuestarios verificables sin reescribir la Purchase Request.
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04, SPEC 07, SPEC 09
> **Modifica:** SPEC 05, SPEC 06, SPEC 08, SPEC 10
> **Reemplaza:** Ninguna

## Contexto

SPEC 10 termina en un award versionado y expone `award-consumption/v1`, pero todavía no existe el consumidor que formaliza la obligación comercial. SPEC 08 dejó `COMMIT + PURCHASE_ORDER` sin registration productivo, SPEC 05 solo mapea `supporting-document-owner/v1`, y las reglas temporales de SPEC 06/08/10 bloquean o liberan la cancelación sin un owner de PO. El resultado actual no puede emitir una PO, aplicar un amendment, autorizar una compra directa ni convertir la reserva exacta en compromiso. Esta entrega cierra esas fronteras usando el award como fuente del Supplier —sin write-back de `supplier_ref` en la PR— y preserva Finance Approval, Procurement Approval y Acceptance Owner como responsabilidades distintas.

## Alcance

### Incluye

- Purchase Orders de una sola Legal Entity, Supplier y moneda, creadas desde un award current y completo de SPEC 10.
- Versiones append-only, aprobación Procurement, emisión, amendments, cancelación total o parcial y takeover por líneas.
- Primer producer productivo `COMMIT + PURCHASE_ORDER` de SPEC 08, compensación `REVERSE` y recuperación durable entre PO y Budget.
- Acceptance Responsibilities por línea, asignadas a usuarios activos antes de emitir una PO o autorizar una compra directa.
- Compra directa para líneas aprobadas cuyo resultado Policy permite `ALLOW_DIRECT_PURCHASE` y no contiene `REQUIRE_PO`.
- Almacenamiento y processor real exact-one de `supporting-document-owner/v1`.
- Términos efectivos configurables tomados de Supplier Master, award y catálogo, congelados como snapshot; ninguna rama por nombre o id de proveedor.
- Proyección de estado/takeover en Purchase Requests, migración, audit, health, telemetría y runbook.

### No incluye

- Goods Receipt, Service Acceptance, Subscription Activation, matching, Supplier Invoice, consumo presupuestario, pagos o disputas.
- Almacenes, bins, inventario, transferencias, picking, stock o el rol `RECEIVER`/`RECEIVING_CLERK`.
- Supplier Portal, envío electrónico/EDI de la PO, firma electrónica, PDF fiscal, notificaciones o frontend.
- Contract Lifecycle Management, impuestos jurisdiccionales, catálogo genérico de ubicaciones ni Subscription Offering/renewal.
- Employee reimbursements, corporate cards o convertir una compra ya realizada sin autorización en `LOW_VALUE`.
- Cambiar Supplier, Legal Entity o moneda mediante amendment; exige cancelar cuando sea posible y producir un nuevo award/PO.

## Comportamiento esperado

- **REQ-01 — Creación desde award, claim y recuperación.** Solo un `PROCUREMENT_BUYER` activo con assignment `ORGANIZATION` crea una PO `DRAFT` mediante `award-consumption-claim/v1`. El comando consume exactamente un award current de SPEC 10, su set completo de líneas y su `award_content_digest`; no acepta Supplier, precios, cantidades, monedas, términos, snapshots ni refs Policy/Approval del caller. Sourcing vuelve a verificar `award-consumption/v1`, Supplier current `ACTIVE`, request/version, líneas y takeover, y en la misma transacción persiste un claim único, transfiere los takeovers de línea `SOURCING→PURCHASE_ORDER` y crea la versión PO con `delivery=null` y assignments vacíos. Replay idéntico recupera claim/PO; otra preimage, un award ya reclamado o una cobertura parcial devuelve `409` sin segunda PO. Cancelar antes de emitir deja el claim `RELEASED` y devuelve los takeovers al award; después de emitir, una cancelación total aprobada lo deja `CONSUMED_CANCELLED` y nunca permite reutilizar ese award.
  Si un award no reclamado deja de ser consumible —por ejemplo Supplier stale— el Buyer puede, con expected process/award versions y motivo, ejecutar `award-recovery/v1`: `REOPEN` devuelve el proceso `AWARDED→ACTIVE` conservando takeover para producir proposal/award sucesor; `CANCEL` marca el award/process terminal, abandona owner attempts y libera takeover para revisar/cancelar la PR. Recovery nunca omite una nueva Policy/Approval ni actúa sobre un claim existente.

- **REQ-02 — Documento PO y ciclo previo a emisión.** `PurchaseOrderVersion` es append-only y contiene PO number estable, organization, Legal Entity, award/request/proposal refs, Supplier, líneas, importes source/base, moneda, términos efectivos, delivery nullable, Acceptance Responsibilities, actor, UTC, estado, predecessor y digest. Claim crea `DRAFT` incompleto; antes de presentar, Buyer debe fijar delivery y assignments mediante sucesora DRAFT. Cada transición crea otra sucesora: `DRAFT→PENDING_APPROVAL→APPROVED→ISSUED`; rechazo crea `CANCELLED`, `REQUEST_CHANGES` exige una sucesora `DRAFT`, y una cancelación voluntaria pre-issue crea `CANCELLED`. Para cada award line, PO copia quantity/unit/unit price/source/base gross y deriva determinísticamente `subtotal=source_gross_total`, `taxes=additional_charges=discounts=0`; Release 1 no pretende reconstruir desglose tributario perdido por el award. Antes de emisión Buyer solo cambia delivery, assignments y referencias operativas no comerciales; quantity, unit, price, gross totals, Supplier, monedas y términos permanecen byte-equivalentes al award. Submit/approval exige delivery no nulo y assignments completos. Una PO emitida nunca se sobrescribe ni vuelve a draft.

- **REQ-03 — Procurement Approval separado de autoridad financiera.** Presentar una PO o amendment usa el adapter exact-one `purchase-order-approval-adapter/v1`, con subject `PURCHASE_ORDER|PURCHASE_ORDER_AMENDMENT`, operation `ISSUE_PURCHASE_ORDER|APPLY_PURCHASE_ORDER_AMENDMENT`, requester/originator Buyer y un requirement ordinario `PROCUREMENT_APPROVER + PROCUREMENT`, scope `ORGANIZATION`, acciones `APPROVE|REJECT|REQUEST_CHANGES` y Buyer excluido. El adapter obtiene server-side `purchase-request-ordering-evidence/v1` por request/version/targets: liga el case Approval current completado de la PR, su subject/operation/version/digest, resultados por target, requirements Department/Finance con role/authority/scope y budget evidence refs. Lo coteja con las refs Policy/Approval del award y acredita las aprobaciones aplicables al importe; jamás convierte `PROCUREMENT_APPROVER`, `ADMIN`, Job Title o una decisión de sourcing en `FINANCE_APPROVER` ni sintetiza authority financiera. Un aumento solo puede presentarse con un award sucesor ya reevaluado y aprobado para el nuevo importe. Case/ref/requirement/evidencia ausente, 0/2, stale o ambigua devuelve `503`; evidencia válida pero insuficiente devuelve `422`; no abre un caso parcial.

- **REQ-04 — Emisión y `COMMIT + PURCHASE_ORDER`.** Emitir exige versión PO current `APPROVED`, Acceptance Responsibilities completas, Supplier elegible, términos vigentes, claim activo y un único set de movimientos `RESERVED` abiertos que cubra exactamente cada target/importe base. El workload `purchase-order-domain` se registra productivamente como producer exact-one de `COMMIT + contract_version=v1 + PURCHASE_ORDER`; el comando conserva `command_version=budget-transition-command/v1`, usa source id/version/digest de la PO y una key determinista por versión. Un `PurchaseOrderBudgetAttempt` durable confirma COMMIT all-or-nothing y libera por `REVERSE` cualquier remanente reservado de esas líneas antes de crear la versión `ISSUED`. La PO no se publica como `ISSUED` hasta confirmar ambos efectos. Cero/dos producers, reserva 0/2, importe mayor al remanente, target ajeno o fallo técnico deja el attempt recuperable y la PO `APPROVED`; jamás marca emisión falsa ni duplica compromiso.

- **REQ-05 — Amendments y cancelación comercial.** Tras `ISSUED`, cualquier cambio usa un `PurchaseOrderAmendmentVersion` append-only con estado `DRAFT→PENDING_APPROVAL→APPROVED→APPLYING→APPLIED|CANCELLED`, expected PO/amendment versions, delta por línea, motivo y evidencia. Delivery/Acceptance Owner puede cambiar sin award sucesor; reducir o cancelar quantity/amount exige Procurement Approval y revierte solo el remanente `COMMITTED` no avanzado, primero a `RESERVED` y luego a `AVAILABLE`. Aumentar quantity/price o cambiar términos exige un award sucesor current de SPEC 10, misma Legal Entity/Supplier/moneda y líneas, con Policy/Approval vigentes; consume sus reservas adicionales y compromete solo el delta. Cambiar Supplier, Legal Entity o moneda se rechaza. Aplicar crea una sucesora PO `ISSUED` o `CANCELLED`, enlaza el amendment y confirma takeover/audit/outbox; fallo entre dominios se recupera con las mismas keys. Un compromiso ya consumido o una línea con takeover de fulfillment/invoice devuelve `409` sin cambio parcial.

- **REQ-06 — Acceptance Responsibilities.** Cada línea requiere exactamente los assignments de `acceptance-responsibility/v1`: `GOOD→GOODS_RECEIPT`, `SERVICE→SERVICE_ACCEPTANCE`, `SUBSCRIPTION→SUBSCRIPTION_PROVISIONING + SUBSCRIPTION_ACCESS_CONFIRMATION`. Un mismo usuario puede cubrir más de uno, pero cada kind aparece exactamente una vez por línea. El candidato por defecto es el `requested_for_user_ref` atestiguado de la PR; el Buyer puede seleccionar otro usuario activo de la misma organización y debe registrar motivo. Ser Requested For o assignee no otorga Approval Authority ni rol global. La v1 solo admite principal `{type=USER,id,version}`; un futuro principal `WAREHOUSE` exige nueva contract version, no campos nullable ni ids especiales. Issue/Direct Purchase falla `422` si falta un assignment, está inactivo/stale o pertenece a otra organización.

- **REQ-07 — Autorización de compra directa.** Un requester de sus líneas o un `PROCUREMENT_BUYER` organizacional crea `DirectPurchaseAuthorizationVersion` para un set no vacío de targets de una PR current aprobada mediante command exacto con `authorization_key`, `expected_request_version` y assignments; organización, actor, Supplier, importes y controles se derivan server-side. Todas las líneas comparten Legal Entity, Supplier ref current `ACTIVE` y moneda; el bundle current contiene `ALLOW_DIRECT_PURCHASE` para cada target, no contiene `REQUIRE_PO|BLOCK`, y `purchase-request-ordering-evidence/v1` prueba que todos sus requirements/prerequisites —incluidos Budget, Active Supplier y Supporting Document— están completados. El servidor deriva del bundle/PR el máximo source/base autorizado y los términos; no acepta importes, Supplier ni controles autocertificados. Con Acceptance Responsibilities completas crea `AUTHORIZED` y takeover único por línea; replay del mismo fingerprint recupera, otra preimage bajo la key o una línea ya consumida da `409`.
  Direct Purchase no mueve `RESERVED→COMMITTED`; una spec de Invoice/Matching definirá el consumo. Cancelar antes de ese takeover futuro libera la reserva mediante trigger `DIRECT_PURCHASE_CANCELLED` y crea versión `CANCELLED`. Un documento real que exceda el máximo deberá bloquear matching y requerir reevaluación o excepción futura; esta spec no autoriza el exceso.

- **REQ-08 — Supporting Documents y owner real.** Requester de la línea o `PROCUREMENT_BUYER` puede stage/confirmar un `ProcurementSupportingDocumentVersion` para una PR/version y targets exactos. Confirmación sella tipo de negocio `INVOICE|RECEIPT|OTHER`, file metadata, SHA-256, coverage, actor y UTC; bytes confirmados no se reemplazan. Para cada target, los mismos bytes (`sha256+length`) cuentan una sola vez aunque se carguen bajo roots, nombres o tipos distintos; un root confirmado no admite sucesora y una corrección crea otro root. `SupportingDocumentProcessorRegistration` contiene exactamente `{adapter_id,adapter_version,processor_id,workload_client_id,workload_issuer}` y es única por `(adapter_id,adapter_version)` para `supporting-document-owner/v1`; processor y owner workload persistido deben coincidir.
  Un worker dedicado, registrado en DI/hosted service y separado de Sourcing, reclama solo prerequisites cuyos parameters canónicos contienen `{document_types,minimum_count=1}`, verifica organization/case/subject/targets y cuenta por target bytes `CONFIRMED` únicos cuyo business type pertenece a la unión de tipos permitidos. `minimum_count` aplica a esa unión, no exige una copia por cada tipo. Solo cuando cada target alcanza el mínimo emite `SATISFIED` con `supporting-document-evidence/v1`; ausencia deja `WAITING`, corrupción/mismatch no señala, y error técnico reintenta. Nunca envía attachments a Approval. Attempts usan lease de 30 s, heartbeat ≤10 s, fencing, signal key determinista y reclaim; cancelación pre-signal termina `ABANDONED`. Health verifica registration 0/1/2, binding workload, storage y backlog; tests usan el worker productivo, no un signal manual.

- **REQ-09 — Términos configurables y vigencia.** `vendor-terms-snapshot/v1` se construye server-side, sin condicionales por Supplier, desde `supplier_content_ref` current de SPEC 09 (`payment_terms` y monedas soportadas), `award_ref`/`commercial-terms/v1` y sus `catalog_snapshot_refs`, o, para Direct Purchase, desde `request_ref` y `policy_bundle_ref`. Conserva source kind, todas esas refs/digests, payment code/net days, delivery/warranty/incoterm, moneda y `evaluated_at`. Para PO, snapshot/terms deben reproducir exactamente el award; para Direct Purchase, `award_ref=null`, Supplier debe soportar la moneda y payment term se deriva del Supplier current. Un Supplier/version stale, moneda no soportada, término contradictorio, source 0/2 o acuerdo vencido devuelve `422/503`. Reglas futuras como Subscription Offering se añadirán mediante contrato versionado; nunca mediante branch por nombre, Tax ID o UUID.

- **REQ-10 — Takeover, cancelación y proyección PR.** `purchase-request-line-takeover/v1` sustituye para nuevas operaciones el lock request-wide de SPEC 10: existe como máximo un takeover activo por `(organization,request_id,request_version,line_id,line_version)` con owner `SOURCING|PURCHASE_ORDER|DIRECT_PURCHASE|FULFILLMENT|INVOICE`, consumer ref/version y estado. La migración expande cada `SourcingTakeoverRecord` legacy desde el line set persistido de su process; solapamiento, línea ausente o process corrupto bloquea preflight, y las rows request-wide quedan solo históricas. SPEC 06/10 y esta spec consultan el resolver exact-one por línea; crear/cancelar/aplicar usa expected version y una valla transaccional, por lo que subset, rutas mixtas y carreras dejan una sola rama. SPEC 06 ya no confirma revisión/cancelación si existe claim PO, PO no cancelada o Direct Purchase `AUTHORIZED`; responde `409` con código opaco. Tras cancelación downstream completa y Budget sin `RESERVED|COMMITTED|CONSUMED`, la PR puede ejecutar su cancelación normal. Una PO `ISSUED` proyecta sus líneas `ORDERED`; una compra directa, `DIRECT_PURCHASE_AUTHORIZED`; subset o mezcla de rutas produce agregado `PARTIALLY_ORDERED`, todas PO `ORDERED`, y todas Direct Purchase `DIRECT_PURCHASE_AUTHORIZED`. Eventos duplicados/tardíos o de otra versión no retroceden la proyección.

- **REQ-11 — API, visibilidad, archivos y errores.** La API autenticada expone commands/reads de PO, amendments, Direct Purchase, assignments y supporting documents. Buyer crea/presenta/emite/amends; `PROCUREMENT_APPROVER` decide solo por Approval; requester solo crea/cancela su Direct Purchase y administra documentos de sus targets; `AUDITOR` organizacional lee historia/audit; `ADMIN` solo observa health/operación. Fuera de scope responde `404`. Descarga de documento usa URL temporal máxima 15 minutos y queda auditada; listados no exponen object key/URL. Problem Details: `400` JSON/contrato, `401`, `403`, `404`, `409` key/versión/takeover/estado, `413` límites, `422` elegibilidad/términos/assignments/evidencia/importe y `503 /problems/purchase-order-dependency-unavailable`. Máximos: 500 líneas, 100 documentos por caso, 20 MiB por archivo, 5 MiB por documento canónico, keys 1–128 `[A-Za-z0-9._:-]` y motivos/location 1–1.000 Unicode scalars.

- **REQ-12 — Idempotencia, operación y recuperación.** Commands, claims, submissions, signals y movimientos usan keys/fingerprints, expected versions, constraints, inbox/outbox, leases y fencing. Readiness degrada ante award verifier/claim, Policy/Approval, Supplier, storage, owner/processor o Budget producer 0/2; backlog mayor de 60 s; digest/takeover/ledger corrupto; o attempts huérfanos. Preflight impide habilitar commands si SPEC 01–10 no están integradas, hay reservations ambiguas o existen awards reclamados por una versión no desplegada. Telemetría informa etapa, versión, result, retry y duración sin Supplier, importes, delivery location, nombres de archivo, motivos, user ids, tokens, URLs, payloads ni digests completos.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `PurchaseOrderRoot` | id, organization, PO number, current version, rowversion | PO number único, inmutable y no reutilizable; el pointer solo avanza. |
| `PurchaseOrderVersion` | root/version, predecessor, state, award claim/ref, request/proposal/ordering-evidence refs, Legal Entity, Supplier, lines, totals, terms snapshot, delivery nullable, assignments, approval/budget refs, actor/UTC, digest | Append-only; DRAFT puede estar incompleto y submit exige completitud; una versión emitida solo recibe sucesora mediante amendment. |
| `PurchaseOrderLine` | PO line id, award/request line refs, quantity/unit, unit price, subtotal/taxes/charges/discounts/gross source/base, FX ref, assignments | Una award line produce una PO line; no hay split ni línea sin origen. |
| `AwardConsumptionClaim` | claim id/version, award ref, PO ref, covered lines, state, key/fingerprint, takeover refs, workload, UTC | `CLAIMED→ISSUED|RELEASED|CONSUMED_CANCELLED`; un claim activo por award/line. |
| `PurchaseOrderAmendmentVersion` | root/version, PO base ref, predecessor, state, deltas, successor award nullable, reason/evidence, approval/budget refs, actor/UTC, digest | Append-only; `APPLIED` produce exactamente una PO sucesora. |
| `PurchaseOrderBudgetAttempt` | PO/amendment ref, operation/release keys e ids, parents/amounts, state, lease/fencing, retry/error, timestamps | `PENDING→COMMITTING→RELEASING→COMPLETED`; compensación/reverse conserva ids originales. |
| `AcceptanceResponsibility` | source ref, line ref, kind, principal, candidate ref, assigner, reason, UTC, version | Exactamente una current por line+kind al presentar; historia append-only. |
| `PurchaseRequestOrderingEvidence` | request/version, targets, bundle ref, case ref, results, requirements, budget evidence refs, UTC, digest | Lo produce PR server-side; prueba case completado y authorities sin aceptar evidence del caller. |
| `PurchaseRequestLineTakeover` | request/line refs, owner, consumer ref, state, version, timestamps | Único activo por line version; reemplaza el lock request-wide para nuevas operaciones. |
| `DirectPurchaseAuthorizationVersion` | root/version, key/fingerprint, expected PR version, PR/bundle/case/ordering-evidence refs, covered lines, Supplier, maximum amounts, terms, assignments, document evidence refs, state, actor/UTC, digest | `AUTHORIZED→CANCELLED`; no es PO ni autoriza exceso. |
| `ProcurementSupportingDocumentVersion` | root/version, PR ref, covered targets, business type, file metadata, state, actor/UTC, digest | `STAGED→CONFIRMED`; confirmed inmutable, mismos bytes cuentan una vez por target. |
| `SupportingDocumentProcessorRegistration` | adapter id/version, processor id, issuer, client id | Exact-one para `supporting-document-owner/v1` y binding exacto al owner workload. |
| `SupportingDocumentOwnerAttempt` | prerequisite/case, parameters/control digest, targets, document refs, evidence, state, lease/fencing, retry | Único por prerequisite; ningún attachment vive en la señal. |
| `VendorTermsSnapshot` | Supplier/award/catalog refs, payment terms, currencies, commercial terms, evaluated UTC, digest | Inmutable y reproducible desde sources versionados. |
| `PurchaseOrderAuditRecord` | actor union, action/cause, target/version, changed fields, effect key, UTC, correlation | Append-only y atómico con la mutación local; sin payload sensible. |

### Estados y ownership

- Estados de negocio PO v1: `DRAFT|PENDING_APPROVAL|APPROVED|ISSUED|CANCELLED`. `PARTIALLY_FULFILLED|FULFILLED|CLOSED` quedan reservados para Fulfillment y no se infieren en esta entrega.
- Estados amendment v1: `DRAFT|PENDING_APPROVAL|APPROVED|APPLYING|APPLIED|CANCELLED`; `APPLYING` es visible a operación pero no sustituye la PO current hasta completar.
- Estado Direct Purchase v1: `AUTHORIZED|CANCELLED`; no se crea `AUTHORIZED` por mera presencia de `ALLOW_DIRECT_PURCHASE`.
- Purchase Orders es owner de PO/amendment/Direct Purchase/assignments y sus audit records. Sourcing conserva award/proposal; Budget conserva operaciones/movimientos; Approval conserva decisiones; Supplier conserva elegibilidad/términos base; storage conserva bytes.

### Schemas cerrados

Todos los objetos rechazan propiedades desconocidas. `content_ref={content_digest,id,version}`, `entity_ref={id,version}`, `line_ref={content_digest,id,version}` y `approval_target={id,material_snapshot_digest,type,version}`. Digests son 64 hex minúsculos, UUID no vacío y versión positiva.

- `purchase-order-line/v1` contiene exactamente `{acceptance_responsibilities,additional_charges,award_line_ref,base_currency,base_gross_total,discounts,fx_snapshot_ref,gross_total,line_id,quantity,request_line_ref,source_currency,subtotal,taxes,unit_code,unit_price}`. Importes son strings decimales canónicos `decimal(38,12)`; `gross_total=subtotal+taxes+additional_charges-discounts`, amounts/quantity relevantes son positivos y el line set reproduce el award.
- `delivery-commitment/v1` contiene exactamente `{delivery_date,delivery_location}`; fecha es calendar date futura al presentar y location tiene 1–1.000 Unicode scalars. En `purchase-order-version/v1`, `delivery` está presente null solo en DRAFT/CANCELLED pre-submit; `PENDING_APPROVAL|APPROVED|ISSUED` exige objeto completo. Cambiarlo después de issue es amendment aunque no exija award sucesor.
- `acceptance-responsibility/v1` contiene exactamente `{assigned_at,assigned_by_user_id,candidate_user_ref,kind,line_ref,principal,reason,version}`; `principal={id,type,version}` y `type=USER` en v1. Candidate es el Requested For exacto y puede diferir del principal.
- `vendor-terms-snapshot/v1` contiene exactamente `{award_ref,award_terms,canonicalization_version,catalog_snapshot_refs,contract_version,evaluated_at,payment_terms,policy_bundle_ref,request_ref,source_currency,source_kind,supplier_content_ref,supplier_supported_currencies}`. `source_kind=AWARD|DIRECT_PURCHASE`; `payment_terms={code,net_days}`; `catalog_snapshot_refs` es set de `{digest,line_ref}` de SPEC 10. AWARD exige award/terms/request/policy refs no nulas; DIRECT_PURCHASE exige award/terms null y request/policy no nulas. `supplier_content_ref` usa `{content_digest,id,version}`; sets presentes, quizá vacíos.
- `purchase-order-version/v1` contiene exactamente `{actor_user_id,amendment_ref,approval_ref,award_claim_ref,award_ref,base_amount,base_currency,budget_operation_refs,canonicalization_version,contract_version,delivery,issued_at,legal_entity_ref,lines,ordering_evidence_ref,organization_id,po_id,po_number,predecessor_version,proposal_ref,purchase_order_version,request_ref,source_amount,source_currency,state,supplier_ref,terms_snapshot}`. Nullables siempre están presentes y su combinación depende del estado; `ISSUED` exige approval/ordering evidence/budget refs e `issued_at` no nulos.
- `purchase-order-amendment/v1` contiene exactamente `{amendment_id,approval_ref,base_po_ref,canonicalization_version,contract_version,evidence_refs,expected_po_version,line_deltas,predecessor_version,reason,replacement_delivery,responsibility_changes,state,successor_award_ref,version}`. `amendment-line-delta/v1={change_kind,line_ref,previous_line,replacement_line}` reutiliza el documento completo `purchase-order-line/v1`; replacement es null solo para `CANCEL`, y `change_kind=REDUCE|CANCEL|COMMERCIAL`. `responsibility-change/v1={kind,line_ref,previous_assignment,replacement_assignment}` usa assignments completos; `replacement_delivery` es null si no cambia. Source/base deltas se derivan restando documentos completos con `decimal(38,12)`/`ToEven`, nunca llegan del caller. DELIVERY/RESPONSIBILITY no llevan line delta; COMMERCIAL exige successor award.
- `direct-purchase-command/v1` contiene exactamente `{acceptance_responsibilities,authorization_key,contract_version,covered_targets,expected_request_version,request_id}`. `authorization_fingerprint` añade server-side `{canonicalization_version,organization_id,actor_user_id,policy_bundle_ref,ordering_evidence_ref,supplier_ref,maximum_source_amount,maximum_base_amount,terms_snapshot_digest}` a todas las propiedades del command.
- `direct-purchase-authorization/v1` contiene exactamente `{acceptance_responsibilities,authorization_id,authorization_key,authorized_at,authorized_by_user_id,canonicalization_version,contract_version,covered_lines,document_evidence_refs,expected_request_version,fingerprint,maximum_base_amount,maximum_source_amount,ordering_evidence_ref,organization_id,policy_bundle_ref,request_approval_case_ref,request_ref,state,supplier_ref,terms_snapshot,version}`.
- `procurement-supporting-document/v1` contiene exactamente `{business_type,canonicalization_version,contract_version,covered_targets,file_ref,organization_id,request_ref,state,version}`; `file_ref={content_type,file_id,file_name,length,sha256,version}` y no contiene object key/URL.
- `supporting-document-evidence/v1` contiene exactamente `{attempt_id,canonicalization_version,checked_at,contract_version,counts_by_target,document_refs,organization_id,parameters_digest,prerequisite_id,result,signal_key,targets}`; `result=SATISFIED`, refs/targets son sets y `counts_by_target` cubre exactamente targets. `document_refs` conserva una ref por `(target,sha256,length)`; duplicados de bytes no aumentan count.
- `award-consumption-claim/v1` request contiene exactamente `{award_ref,claim_key,contract_version,covered_lines,organization_id,po_id,requested_at,workload_client_id,workload_issuer}` y response `{award_snapshot,claim_ref,claimed_at,contract_version,po_ref,replayed,takeover_refs}`. `takeover_refs` cubre exactamente las líneas. Workload coincide con identidad autenticada; `requested_at` no decide elegibilidad.
- `purchase-request-ordering-evidence/v1` request contiene `{contract_version,covered_targets,organization_id,request_ref,requested_at,workload_client_id,workload_issuer}` y response `{budget_evidence_refs,case_ref,checked_at,contract_version,covered_targets,policy_bundle_ref,requirements,result_refs}`. `case_ref={case_id,case_version,content_digest,operation,subject_id,subject_type,subject_version}`; cada requirement `{authority,decision_ref,key,role,scope,targets}` y cada result ref liga event/version/digest/targets/result. PR produce/verifica el digest; caller no aporta response.

### Budget, approval y takeover

- La registration productiva exacta es `{operation=COMMIT,contract_version=v1,source_type=PURCHASE_ORDER,producer_id=purchase-order-domain,workload_issuer=internal://procure-to-pay,workload_client_id=purchase-order-domain}`; el request conserva `command_version=budget-transition-command/v1` y `REVERSE` reutiliza identidad/source del padre conforme a SPEC 08. Registry, configuración y tests resuelven el literal `v1`; no se registra el nombre del schema en ese campo.
- El resolver de reserva recibe award/PO refs y targets; por target devuelve exactamente los movements `RESERVED` abiertos del case/binding autoritativo y sus remanentes. Cero, dos cadenas activas, suma insuficiente o movement de otra organización/source falla cerrado. No escoge “el más reciente”.
- `purchase-order-approval-adapter/v1` snapshot liga PO/amendment digest, award/policy/approval/ordering-evidence refs, targets, términos y deltas. El `submission_fingerprint` conserva SPEC 03; no reutiliza una decisión Procurement de Sourcing como decisión de PO.
- `purchase-request-line-takeover/v1` contiene exactamente `{canonicalization_version,consumer_ref,contract_version,line_ref,organization_id,owner,predecessor_ref,request_ref,state,version}`; `state=ACTIVE|RELEASED|TERMINAL`, y la valla compara todas las refs/versiones del set bajo locks locales en orden canónico.
- Direct Purchase cancellation amplía `budget-release-command/v1` con `trigger=DIRECT_PURCHASE_CANCELLED`; `trigger_event` contiene la ref inmutable de authorization. Históricos y triggers existentes no cambian.

### Canonicalización y digests

Todos los documentos propios usan `policy-canonical-json/v1` y SHA-256: UTF-8 sin BOM/whitespace, propiedades exactas presentes en orden ordinal, strings NFC, UUID `D` minúsculo, timestamps UTC con siete decimales, decimales como strings invariantes y sets ordenados por bytes canónicos sin duplicados. Cambiar una preimage exige nueva `canonicalization_version`; cambiar schema sin cambiar preimage exige nueva `contract_version`.

| Digest/fingerprint | Preimage exacta |
| --- | --- |
| `purchase_order_content_digest` | Todas y solo las propiedades de `purchase-order-version/v1`. |
| `purchase_order_amendment_digest` | Todas y solo las propiedades de `purchase-order-amendment/v1`. |
| `direct_purchase_authorization_digest` | Todas y solo las propiedades de `direct-purchase-authorization/v1`. |
| `authorization_fingerprint` | Todas y solo las propiedades públicas y server-side enumeradas para `direct-purchase-command/v1`. |
| `ordering_evidence_digest` | Todas y solo las propiedades de la response `purchase-request-ordering-evidence/v1`. |
| `supporting_document_digest` | Todas y solo las propiedades de `procurement-supporting-document/v1`. |
| `supporting_document_evidence_digest` | Todas y solo las propiedades de `supporting-document-evidence/v1`. |
| `vendor_terms_snapshot_digest` | Todas y solo las propiedades de `vendor-terms-snapshot/v1`. |
| `award_claim_fingerprint` | `award_ref`, `canonicalization_version`, `claim_key`, `contract_version`, `covered_lines`, `organization_id`, `po_id`, `workload_client_id`, `workload_issuer`. |
| `po_issue_fingerprint` | `approval_ref`, `award_claim_ref`, `budget_commands`, `canonicalization_version`, `expected_po_version`, `issue_key`, `po_content_digest`, `terms_snapshot_digest`. |

Fixtures independientes bajo `tests/ContractFixtures/PurchaseOrders/v1/` publican JSON completo y SHA-256 para PO, amendment/delta, Direct Purchase command/authorization, ordering evidence, supporting document/evidence, vendor terms, claim e issue. Una fixture mínima usa dos líneas en posiciones Budget distintas para impedir que un golden de una sola línea oculte pérdida de cardinalidad; pruebas recalculan bytes sin helpers de producción.

## Impacto sobre especificaciones anteriores

| Contrato anterior | Regla nueva y alcance |
| --- | --- |
| SPEC 05 REQ-03 y owner table | `supporting-document-owner/v1` deja de ser solo mapping fail-closed: esta spec registra processor real, attempt y evidence sin cambiar adapter id/version ni parameters históricos. |
| SPEC 06 REQ-09/REQ-10 | PR publica `purchase-request-ordering-evidence/v1` desde su case/result history y cancel/revision consulta takeover exact-one por línea: claim PO, PO emitida o Direct Purchase autorizada bloquea; tras cancelación downstream y liberación Budget puede continuar. La PR nunca recibe el Supplier del award. |
| SPEC 08 REQ-08/REQ-09 | Se habilita el primer registration productivo `COMMIT + PURCHASE_ORDER`; issue/amendment/cancel usan COMMIT/REVERSE y Direct Purchase añade trigger de release, sin reetiquetar movimientos históricos. |
| SPEC 10 REQ-01/REQ-12/REQ-14 | El takeover request-wide migra a rows por línea; `award-consumption/v1` sigue siendo verifier y `award-consumption-claim/v1` añade consumo único. `award-recovery/v1` permite REOPEN/CANCEL solo sin claim. Un successor award para amendment no cambia Supplier/Legal Entity/moneda y no desplaza la PO vigente hasta `APPLIED`. |

La vigencia empieza al integrar SPEC 11. POs, claims, authorizations o evidencias no se sintetizan para awards/PRs históricos. Cases, bundles, reservations y awards previos conservan bytes/digests; cualquier ambigüedad previa bloquea preflight y se corrige mediante comandos soportados, nunca update manual.

## Migración, despliegue y reversión

- La migración crea schema PurchaseOrder, roots/versiones, claims, amendments, Direct Purchase, assignments, documents, processor registrations, owner/budget attempts, audit/outbox, pointers e índices de unicidad; además expande takeovers Sourcing legacy a rows por línea desde el process line set. No crea POs desde awards existentes ni elimina rows históricas.
- Preflight verifica SPEC 01–10 integradas, migrations aplicadas, storage, Supplier/Policy/Approval/Sourcing/Budget disponibles, processor/owner/adapter/producer exact-one, workloads coincidentes, cero reservation/claim ambiguity y expansión 1:1 de cada takeover legacy a sus líneas sin solapamiento.
- Orden: migración; readers y contratos; adapter/verifier/processor deshabilitados; registration del workload y Budget producer; commands de draft/documentos; worker de supporting docs; por último issue/amendment/Direct Purchase. Hasta habilitar, falta de owner/producer devuelve `503`, nunca ALLOW/ISSUED/AUTHORIZED.
- Rollback detiene nuevos claims/commands, conserva readers, worker y recuperación de attempts ya confirmados. Tras primer COMMIT, signal o authorization no hay downgrade destructivo: se corrige hacia adelante y se mantienen consumers de contratos v1.
- `docs/purchase-order-operations.md` documenta rollout, configuración, storage, claim/takeover, recovery de COMMIT/REVERSE/signal, rotación de credenciales, reservation ambiguity, rollback y evidencia corrupta sin editar filas.

## Seguridad y privacidad

- Buyer prepara y ejecuta commands; Procurement Approver decide en Approval; Finance Approver solo decide requirements con authority financiera; ninguno hereda la facultad del otro. `ADMIN` no bypassa controles empresariales.
- Organization/actor/workload se derivan del contexto autenticado. `issuer+client_id`, owner/adapter id, expected version, scope, SoD, Supplier y digests se revalidan en la transacción.
- Acceptance Owner es una responsabilidad de proceso, no rol ni aprobador. El assignee solo obtendrá capacidades de Fulfillment cuando su spec las defina.
- Supporting documents, delivery location, file names, motivos, Supplier e importes pueden ser sensibles. URLs temporales duran ≤15 minutos; object keys, bytes, nombres, ubicación, textos, PII y datos comerciales no aparecen en logs, traces, metrics, health, outbox ni Problem Details.
- Lecturas fuera de scope responden `404`; corrupción o dependencia no revela si otro tenant posee ids. Audit conserva actor ids solo en storage autorizado, no en telemetría.

## Requisitos no funcionales

- **NFR-01 — Inmutabilidad reconstruible.** Versiones, amendments, claims, assignments, documents, evidence, movements y audit son append-only; current se reconstruye desde historia/pointers validados.
- **NFR-02 — Determinismo contractual.** Mismos sources producen mismos bytes, totals, deltas, snapshots y digests; permutar sets no cambia, alterar binding/importe/ref sí.
- **NFR-03 — Exactly-once lógico.** Dos instancias, retries, timeout y restart producen un claim, submission, signal, budget effect y PO sucesora lógicos.
- **NFR-04 — Disponibilidad segura.** Dependencia, owner, processor, producer, reservation o verifier ausente/ambiguo/stale/corrupto nunca produce `APPROVED|ISSUED|AUTHORIZED|SATISFIED`.
- **NFR-05 — Atomicidad recuperable.** Cada transacción local es atómica y las fronteras Sourcing/Approval/Budget usan attempts/keys recuperables; no se requiere transacción distribuida.
- **NFR-06 — Tiempo y dinero.** Instantes UTC, fechas ISO, monedas ISO 4217, `decimal(38,12)`/`ToEven`; no coma flotante binaria ni reloj del caller para vigencia.
- **NFR-07 — Observabilidad minimizada.** Telemetría correlaciona ids opacos, etapa, estado, retry y duración mediante allowlist, y pruebas negativas impiden contenido sensible.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | estados/deltas, totals, assignments por tipo, terms, Policy route, canonicalización y goldens | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | SQL append-only, claims/takeover, Approval, COMMIT/REVERSE, attempts, leases/fencing, processor documents y migración | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Docker |
| API/E2E | JWT/workload, roles, visibilidad, archivos, limits, Problem Details y recorridos PO/Direct Purchase | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` con Docker |
| Cross-module | award→claim→PO Approval→COMMIT→ISSUED, amendment→REVERSE/delta y PR→documents owner→Direct Purchase | Suite E2E sobre SQL Server/LocalStack efímeros, sin doubles de Sourcing, Policy, Approval, Budget o Supplier |
| Operación | rollout, restart/reclaim, storage/owner/producer 0/2, backlog, corrupción, rollback y runbook | Automatización descrita en `docs/purchase-order-operations.md` |

Todos los CA son automáticos. Negativos incluyen partial award, Supplier stale, Finance evidence insuficiente, Procurement autoaprobación, owner/processor/producer 0/2, dos reservas autoritativas, documento adulterado/tipo incorrecto, missing Acceptance Owner, issue vs cancel, amendments concurrentes, amount consumido, direct purchase con `REQUIRE_PO`, replay conflictivo, crash en cada frontera y telemetría con marcadores sensibles.

## Decisiones

- **DEC-01 — Supplier permanece en award/PO.** La PO consume el Supplier seleccionado y la PR no se reversiona ni recibe write-back; se evita reevaluación circular y divergencia de evidencia.
- **DEC-02 — Claim antes que lectura optimista.** `award-consumption/v1` verifica y el nuevo claim transfiere takeover de forma atómica; una lectura sola no impide dos POs.
- **DEC-03 — Procurement aprueba la obligación, Finance conserva su authority.** La PO exige decisión Procurement propia y verifica evidencia financiera upstream; se descarta tratar roles como equivalentes o duplicar Finance sin cambio material.
- **DEC-04 — Amendment para toda mutación post-issue.** La PO emitida es inmutable; incluso delivery/Acceptance Owner crea historia causal. Cambios comerciales crecientes requieren award sucesor.
- **DEC-05 — Direct Purchase no es PO implícita.** Autoriza un máximo y conserva `RESERVED`; no crea COMMIT artificial ni elimina evidencia/acceptance/matching futuros.
- **DEC-06 — Assignments tipados, no rol global.** Responsibilities por línea satisfacen GOOD/SERVICE/SUBSCRIPTION; Warehouse se añade con nueva versión futura.
- **DEC-07 — Términos por snapshots versionados.** Supplier/award/catalog son fuentes configurables; se descartan branches por proveedor y una mini-CLM en esta entrega.
- **DEC-08 — Sagas durables sobre XA.** Issue/amendment/cancel usan attempts e idempotency keys; se descarta una transacción distribuida Sourcing/Approval/Budget/storage.

## Plan de implementación

### Bloque 1 — Núcleo PO, award claim y responsabilidades

- **T-01 — Modelo/versiones y claim.** Implementar roots/versiones/lines, canonicalización, constraints, `award-consumption-claim/v1`, `award-recovery/v1`, takeover por línea/migración y cancelación pre-issue. Cubre: REQ-01, REQ-02, REQ-10, NFR-01, NFR-02, NFR-03, CA-01, CA-02.
- **T-02 — Acceptance y términos.** Construir assignments por tipo, validación de usuarios, defaults Requested For y `vendor-terms-snapshot/v1` desde sources reales. Cubre: REQ-06, REQ-09, NFR-02, NFR-04, NFR-06, CA-03.

**Resultado verificable:** un award exacto produce una sola PO draft reproducible con takeover; tras T-02, una sucesora DRAFT completa assignments y términos sin modificar Supplier en PR.

### Bloque 2 — Approval, emisión y presupuesto

- **T-03 — Adapter y lifecycle Approval.** Implementar descriptor/adapter, `purchase-request-ordering-evidence/v1`, evidencia Finance upstream, SoD, submit/result consumer y sucesoras de estado. Cubre: REQ-02, REQ-03, REQ-07, NFR-03, NFR-04, CA-04, CA-07.
- **T-04 — Producer Budget e issue saga.** Registrar `COMMIT + PURCHASE_ORDER`, resolver parents exactos, COMMIT/release remanente, attempts, recovery y proyección PR. Cubre: REQ-04, REQ-10, REQ-12, NFR-03, NFR-04, NFR-05, CA-05, CA-09.

**Resultado verificable:** solo una PO aprobada pasa a ISSUED después de comprometer exactamente su importe y liberar el remanente, aun con crash/retry.

### Bloque 3 — Amendments y compra directa

- **T-05 — Amendments y reversas.** Implementar delta, aprobación, successor award, reducción/cancelación, COMMIT adicional, REVERSE, fencing y PO sucesora. Cubre: REQ-05, REQ-10, REQ-12, NFR-01, NFR-03, NFR-05, CA-06.
- **T-06 — Direct Purchase.** Resolver Policy/Approval current, Supplier, maximum amounts, assignments, takeover, autorización/cancelación y release Budget. Cubre: REQ-07, REQ-09, REQ-10, NFR-03, NFR-04, CA-07.

**Resultado verificable:** amendments ajustan solo remanentes válidos y Direct Purchase se autoriza exclusivamente por controles completados, sin inventar una PO ni un COMMIT.

### Bloque 4 — Documents owner, API y operación

- **T-07 — Documentos y processor real.** Implementar staging/confirmación/dedupe/descarga, `SupportingDocumentProcessorRegistration`, DI/hosted worker, attempt/evidence/signal, health, leases/fencing y negatives de corrupción. Cubre: REQ-08, REQ-11, REQ-12, NFR-01, NFR-03, NFR-04, NFR-07, CA-08.
- **T-08 — API, seguridad y límites.** Exponer commands/reads, JWT/workload, visibility, Problem Details, size limits, audit y telemetría minimizada. Cubre: REQ-11, NFR-04, NFR-07, CA-09.
- **T-09 — Migración, health y E2E.** Añadir migration/preflight/readiness, runbook, fault injection, dos instancias y recorridos cross-module completos. Cubre: REQ-12, NFR-03, NFR-04, NFR-05, NFR-07, CA-10.

**Resultado verificable:** el sistema recupera claims/signals/movimientos interrumpidos y health detecta toda ambigüedad sin filtrar datos comerciales.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-10, NFR-03 | Dos Buyers/instancias sobre el mismo award completo producen un claim y una PO; partial/stale/digest distinto falla. Cancel pre-issue devuelve takeovers de línea al award; post-issue nunca reutiliza award. Un award no reclamado con Supplier stale puede REOPEN para sucesor o CANCEL para liberar, pero no saltarse Policy/Approval. PR no cambia `supplier_ref`. | Integración SQL concurrente + E2E award/recovery real. |
| CA-02 | REQ-02, NFR-01, NFR-02 | Claim crea DRAFT con delivery null/assignments vacíos; submit incompleto falla. Versiones recorren DRAFT→PENDING→APPROVED→ISSUED/CANCELLED sin update/delete. Quantity/unit/prices/gross igualan award; subtotal=gross y taxes/charges/discounts=0 de forma reproducible; cualquier override se rechaza y goldens fijan hashes. | Unitarias, fixtures y constraints SQL. |
| CA-03 | REQ-06, REQ-09, NFR-02, NFR-04, NFR-06 | GOOD/SERVICE/SUBSCRIPTION exigen sus kinds exactos; default usa Requested For y override audita. Usuario stale/foreign/missing falla. Terms y todas sus refs/provenance se reconstruyen desde Supplier/award o PR/Policy con UTC, ISO 4217 y decimales exactos; branch hardcoded queda cubierto por dos Suppliers/configuraciones. | Unitarias + integración de owner user/Supplier. |
| CA-04 | REQ-03, NFR-03, NFR-04 | `purchase-request-ordering-evidence/v1` liga case, targets, results y requirements exactos. Buyer no se autoaprueba; Procurement decision no satisface Finance. Evidencia financiera/case ausente, 0/2, insuficiente o stale bloquea, y aumento con award reevaluado crea caso PO exacto sin duplicar requirements financieros. | Contrato adapter + PR/Approval E2E con authority/SoD. |
| CA-05 | REQ-04, REQ-12, NFR-03, NFR-05 | Issue sobre dos posiciones ejecuta un COMMIT all-or-nothing y libera remanente antes de ISSUED. Producer/reservation 0/2, crash antes/después de Budget y redelivery recuperan mismos ids sin doble saldo ni emisión falsa. | Integración Budget multiinstancia/fault injection + E2E. |
| CA-06 | REQ-05, NFR-01, NFR-03, NFR-05 | Delivery/owner cambia mediante amendment; documentos completos previous/replacement reproducen deltas; reducción/cancel revierte solo remanente; aumento exige successor award y compromete delta. Supplier/entity/currency change, consumed amount y dos amendments concurrentes fallan sin parcial. | Goldens/matriz unitaria + SQL concurrente + Budget real. |
| CA-07 | REQ-07, REQ-09, REQ-10, NFR-04 | Command/fingerprint liga key, expected PR version, targets, case/ordering evidence y sources server-side. `ALLOW_DIRECT_PURCHASE` por todos los targets, sin REQUIRE_PO/BLOCK y con case completo crea AUTHORIZED por máximo derivado; replay recupera y otra preimage da `409`. Falta de docs/Supplier/Budget/assignment, exceso o takeover previo bloquea; cancel release una vez. | Policy/PR/Approval/Budget/Supplier E2E sin doubles. |
| CA-08 | REQ-08, NFR-01, NFR-03, NFR-04 | Documentos confirmed cuyo tipo pertenece al set permitido cuentan bytes únicos por target hasta `minimum_count`; los mismos bytes bajo otro root/nombre/tipo permitido cuentan una vez. Staged, adulterado, foreign o tipo no permitido no señala. Registration/owner/worker 0/2, restart, lease/fence obsoleto y signal perdida producen un SATISFIED lógico y evidence reproducible sin attachment usando DI/hosted service productivo. | Storage+SQL integration, health, goldens y E2E owner real. |
| CA-09 | REQ-10, REQ-11, NFR-04, NFR-07 | Migración expande takeovers request-wide a líneas exactas y rechaza solapamiento; subset/mixed routes conviven, bloquean cancel/revision PR y carreras terminan en una rama. Proyección produce PARTIALLY_ORDERED/ORDERED/DIRECT_PURCHASE_AUTHORIZED. Roles/scope/404, límites ±1 y descarga temporal se cumplen; captura de logs/traces no contiene marcadores sensibles. | Migración + API/E2E JWT/workload, concurrencia y telemetría negativa. |
| CA-10 | REQ-12, NFR-03, NFR-04, NFR-05, NFR-07 | Migración/rollback conservan historia; preflight/readiness detectan dependencia/owner/processor/producer/reservation/claim 0/2, takeover legacy no expandible, backlog >60 s y corrupción. Runbook recupera claim, signal y budget attempt interrumpidos sin editar datos. | Test migración, health E2E y ejercicio automatizado del runbook. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Award se consume dos veces | dos POs/claims activos por award o línea | Claim único, takeover fence y CA-01. |
| Procurement suplanta Finance | PO emitida sin authority financiera upstream | REQ-03, adapter cerrado y CA-04. |
| COMMIT usa reserva equivocada | target con 0/2 parents o case distinto | Resolver exact-one fail-closed y CA-05. |
| Crash deja PO y Budget divergentes | COMMIT confirmado con PO no emitida | Attempt durable, recovery por keys y CA-05/CA-10. |
| Amendment libera gasto ya avanzado | REVERSE sobre committed con child CONSUMED | Validación de remanente/takeover y CA-06. |
| Documento staged satisface control | signal sin bytes confirmados o tipo inválido | Seal SHA-256, count por target/tipo y CA-08. |
| Direct Purchase se usa como bypass | authorization con REQUIRE_PO/control WAITING | Revalidación bundle/case exactos y CA-07. |
| Warehouse queda codificado como usuario falso | id especial o principal ambiguo | Principal v1 cerrado USER; nueva versión futura. |
| Datos comerciales filtran por operación | log/health con Supplier, amount, file/location | Allowlist, captura negativa y CA-09/CA-10. |
