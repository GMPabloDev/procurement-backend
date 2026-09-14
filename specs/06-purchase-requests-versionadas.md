# SPEC 06 — Purchase Requests versionadas e integración con Policy y Approval

> **Formato:** sdd/v3
> **Estado:** Implementada
> **Ejecución:** Integrada
> **Vigencia:** Vigente
> **Revisión:** 1
> **Digest contractual:** f8c570320cc1093698b8b2998c83eee168335c5345236a2fe457755bf4a41dd8
> **Fecha:** 2026-09-13
> **Actualizada:** 2026-09-14
> **Aprobada el:** 2026-09-13
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Crear y revisar Purchase Requests multilínea mediante versiones y líneas inmutables, atestiguar su completitud y presentarlas por referencia al motor de políticas y al Approval Workflow sin confiar en snapshots del caller.
> **Depende de:** SPEC 01
> **Modifica:** SPEC 02, SPEC 03, SPEC 04, SPEC 05
> **Reemplaza:** Ninguna

## Contexto

La fuente funcional define la Purchase Request como contenedor y cada Purchase Request Line como unidad principal de política, presupuesto, aprobación y trazabilidad; exige historial ante cambios materiales y permite múltiples líneas y Cost Centers. Las SPEC 02 y 05 ya implementaron evaluación por referencia, `CompletenessManifest`, targets de línea y el adapter Policy→Approval, pero el repositorio solo registra providers controlados en tests: no existe el dominio Purchase Requests ni un `IPolicyFactProvider` de producción. Además, el adapter actual no ofrece `REQUEST_CHANGES`, SPEC 03 rechaza que requester y originator sean la misma persona y la supersesión de SPEC 04 exige cardinalidad idéntica; esas tres reglas impiden integrar el flujo real donde el requester crea/presenta y una revisión puede agregar o retirar líneas.

## Alcance

### Incluye

- Identidad estable de Purchase Request y líneas, con snapshots de contenido inmutables y cadena de versiones append-only.
- Revisiones que reutilizan la versión exacta de cada línea sin cambios y crean una versión nueva para cada línea modificada; altas y bajas quedan declaradas en el delta.
- Manifest persistido que atestigua el conjunto completo de líneas de una versión y enlaza digests de contenido y referencias externas verificadas.
- Creación, revisión, presentación, cancelación y lectura hasta `APPROVED|PARTIALLY_APPROVED|REJECTED|CHANGES_REQUESTED`, con estado agregado derivado de líneas.
- Provider in-process real para `PURCHASE_REQUEST/REQUEST_EVALUATE`, compatible con SPEC 02 y registrado exactamente una vez.
- Orquestación idempotente de presentación: attestation → evaluación Policy → apertura o supersesión del caso Approval mediante el adapter de SPEC 05.
- Consumo idempotente de resultados Approval por target de línea, historial/audit, concurrencia, seguridad, límites, migración, operación y pruebas.
- Extensión diferencial de SPEC 03/04/05 para requester=originator, `REQUEST_CHANGES` y supersesión con líneas retenidas, agregadas o retiradas.

### No incluye

- Administración de Cost Centers, Spend Categories, catálogos de productos, schemas de riesgo, tipos de cambio, Supplier Master o usuarios; sus owners se consumen por contratos versionados exact-one.
- Movimientos `REQUESTED`, reserva o disponibilidad presupuestaria; el prerequisite de SPEC 05 permanece fail-closed hasta que exista su owner.
- Sourcing, RFQ, cotizaciones, award, Procurement material, Purchase Orders, Direct Purchase, fulfillment, invoices o payments.
- Selección de proveedor, condiciones finales de PO, división de una línea entre proveedores o mezcla de Purchase Requests.
- Adjuntos, documentos de soporte, frontend, notificaciones o búsquedas/reportes operativos avanzados.
- Cambiar reglas, políticas, thresholds, routing de aprobadores o semántica de decisiones ya emitidas.

## Comportamiento esperado

- **REQ-01 — Identidades y contenido inmutables.** Una `PurchaseRequest` tiene UUID estable, organización y secuencia monotónica; cada `PurchaseRequestVersion` y `PurchaseRequestLineVersion` es append-only desde su creación. La primera versión de request y línea es 1. Una revisión exige versión actual esperada y `revision_key`; crea exactamente una request version sucesora, reutiliza refs de líneas cuyo contenido no cambia, crea `line_version+1` para cada línea modificada, crea versión 1 para altas y omite del nuevo manifest las bajas declaradas. Nunca actualiza, reasigna ni elimina físicamente una versión previa. UUID vacío, predecessor ajeno, line id repetido, salto de versión, línea de otra request/organización o manifest vacío se rechaza.

- **REQ-02 — Revisión material y delta explícito.** `organization_id`, `requester_id`, `request_id` y `legal_entity_ref` quedan fijados al crear la Purchase Request; cambiarlos exige otra request. Una revisión recibe el nuevo `business_justification` completo y los cuatro sets exactos de `purchase-request-revision-command/v1`: `retained`, `changed`, `added` y `removed`. La unión de `retained+changed+removed` coincide con las líneas de la versión anterior y `retained+changed+added` con la nueva. `retained` conserva id/version/digest; `changed` aporta el contenido sucesor, conserva id y avanza una versión; `added` aporta `client_line_key+content` y recibe id nuevo/versión 1; `removed` no reaparece. El delta persistido guarda previous/replacement completos según Datos y contratos. Se permite revisar desde `DRAFT`, `CHANGES_REQUESTED`, `IN_APPROVAL` o `APPROVED` con motivo; desde `REJECTED` o `CANCELLED` se crea otra Purchase Request. Una sucesora de versión presentada siempre obtiene evaluación y caso nuevos.

- **REQ-03 — Datos completos de la línea.** El payload `purchase-request-line-content/v1` contiene exactamente los campos y refs tipadas de Datos y contratos. Cada línea identifica su `requested_for_user_ref`; contiene resumen de necesidad, `purchase_type`, Spend Category, Cost Center, Fiscal Year, Beneficiary Department, importe bruto estimado y moneda, equivalente/moneda base, prueba FX cuando aplique, respuestas de riesgo, flags contractuales y exactamente cero o una ref de Preferred Product, Required Product y Supplier. Preferred y Required Product no pueden coexistir. Importe negativo, moneda no ISO 4217, ejercicio fuera de rango configurado, referencia/versión vacía, usuario fuera de organización o facts no representables por SPEC 02 dan `422`. `business_justification` puede cambiar por versión; `need_summary` puede cambiar por line version, pero ambos se excluyen del bundle Policy y de materiality digests.

- **REQ-04 — Attestation de referencias y completitud.** Al presentar, el dominio resuelve exactamente un owner versionado para Legal Entity, usuarios, Cost Center, relación Cost Center→Department, Spend Category, schemas/respuestas de riesgo, productos y FX requerido. Verifica organización, versión, vigencia en el instante servidor y que el Department propietario del Cost Center coincide con el snapshot; Beneficiary Department no sustituye esa relación. Ausencia, ambigüedad, timeout o fallo de owner da `503`; referencia inexistente/inactiva, relación o tipo inválidos da `422`. En una transacción local persiste `PurchaseRequestReferenceAttestation` y un `PurchaseRequestCompletenessManifest` inmutables. El manifest enumera exactamente una ref id/version/content digest por cada línea de la request version, sin duplicados, e incluye `policy_manifest_digest` y `domain_attestation_digest` reproducibles según Datos y contratos.

- **REQ-05 — Provider in-process atestiguado.** `PurchaseRequestPolicyFactProvider` se registra exactamente una vez para `subject_type=PURCHASE_REQUEST`, `operation=REQUEST_EVALUATE`, `provider_id=purchase-request-domain` y `contract_version=purchase-request-policy-facts/v1`. Solo acepta una `FactRequest` de SPEC 02 y carga la request version exacta; no recibe facts, total ni manifest por HTTP. Recalcula request/line content digests, reference attestation, policy manifest y domain attestation antes de producir `PolicyFactBundle`. Proyecta únicamente los facts tipados de SPEC 02, con provenance por fact hacia la versión/attestation propietaria, y usa como manifest de Policy el mismo request id/version y set completo de line id/version persistidos. Versión no atestiguada, digest corrupto, línea ausente, source version mezclada o intento de servir otra organización falla `503` sin bundle; referencia de sujeto inexistente/no visible da `404` solo en la superficie autorizada del dominio.

- **REQ-06 — Presentación idempotente sin ventana fail-open.** Solo el requester activo de la versión actual puede presentar con `submission_key`, versión esperada y motivo. La key es única en `(organization_id, requester_id, PURCHASE_REQUEST_SUBMIT)` y su fingerprint liga request/version/content, manifest/attestation, actor y causa, excluyendo reloj/correlation. La operación durable crea o recupera un `PurchaseRequestSubmissionAttempt` y usa keys internas deterministas ligadas a request/version para: (1) obtener la evaluación Policy por referencia; (2) rechazar `BLOCKED`; y (3) abrir, o superseder si existe caso previo, `SUBMIT_PURCHASE_REQUEST` mediante `policy-approval-adapter/v2`. Reintento idéntico recupera bundle/caso originales; otra carga con la key da `409`. La request solo proyecta `SUBMITTED` y después `IN_APPROVAL`/`APPROVED` cuando se confirmaron refs de Policy y Approval; si Policy bloquea devuelve `422`, y si una dependencia falla devuelve `503`, conservando attempt/evaluación ya confirmados para retry pero sin declarar éxito ni crear datos parciales dentro de cada módulo.

- **REQ-07 — Integración Approval y cambios solicitados.** `policy-approval-adapter/v2` exige `requester_id`, permite `APPROVE|REJECT|REQUEST_CHANGES` en cada requirement humano y conserva exclusiones SoD. Para este adapter SPEC 03 admite `requester_id=originator_id`: la identidad se incluye una sola vez en el set de exclusiones y sigue sin poder aprobar. Un resultado `CHANGES_REQUESTED` marca las líneas target y la request agregada como `CHANGES_REQUESTED`; solo una nueva versión/caso enlazado continúa. `REJECTED`, `CANCELLED`, decisiones y evidencias históricas nunca se mutan ni se atribuyen al requester.

- **REQ-08 — Supersesión con cardinalidad variable.** Para `policy-approval-adapter/v2`, la extensión `approval-supersession-delta/v1` sustituye el mapping de igual cardinalidad de SPEC 04 por entradas cerradas `RETAINED|ADDED|REMOVED`. `RETAINED` exige previous y replacement con el mismo line id y un `purchase-request-materiality/v1` verificable; `ADDED` exige solo replacement de id nuevo; `REMOVED`, solo previous. Cada target anterior aparece exactamente una vez como `RETAINED|REMOVED` y cada target nuevo exactamente una vez como `RETAINED|ADDED`; no se permite fusión, división ni omisión. El caso previo completo y sus nodos no terminales quedan `SUPERSEDED`; removed no crea target nuevo y added nunca hace carry-forward. Para retained, target version/digest puede cambiar, pero carry-forward solo ocurre si coinciden materiality digest, requirement contract, acciones, authority, scope, dependencias, exclusiones y conjunto estable cubierto; cualquier diferencia o duda crea task nueva.

- **REQ-09 — Resultados y estado agregado por línea.** Un consumer in-process de `approval-result/v2|v3` y `approval-case-lifecycle/v1` deduplica por `event_id+contract_version`, verifica organización, case, subject/version, source y target exactos, y registra `PurchaseRequestApprovalResult` append-only. Mantiene por línea las obligaciones esperadas del caso: todos los requirements deben terminar `APPROVED` y todos los prerequisites `SATISFIED` para que la línea sea `APPROVED`; `REJECTED` y `CHANGES_REQUESTED` prevalecen para sus targets; `CANCELLED` o `SUPERSEDED` no se convierten en aprobación. La request actual queda `APPROVED` si todas sus líneas están aprobadas, `REJECTED` si todas están rechazadas, `PARTIALLY_APPROVED` si al menos una está aprobada y otra no terminal o rechazada, `CHANGES_REQUESTED` si alguna línea vigente lo está, e `IN_APPROVAL` en los demás casos abiertos. Eventos duplicados, tardíos de una versión superseded o fuera del manifest conservan historia pero no alteran la proyección actual.

- **REQ-10 — Cancelación, lectura y auditoría.** En esta entrega, donde ningún dominio downstream consume la request, el requester puede cancelar la versión actual en `DRAFT|CHANGES_REQUESTED|IN_APPROVAL|APPROVED`; `REJECTED|CANCELLED` son terminales. Si Approval informa un caso no terminal, el workload owner debe cancelarlo con key/versión esperada antes de confirmar `CANCELLED`; si decisión/supersesión gana la carrera, se relee el caso y el comando reintenta contra el estado actual o devuelve `409`, nunca proyecta cancelación sobre un caso aún abierto. Un caso ya `COMPLETED` no impide cancelar `APPROVED`, porque esta spec no ha transferido ownership. Una spec downstream deberá modificar esta regla con una señal durable de takeover antes de bloquear cancelación. Cancelar no borra evaluaciones, casos, decisiones ni versiones. El requester lee su request e historia; `AUDITOR` organizacional lee snapshots, attestations y referencias minimizadas; `ADMIN` no obtiene lectura empresarial por el rol solo. Toda mutación/resultado/corrupción genera audit append-only con actor, UTC, motivo/código, versiones, digests y correlation, sin snapshots completos.

- **REQ-11 — API, errores y límites.** La API autenticada expone comandos idempotentes de crear request, crear revisión, presentar y cancelar, y lecturas de versión actual/historia según REQ-10; nunca acepta un `PolicyFactBundle`, manifest atestiguado, estado, evaluación o resultado Approval autocertificados. Usa Problem Details: `401` autenticación; `403` actor no permitido; `404` recurso no visible; `400` forma/JSON; `409` key, versión, carrera o estado; `413` más de 500 líneas, más de 256 respuestas de riesgo por línea o snapshot mayor de 5 MiB; `422` invariante/referencia inválida o Policy `BLOCKED`; y `503 /problems/purchase-request-dependency-unavailable` para owner/provider/Policy/Approval ausente, ambiguo, corrupto o indisponible. Summary admite 1–500 Unicode scalars, motivo 1–1.000 y keys 1–128 `[A-Za-z0-9._:-]`; límites se verifican antes de persistir el artefacto afectado.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `PurchaseRequest` | id, organización, secuencia actual, requester, estado proyectado, row version | Identidad estable; el estado es proyección de eventos, no contenido mutable. |
| `PurchaseRequestVersion` | request id/version, predecessor, organización, Legal Entity ref, requester, business justification, line refs, content digest, actor/UTC/key | Snapshot append-only; line refs forman set no vacío. |
| `PurchaseRequestLineVersion` | line id/version, request id, campos de REQ-03, content digest, actor/UTC | Append-only; una línea pertenece siempre a la misma request. |
| `PurchaseRequestRevisionDelta` | retained/changed/added/removed, predecessor y successor, key/fingerprint, motivo | Cobertura exacta de REQ-02; append-only. |
| `PurchaseRequestReferenceAttestation` | request/version, owner ids/versions, refs verificadas, relación Cost Center→Department, FX, evaluated UTC, digest | Snapshot de respuestas owner; no convierte PR en owner del catálogo. |
| `PurchaseRequestCompletenessManifest` | request/version/content digest, line count/set con content digests, reference attestation digest, policy manifest digest, domain attestation digest | Uno por versión presentada; persistido antes del provider y nunca mutable. |
| `PurchaseRequestSubmissionAttempt` | request/version, key/fingerprint, estado, manifest, policy bundle/result, approval case/version, previous case, error code, timestamps | Estados `PENDING|POLICY_CONFIRMED|APPROVAL_CONFIRMED|BLOCKED|DEPENDENCY_FAILED`; retries avanzan la misma fila. |
| `PurchaseRequestApprovalResult` | event/version, case, source, target line/version/digest, resultado, decision nullable, UTC | Inbox append-only y deduplicado; solo eventos verificados actualizan proyección. |
| `PurchaseRequestLifecycleEvent` | request/current version, before/after, actor union, motivo/código, refs causales, UTC, correlation | Append-only; permite reconstruir el estado agregado. |

### Payloads de revisión y referencias

Los objetos contractuales rechazan propiedades desconocidas. `VersionedEntityRef` tiene exactamente `{entity_type,id,version}`; `VersionedCodeRef`, `{catalog,code,digest,version}`; y `TypedAnswerRef`, `{question_code,schema_version,value,value_kind}` con `value_kind=BOOLEAN|ENUM_CODE`. Sus campos son no nulos salvo que la propiedad contenedora se declare nullable.

`purchase-request-line-content/v1` tiene exactamente `{base_amount,base_currency,beneficiary_department_ref,contract_required,cost_center_department_ref,cost_center_ref,estimated_gross_amount,fiscal_year,fx_attestation_ref,need_summary,non_standard_terms,preferred_product_ref,purchase_type,requested_for_user_ref,required_product_ref,risk_answers,spend_category_ref,supplier_ref,transaction_currency}`. Solo `fx_attestation_ref`, `preferred_product_ref`, `required_product_ref` y `supplier_ref` son nullable; `risk_answers` es set posiblemente vacío. Las refs usan los objetos anteriores. `base_amount` y `estimated_gross_amount` son strings decimales canónicos.

- `purchase-request-create-command/v1` contiene exactamente `{business_justification,command_version,legal_entity_ref,line_drafts,reason,revision_key}`; cada draft es `{client_line_key,content}` y `content` es el objeto exacto anterior. `client_line_key` es única en el comando y permite recuperar los UUID server-side en replay.
- `purchase-request-revision-command/v1` contiene exactamente `{business_justification,command_version,expected_request_version,reason,revision_key,retained,changed,added,removed}`. `retained` y `removed` contienen refs exactas `{content_digest,id,version}`; `changed` contiene `{content,expected_content_digest,expected_version,id}`; `added`, `{client_line_key,content}`. Los cuatro arrays son sets ordenados por bytes canónicos.
- `PurchaseRequestRevisionDelta` persistido contiene exactamente `{added,changed,contract_version,from_request_version,removed,retained,to_request_version}`. `retained[{reference}]`, `removed[{previous}]`, `changed[{previous,replacement}]` y `added[{client_line_key,replacement}]`; previous/replacement son refs `{content_digest,id,version}`. Cada array es set canónico y las reglas de REQ-02 fijan cardinalidad/nullabilidad.
- El successor `business_justification` viaja completo en create/revision. Organización, requester, request id y Legal Entity nunca se aceptan en revision; se leen de la raíz y deben coincidir al construir su content digest.

La request y todas sus líneas pertenecen a una sola organización y Legal Entity. Cada línea tiene un Cost Center, Fiscal Year, Spend Category, Beneficiary Department y Requested For; Cost Center→owner Department proviene del owner. Si monedas coinciden, FX ref es nula y tasa efectiva 1; si difieren, una attestation versionada es obligatoria. `risk_answers` es único por question/schema.

### Contrato de attestation owner

`AttestedRef` tiene propiedades exactas `{code,digest,id,kind,type,version}`, presentes incluso como `null`. `kind=ENTITY` exige UUID/version y code/digest nulos; `CODE|QUESTION_SCHEMA` exige code/version/digest y UUID nulo. `type` identifica `LEGAL_ENTITY|USER|COST_CENTER|DEPARTMENT|SPEND_CATEGORY|PRODUCT|SUPPLIER|RISK_SCHEMA|FX`.

Cada resolver implementa `purchase-request-reference-verification/v1`: request exacto `{assertion_type,contract_version,organization_id,source_ref,target_ref,verified_at}` y response exacta `{active,assertion_type,contract_version,organization_id,owner_contract_version,owner_id,source_ref,status,target_ref,verified_at}`. `target_ref` está presente como `null` salvo para `COST_CENTER_OWNED_BY_DEPARTMENT`; el response debe ecoar request/instante y `status=ACTIVE|INACTIVE|NOT_FOUND`. Mismatch, campo extra o versión desconocida es dependencia inválida.

La attestation persiste un set `assertions`; cada elemento tiene exactamente `{assertion_type,owner_contract_version,owner_id,source_ref,status,target_ref}`. Cardinalidades por versión presentada:

- una `ACTIVE_IN_ORGANIZATION` para Legal Entity y requester; una por cada Requested For, Beneficiary Department y ref opcional distinta;
- una `ACTIVE_IN_ORGANIZATION` por Cost Center, Spend Category, Risk Schema y producto/proveedor declarado;
- exactamente una `COST_CENTER_OWNED_BY_DEPARTMENT` por par Cost Center/owner Department de cada línea, además de actividad del Department;
- cero assertions FX si transaction/base currency coinciden; exactamente una `FX_ATTESTATION_VALID` por ref distinta si difieren.

Assertions idénticas entre líneas se deduplican; faltar una cardinalidad, recibir dos owners para un type, o recibir source/target/status distinto de lo solicitado impide confirmar el manifest.

### Proyección exacta a Policy

Cada línea proyecta estas keys: `GROSS_AMOUNT_BASE` (Money), `PURCHASE_TYPE` (Code), `SPEND_CATEGORY` (VersionedCodeRef), `BENEFICIARY_DEPARTMENT`, `COST_CENTER`, `COST_CENTER_DEPARTMENT`, `SUPPLIER`, `PREFERRED_PRODUCT`, `REQUIRED_PRODUCT` (VersionedEntityRef cuando no son null), `COST_CENTER_ACTIVE`, `COST_CENTER_DEPARTMENT_ACTIVE`, `PREFERRED_SUPPLIER`, `CONTRACT_REQUIRED`, `NON_STANDARD_TERMS` (Boolean), `EXTERNAL_AGREEMENT_STATUS` (Code), `DATA_RISK` (VersionedCodeRef cuando aplica) y `RISK_ANSWER`. Esta última key se omite si no hay respuestas; en otro caso es un `Set` no vacío de `TypedAnswerRef` único por `(question_code,schema_version)`. La request proyecta solo facts request-level definidos por SPEC 02; en v1 es `{}` y el motor calcula el total desde todas las líneas.

Cada `PolicyValue` canónico contiene exactamente `{catalog,currency,digest,entity_id,kind,lower_bound,lower_inclusive,members,question_code,reference_type,schema_version,upper_bound,upper_inclusive,value,value_kind,version}`, con todas las propiedades presentes y nulas según kind, conforme al serializador publicado de SPEC 02. En el bundle y en `purchase-request-materiality/v1`, `facts` es exactamente `{line,request}` y cada objeto es un mapa key→`PolicyValue`; `provenance` es un mapa exacto key→`pr://<request-id>/versions/<request-version>/lines/<line-id>/versions/<line-version>#<field>|attestations/<digest>#<assertion-type>`.

Para predicates `RISK_ANSWER`, `EQ|NEQ` exige un `TypedAnswerRef`; `IN|NOT_IN`, un Set no vacío cuyos miembros comparten question/schema/value kind. El matcher valida que el fact real sea un Set de TypedAnswerRef sin duplicar question/schema, extrae el único miembro de la identidad pedida y compara value kind/value. Cero miembros con esa identidad es `no-match` incluso para `NEQ|NOT_IN`; más de uno o un member de otro kind hace inválido el bundle (`400`) antes de persistir evaluación. Uno igual satisface `EQ|IN`; uno distinto satisface `NEQ|NOT_IN`. Se prueban ausencia, una coincidencia, múltiples preguntas válidas, duplicate question/schema y set/predicate mal tipados.

### Canonicalización y attestation

Todos los digests de este dominio usan `policy-canonical-json/v1` y SHA-256 hexadecimal minúsculo. Aplican UTF-8 sin BOM/whitespace, propiedades conocidas presentes y orden ordinal, strings NFC, UUID `D` minúsculo, timestamps UTC con siete decimales, decimales como strings invariantes y sets ordenados por bytes canónicos sin duplicados. Cambiar propiedades, orden o preimage exige otra `canonicalization_version`; cambiar solo el schema documental exige subir su `contract_version`.

| Digest | Propiedades exactas del preimage |
| --- | --- |
| `line_content_digest` (`purchase-request-line-version/v1`) | `base_amount`, `base_currency`, `beneficiary_department_ref`, `canonicalization_version`, `contract_required`, `contract_version`, `cost_center_department_ref`, `cost_center_ref`, `estimated_gross_amount`, `fiscal_year`, `fx_attestation_ref`, `line_id`, `line_version`, `need_summary`, `non_standard_terms`, `organization_id`, `preferred_product_ref`, `purchase_request_id`, `purchase_type`, `requested_for_user_ref`, `required_product_ref`, `risk_answers`, `spend_category_ref`, `supplier_ref`, `transaction_currency`; refs/content usan los schemas exactos anteriores. |
| `request_content_digest` (`purchase-request-version/v1`) | `business_justification`, `canonicalization_version`, `contract_version`, `legal_entity_ref`, `line_refs`, `organization_id`, `predecessor_version`, `request_id`, `request_version`, `requester_id`; cada line ref contiene exactamente `content_digest`, `id`, `version`. |
| `create_fingerprint` | `actor_user_id`, `business_justification`, `canonicalization_version`, `command_version`, `legal_entity_ref`, `line_drafts`, `organization_id`, `reason`, `revision_key`; drafts/content usan los schemas exactos anteriores. |
| `revision_fingerprint` | `actor_user_id`, `business_justification`, `canonicalization_version`, `changed`, `command_version`, `expected_request_version`, `organization_id`, `reason`, `removed`, `request_id`, `retained`, `revision_key`, `added`; arrays/elementos usan `purchase-request-revision-command/v1`. |
| `reference_attestation_digest` (`purchase-request-reference-attestation/v1`) | `assertions`, `attested_at`, `canonicalization_version`, `contract_version`, `organization_id`, `request_id`, `request_version`; assertions usan el schema exacto anterior. |
| `policy_manifest_digest` | Preimage exacto ya implementado por SPEC 02: `canonicalization_version`, `line_count`, `lines[{id,version}]`, `request_id`, `request_version`; no incluye el digest dentro de sí. |
| `domain_attestation_digest` (`purchase-request-completeness-manifest/v1`) | `canonicalization_version`, `contract_version`, `line_count`, `lines[{content_digest,id,version}]`, `organization_id`, `policy_manifest_digest`, `provider_contract_version`, `provider_id`, `reference_attestation_digest`, `request_content_digest`, `request_id`, `request_version`. |
| `submission_fingerprint` (`purchase-request-submission/v1`) | `actor_user_id`, `canonicalization_version`, `contract_version`, `domain_attestation_digest`, `organization_id`, `policy_manifest_digest`, `reason`, `request_content_digest`, `request_id`, `request_version`, `submission_key`. |
| `purchase_request_materiality_digest` (`purchase-request-materiality/v1`) | `base_currency`, `canonicalization_version`, `contract_version`, `facts`, `legal_entity_id`, `organization_id`, `provenance`, `target_id`; `facts`, cada `PolicyValue` y provenance tienen la forma exacta de Proyección a Policy y excluyen texto, ids/versiones de snapshot y manifest global. |

El provider entrega a SPEC 02 el `policy_manifest_digest`, no sustituye su preimage. Antes comprueba además `domain_attestation_digest`; así Policy conserva `policy-canonical-json/v1` y la prueba más rica queda bajo ownership de Purchase Requests.

`approval-supersession-delta/v1` usa `approval-canonical-json/v3` porque amplía el preimage de supersesión implementado. `supersession_fingerprint` contiene exactamente `{canonicalization_version,new_submission_fingerprint,owner_workload_client_id,owner_workload_issuer,previous_case_id,previous_case_version,supersession_key,target_delta}`. Cada entrada de `target_delta` contiene exactamente `{change_kind,materiality_digest,materiality_schema_version,previous,replacement}`; previous/replacement usan el target exacto de SPEC 03 y están presentes como `null` según REQ-08. El v2 histórico permanece verificable y no se reetiqueta.

Vectores mínimos contractuales (bytes UTF-8 completos de cada bloque, sin salto final):

```json
{"assertions":[{"assertion_type":"ACTIVE_IN_ORGANIZATION","owner_contract_version":"organization-db/v1","owner_id":"organization-domain","source_ref":{"code":null,"digest":null,"id":"44444444-4444-4444-4444-444444444444","kind":"ENTITY","type":"LEGAL_ENTITY","version":1},"status":"ACTIVE","target_ref":null}],"attested_at":"2026-09-13T12:00:00.0000000Z","canonicalization_version":"policy-canonical-json/v1","contract_version":"purchase-request-reference-attestation/v1","organization_id":"11111111-1111-1111-1111-111111111111","request_id":"22222222-2222-2222-2222-222222222222","request_version":1}
```

SHA-256 `reference_attestation_digest`: `e00b6c3fb9488a27cee654c6c2633e00fc280ffd626dfb8ac29f063b0ad19194`.

```json
{"base_currency":"PEN","canonicalization_version":"policy-canonical-json/v1","contract_version":"purchase-request-materiality/v1","facts":{"line":{"GROSS_AMOUNT_BASE":{"catalog":null,"currency":"PEN","digest":null,"entity_id":null,"kind":"MONEY","lower_bound":null,"lower_inclusive":null,"members":null,"question_code":null,"reference_type":null,"schema_version":null,"upper_bound":null,"upper_inclusive":null,"value":"100","value_kind":null,"version":null}},"request":{}},"legal_entity_id":"44444444-4444-4444-4444-444444444444","organization_id":"11111111-1111-1111-1111-111111111111","provenance":{"GROSS_AMOUNT_BASE":"pr://22222222-2222-2222-2222-222222222222/versions/1/lines/33333333-3333-3333-3333-333333333333/versions/1#base_amount"},"target_id":"33333333-3333-3333-3333-333333333333"}
```

SHA-256 `purchase_request_materiality_digest`: `2a5dcf855c7ec1c9f18f51486a0ed6c26f9e4c4d0c4a334c62eb6968ac7c31f9`.

```json
{"canonicalization_version":"approval-canonical-json/v3","new_submission_fingerprint":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","owner_workload_client_id":"purchase-request-domain","owner_workload_issuer":"internal://procure-to-pay","previous_case_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","previous_case_version":2,"supersession_key":"pr-222-v2","target_delta":[{"change_kind":"ADDED","materiality_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","materiality_schema_version":"purchase-request-materiality/v1","previous":null,"replacement":{"id":"33333333-3333-3333-3333-333333333334","material_snapshot_digest":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","type":"PURCHASE_REQUEST_LINE","version":1}},{"change_kind":"REMOVED","materiality_digest":"1111111111111111111111111111111111111111111111111111111111111111","materiality_schema_version":"purchase-request-materiality/v1","previous":{"id":"33333333-3333-3333-3333-333333333335","material_snapshot_digest":"2222222222222222222222222222222222222222222222222222222222222222","type":"PURCHASE_REQUEST_LINE","version":1},"replacement":null},{"change_kind":"RETAINED","materiality_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","materiality_schema_version":"purchase-request-materiality/v1","previous":{"id":"33333333-3333-3333-3333-333333333333","material_snapshot_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","type":"PURCHASE_REQUEST_LINE","version":1},"replacement":{"id":"33333333-3333-3333-3333-333333333333","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST_LINE","version":2}}]}
```

SHA-256 `supersession_fingerprint`: `6016e088b85c6a2ebaf4129461551284133f0872810a8771fdcc79fcaafc4470`.

Los tests golden fijan además bytes y SHA-256 de line/request content, create/revision, policy/domain manifest y submit. Cada suite usa fixtures independientes de las funciones de producción; los vectores anteriores son obligatorios y no sustituyen los casos válidos de cardinalidad completa.

## Impacto sobre especificaciones anteriores

| Contrato anterior | Regla nueva y alcance |
| --- | --- |
| SPEC 02 REQ-04/REQ-05 y matcher | El fact real `RISK_ANSWER` es ausente o Set canónico de `TypedAnswerRef`; predicates `EQ|NEQ` usan uno e `IN|NOT_IN` un Set homogéneo. El matcher selecciona por question/schema y aplica la tabla de Proyección exacta a Policy. La forma publicada de `PolicyValue` ya reserva `members`, por lo que `policy-canonical-json/v1` no cambia; ausencia no satisface operadores negativos y duplicidad/mal tipo invalida el bundle. |
| SPEC 03 REQ-01/REQ-05 y `ValidateActors` | `policy-approval-adapter/v2` declara requester obligatorio y permite que requester=originator para `SUBMIT_PURCHASE_REQUEST`; se deduplica la exclusión. Los adapters v1 y cualquier operación que declare actores distintos conservan su validación. El preimage v2 no cambia. |
| SPEC 04 REQ-04 | Para adapter v2, mapping biyectivo de igual cardinalidad se reemplaza por cobertura total mediante `RETAINED|ADDED|REMOVED`; adapters previos conservan mapping v2 de cardinalidad idéntica. |
| SPEC 04 REQ-05 | En retained de PR se compara stable line id + `purchase-request-materiality/v1` y contrato completo; no se exige igualdad de target version/digest cuando el materiality proof coincide. Added nunca hace carry-forward y removed nunca reaparece. |
| SPEC 04 `supersession_fingerprint` | La nueva forma usa `approval-canonical-json/v3` y `approval-supersession-delta/v1`; filas/digests v2 históricos permanecen inmutables y verificables. |
| SPEC 05 REQ-03 y adapter v1 | `policy-approval-adapter/v2` agrega `REQUEST_CHANGES`, requester obligatorio, materiality proof y construcción del delta de supersesión. Mapping de controles, owners, fases, targets Policy y verifier de waiver no cambian. |

La vigencia de estas sustituciones empieza al integrar SPEC 06. Policies, casos, submissions, mappings, decisiones y digests previos permanecen bajo sus versiones originales; no hay migración inferida ni dual-write contractual.

## Migración, despliegue y reversión

- La migración agrega un schema de Purchase Requests con versiones, líneas, deltas, attestations, manifests, attempts, lifecycle, inbox y constraints append-only; no crea FKs hacia tablas de owners futuros ni modifica contenido Policy/Approval histórico.
- El preflight exige SPEC 01–05 integradas, una sola registration del provider real, consumers `approval-result/v2|v3` y lifecycle activos, y soporte Approval de adapter v2/canonicalización v3. Primero se despliegan schema y consumers, después soporte de supersession v3, luego provider/adapter v2 y por último las APIs de presentación.
- Owners externos pueden no estar desplegados: health indica exactamente qué registro falta/está ambiguo y submit falla `503`; nunca se registran catálogos ficticios ni se degrada a facts incompletos.
- No se migran providers de test ni se fabrican manifests para evaluaciones históricas. Versiones creadas antes de completar una attestation deben presentarse/reintentarse con owners reales; corrupción obliga a corregir hacia una nueva versión, no a editar la anterior.
- Rollback deshabilita nuevos create/submit y mantiene readers, inbox y consumers compatibles con v2/v3. Tras crear supersession v3 no se vuelve a una versión incapaz de leerla; se corrige hacia adelante. Datos y eventos no se borran.
- Antes de tráfico se ejecuta un recorrido real: requester → versión/manifest → provider real → Policy → adapter v2 → Approval → resultado outbox → proyección PR, más revisión con retained/added/removed y recovery tras fallo entre módulos.

## Seguridad y privacidad

- Crear, revisar, presentar y cancelar exige usuario activo y organización derivada del contexto autenticado; ids de organización/requester del payload no sustituyen esa fuente.
- Requester/originator queda excluido de toda aprobación; `ADMIN`/`AUDITOR` no deciden ni presentan por otra persona. No existe bypass de Policy, owners, presupuesto o Approval.
- El provider y orquestador son in-process; sus workload identities son `issuer + client_id` allowlisted y estables ante rotación de credenciales. No se expone una API de facts/manifest ni se confía en attestations del caller.
- Business justification, need summary, motivos, user ids y respuestas de riesgo pueden ser sensibles. Policy recibe solo facts allowlisted; logs, métricas, trazas, health, Problem Details y eventos no incluyen textos, respuestas, tokens, snapshots completos, digests de attestation completos ni PII innecesaria.
- Lecturas fuera de scope responden `404`; corrupción o dependencia no revela si otra organización posee el id.

## Requisitos no funcionales

- **NFR-01 — Inmutabilidad reconstruible.** Versiones, líneas, manifests, attestations, deltas, resultados y lifecycle son append-only; el estado actual se reconstruye desde historia y no depende de sobrescribir evidencia.
- **NFR-02 — Determinismo contractual.** Mismos snapshots/attestations producen los mismos bytes, manifests, facts, materiality y digests; permutar sets no cambia el resultado y cambiar un campo material sí.
- **NFR-03 — Exactly-once lógico.** Revisar, presentar, superseder, cancelar y consumir eventos usan keys, fingerprints, constraints y versión esperada para producir un solo efecto lógico bajo retries, timeout y dos instancias.
- **NFR-04 — Disponibilidad segura.** Owner, provider, Policy, Approval o consumer ausente/ambiguo/corrupto bloquea el avance; health distingue backlog, attestation inválida y etapa del attempt sin revelar datos.
- **NFR-05 — Observabilidad minimizada.** Métricas, logs y trazas correlacionan versión, etapa, resultado, retry y duración, sin contenido de línea, user ids, respuestas de riesgo ni snapshots.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | State/delta, inmutabilidad, validaciones de línea, agregación, preimages/goldens, provider mapping, materiality y errores | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | SQL Server, constraints append-only, concurrencia, attestations exact-one, attempts/retry, inbox, proyecciones y migración | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Testcontainers |
| API/E2E | JWT usuario, visibilidad, idempotencia, Problem Details/límites, submit/cancel y recuperación | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` con Docker |
| Contrato cross-module | Provider real y recorrido PR→Policy→Approval→PR, más adapter v1/v2 y supersession v2/v3 | Suite API/E2E sobre SQL Server efímero, sin fact provider ni Approval doubles |
| Operación | Deploy por fases, owner ausente/ambiguo, crash entre etapas, replay/dead letter, health y rollback compatible | Pruebas automatizadas y `docs/purchase-request-operations.md` |

Todos los criterios son automáticos. Los tests negativos cubren manifest omitido/duplicado/adulterado, línea de otra versión/organización, owner ausente/ambiguo/inactivo, provider duplicado, Policy `BLOCKED`, Approval ownerless, requester aprobador, revision/submit key conflictiva, delta incompleto, event tardío/duplicado y fallo después de persistir Policy o Approval.

## Decisiones

- **DEC-01 — Revisiones siempre inmutables.** Cada cambio aceptado crea snapshots nuevos y reutiliza líneas intactas; se descarta un draft editable en sitio porque impediría reproducir manifest, policy input e historia.
- **DEC-02 — Manifest emitido por el owner.** Purchase Requests persiste y atestigua completitud; Policy lo recalcula y verifica. Se descarta que el caller seleccione líneas o envíe totals/facts.
- **DEC-03 — Provider real in-process.** La integración se realiza por el puerto existente de SPEC 02, no por HTTP ni mediante un provider de test promovido a producción.
- **DEC-04 — Referencias externas fail-closed.** Esta spec no adelanta Cost Centers, Spend Categories, riesgo, productos o FX; exige owners exact-one y conserva sus attestations versionadas.
- **DEC-05 — Presentación completa.** Submit termina en una evaluación y un caso Approval confirmados, o en fallo/bloqueo explícito recuperable; se descarta dejar un bundle sin owner de proceso.
- **DEC-06 — Delta con altas y bajas.** Una revisión puede cambiar membership y conserva cobertura explícita de ambos manifests; se descarta obligar a cancelar y crear otra request por cada alta/baja.
- **DEC-07 — Carry-forward por materialidad, no por número de versión.** Una edición descriptiva puede conservar evidencia si facts y contrato completo coinciden; cualquier hecho material, grupo, requisito o incertidumbre exige decisión nueva.
- **DEC-08 — Estado derivado por línea.** Approval emite resultados y Purchase Requests proyecta su estado; ninguno muta las tablas del otro ni infiere aprobación de ausencia de eventos.

## Plan de implementación

### Bloque 1 — Versiones, líneas y contratos

- **T-01 — Modelo y persistencia inmutable.** Implementar request/version/line/delta/lifecycle, invariantes, ids/versiones, keys/fingerprints, auditoría y migración SQL append-only. Cubre: REQ-01, REQ-02, REQ-03, REQ-10, REQ-11, NFR-01, NFR-03, CA-01, CA-02, CA-08.
- **T-02 — API segura de borrador/revisión.** Implementar create, revision, cancel y reads con actor derivado, visibilidad, límites, concurrencia y Problem Details. Cubre: REQ-01, REQ-02, REQ-03, REQ-10, REQ-11, NFR-03, NFR-05, CA-01, CA-02, CA-08.

**Resultado verificable:** dos instancias y retries producen una sola cadena inmutable, con deltas completos y sin modificar una fila histórica.

### Bloque 2 — Attestation y provider Policy

- **T-03 — Referencias y manifest.** Resolver owners exact-one, persistir attestations/manifests, implementar canonicalización/goldens y casos de corrupción/timeout. Cubre: REQ-03, REQ-04, REQ-11, NFR-02, NFR-04, CA-03, CA-07.
- **T-04 — Provider real.** Implementar/registrar `PurchaseRequestPolicyFactProvider`, mapping/provenance, set/matching de risk answers, verificación de digests y pruebas bidireccionales con SPEC 02. Cubre: REQ-04, REQ-05, REQ-11, NFR-02, NFR-04, CA-03, CA-04, CA-07.

**Resultado verificable:** Policy evalúa todas y solo las líneas de una versión persistida mediante el provider real; cualquier omisión, mezcla o attestation inválida falla cerrado.

### Bloque 3 — Presentación y evolución Approval

- **T-05 — Orquestador idempotente.** Implementar attempts, keys internas, Policy→Approval, retries tras fallos parciales y estados de submit sin transacción distribuida. Cubre: REQ-06, REQ-11, NFR-03, NFR-04, CA-04, CA-05, CA-08.
- **T-06 — Adapter v2 y supersession v3.** Implementar requester=originator acotado, `REQUEST_CHANGES`, materiality, delta retained/added/removed, canonicalización/golden y compatibilidad histórica v1/v2. Cubre: REQ-07, REQ-08, NFR-01, NFR-02, NFR-03, CA-05, CA-06, CA-07.
- **T-07 — Inbox y proyección.** Consumir resultados/lifecycle, verificar bindings, deduplicar y derivar estados por línea/request; cubrir eventos tardíos y carreras con cancel/revision. Cubre: REQ-07, REQ-09, REQ-10, NFR-01, NFR-03, NFR-05, CA-06, CA-08.

**Resultado verificable:** el E2E real crea/supersede un caso, solicita cambios, revisa con líneas retained/added/removed y proyecta el resultado sin duplicados ni carry-forward inseguro.

### Bloque 4 — Operación y evidencia completa

- **T-08 — Operación y regresión.** Añadir health, telemetría minimizada, despliegue/reversión, fault injection y regresión completa de SPEC 01–05. Cubre: NFR-03, NFR-04, NFR-05, CA-08.

**Resultado verificable:** suites y runbook demuestran recovery en cada frontera, compatibilidad histórica y bloqueo seguro ante toda dependencia ausente o ambigua.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, NFR-01, NFR-03 | Create/revision concurrente produce una sola sucesora; retained reutiliza ref exacta, changed avanza una, added inicia en 1 y removed no aparece. Ninguna operación modifica/borra versiones previas y delta incompleto/solapado da `409/422`. | Automática: dominio y SQL multi-DbContext con replay/fallo forzado. |
| CA-02 | REQ-03, REQ-10, REQ-11 | Solo requester activo crea/revisa/cancela y ve su historia; `AUDITOR` lee scope y `ADMIN` no amplía acceso. Campos, incompatibilidades, límites ±1, monedas/importes y estados terminales producen el Problem Details previsto sin filtrar datos. | Automática: unitarias y API/E2E JWT/visibilidad/límites. |
| CA-03 | REQ-04, NFR-02, NFR-04 | Owners exact-one válidos producen una attestation y manifest cuyos sets/digests se reproducen; owner ausente/ambiguo/timeout da `503`, ref inactiva/relación falsa da `422` y no existe manifest parcialmente confirmado. | Automática: adapters controlados, SQL transaccional y goldens independientes. |
| CA-04 | REQ-05, REQ-06, NFR-02, NFR-03, NFR-04 | Provider real sirve solo request/version/manifest atestiguado y Policy evalúa todas las líneas sin facts/total del caller. Risk answers cubren ausencia, match único, múltiples preguntas, duplicidad y operadores `EQ|NEQ|IN|NOT_IN` fail-closed. Retry tras confirmar Policy reutiliza bundle; digest corrupto, provider duplicado o Policy `BLOCKED` no crea caso ni éxito. | Automática: integración y API/E2E PR→Policy con fault injection. |
| CA-05 | REQ-06, REQ-07, NFR-03 | Submit confirmado usa adapter v2, requester=originator queda excluido una vez y nunca decide; requirements ofrecen las tres acciones. Retry tras confirmar Approval recupera el mismo caso y una key con otro fingerprint da `409`. | Automática: contrato adapter, SQL concurrente y E2E sin doubles. |
| CA-06 | REQ-07, REQ-08, REQ-09, NFR-01, NFR-03 | `REQUEST_CHANGES` exige una nueva versión/caso. Delta retained/added/removed cubre ambos manifests: added no carry, removed no reaparece y retained solo carry-forward con materiality/contrato/set idénticos. Eventos v2/v3/lifecycle duplicados o tardíos no alteran indebidamente la versión actual. | Automática: matriz supersession/carry-forward, inbox SQL y E2E de revisión multilínea. |
| CA-07 | REQ-04, REQ-05, REQ-08, NFR-02 | Goldens independientes fijan bytes y SHA-256 de content, revision, attestations, manifests, submit, materiality y supersession v3; permutar sets no cambia hash, alterar un fact/ref/membership sí. Artefactos v2 históricos conservan sus hashes. | Automática: fixtures independientes y regresión de vectores SPEC 02–05. |
| CA-08 | REQ-06, REQ-09, REQ-10, REQ-11, NFR-03, NFR-04, NFR-05 | El recorrido real llega a estado por línea/request correcto; cancel/revision/decisión concurrentes terminan una vez, crash entre módulos se recupera con el mismo attempt y health/telemetría muestran etapa/backlog sin texto, PII, tokens o snapshots. | Automática: API/E2E, dos instancias, restart, captura de logs/trazas y runbook. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Manifest autocertificado o incompleto | Caller selecciona líneas o total | Manifest owner persistido, provider por referencia y CA-03/CA-04. |
| Versión aparente mutable | UPDATE/DELETE sobre snapshots históricos | Constraints, permisos de persistencia y CA-01. |
| Catálogo futuro adelantado | PR administra Cost Center/Spend Category | Owners exact-one, refs/attestations y DEC-04. |
| Alta/baja omite un target anterior | Delta no cubre ambos manifests | Variantes cerradas, cobertura total y CA-06. |
| Carry-forward por UUID/version | Cambio material conserva decisión | Materiality + contrato completo + default nueva task en CA-06/CA-07. |
| Fallo entre Policy y Approval duplica o declara éxito | Dos bundles/casos o estado `IN_APPROVAL` sin caso | Attempt durable, keys deterministas y CA-04/CA-05/CA-08. |
| Evento tardío reabre versión vieja | Resultado de caso superseded cambia current | Binding a subject/version/case y CA-06. |
| Textos o riesgo llegan a logs/Policy | Snapshot completo en traza o bundle | Allowlist de facts, captura de telemetría y CA-02/CA-08. |
