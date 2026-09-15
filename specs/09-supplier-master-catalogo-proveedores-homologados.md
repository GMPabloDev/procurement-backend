# SPEC 09 — Supplier Master y catálogo de proveedores homologados

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** No iniciada
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** 338d7c40ad2d45979e5b053006f3a2f2283f0e9bfc0faa5bd1f0e144754f7faa
> **Fecha:** 2026-09-15
> **Actualizada:** 2026-09-15
> **Aprobada el:** 2026-09-15
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Proveer proveedores versionados, gobernados y verificables, junto con homologaciones y acuerdos externos reproducibles, para que Purchase Requests y Policy dejen de usar facts sintéticos y las futuras Sourcing/PO puedan fallar cerrado ante proveedores no elegibles.
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04
> **Modifica:** SPEC 05, SPEC 06, SPEC 07, SPEC 08
> **Reemplaza:** Ninguna

## Contexto

Purchase Requests ya admite `supplier_ref` nullable y exige un owner `ACTIVE_IN_ORGANIZATION/SUPPLIER` cuando la referencia está presente, pero producción no registra ese owner. Además, `PurchaseRequestPolicyProjection` fija siempre `PREFERRED_SUPPLIER=false` y `EXTERNAL_AGREEMENT_STATUS=NONE`, aunque Policy ya tipa ambos facts y el adapter de Approval ya conoce `active-supplier-owner/v1`. No existe Supplier Master, catálogo Policy `SUPPLIER`, processor del prerequisite ni Approved Supplier Catalog. Por ello una request con proveedor declarado falla cerrada, y una sin proveedor solo puede evaluar hechos no informativos.

## Alcance

### Incluye

- Supplier Master organizacional con identidad fiscal única, contenido versionado, borradores, aprobación, estados operativos e historia append-only.
- Datos de identidad, dirección, contacto, pago, monedas, categorías, riesgo y performance manual, más Banking Details cifrados, enmascarados y con lectura explícita auditada.
- Aprobación de activación, suspensión, bloqueo, reactivación y cambios sensibles mediante Approval Workflow, `PROCUREMENT_APPROVER`, authority `SUPPLIER_MASTER` y segregación respecto del editor.
- Approved Supplier Catalog por Spend Category y Product ref opcional, con precio negociado, moneda, unidad, acuerdo externo, vigencia, estado y attachment.
- Owner exact-one de `ACTIVE_IN_ORGANIZATION/SUPPLIER`, catálogo Policy `SUPPLIER`, snapshot de homologación para Purchase Requests y facts reales `PREFERRED_SUPPLIER`/`EXTERNAL_AGREEMENT_STATUS`.
- Processor real de `active-supplier-owner/v1` y evolución compatible del adapter Policy→Approval cuando un control combinado contiene proveedores distintos.
- Lecturas mínimas para construir Purchase Requests, lecturas operativas por rol, auditoría, health, migración, rollback y `docs/supplier-operations.md`.

### No incluye

- RFQ, sourcing, quotations, award ni quotation waiver; el catálogo publica facts y Policy decide si exige o reduce cotizaciones.
- Purchase Orders, amendments o la integración que volverá a comprobar elegibilidad justo antes de crear/emitir una PO.
- Fulfillment, Supplier Invoices, matching, preparación de pagos o ejecución bancaria.
- Contract Lifecycle Management: no se administran cláusulas, negociación, firma, obligaciones ni renovaciones del acuerdo externo.
- Supplier Portal, onboarding autoservicio, importación/sincronización ERP, notificaciones, analytics ni multi-organización.
- Cálculo automático de risk status o performance score; esta entrega solo conserva valores administrados y su provenance.
- Product Master. Una entrada acotada a Product solo es publicable si el owner opcional de `PRODUCT` ya puede verificar esa referencia; las entradas por categoría funcionan sin él.
- Elegir proveedor, recomendar ofertas o autorizar una PO. `BLOCKED` y cualquier estado distinto de `ACTIVE` se publican como no elegibles, pero la futura SPEC de PO será responsable de consumir esa decisión en su frontera transaccional.

## Comportamiento esperado

- **REQ-01 — Raíz, versiones e identidad fiscal.** Cada `Supplier` pertenece a la única organización, tiene UUID estable y reserva una identidad fiscal por `(country_code, tax_id_key)`. `country_code` es ISO 3166-1 alpha-2 mayúsculo y `tax_id_key` es el Tax ID en NFC, trim y uppercase invariant; no se realiza validación tributaria externa. La pareja es única entre raíces y cualquier identidad fiscal que haya sido aprobada permanece reservada por esa raíz. La raíz mantiene como máximo un puntero operacional aprobado y un puntero de trabajo `DRAFT|PENDING_APPROVAL`; ambos apuntan a versiones append-only existentes y nunca retroceden. Crear produce versión 1 `DRAFT`; cada edición o transición produce una única sucesora, exige motivo, key idempotente y `expected_version`, y una carrera confirma una sola versión o devuelve `409`. No hay update/delete de versiones, aprobaciones, identities fiscales históricas ni audit.

- **REQ-02 — Datos completos y clasificación de cambios.** Una versión conserva legal name, trade name nullable, Country/Tax ID, cero o más direcciones, uno o más contactos, payment terms, monedas soportadas, Spend Categories suministradas, estado operativo, risk status, performance score nullable y refs de Banking Details. Legal name, Country/Tax ID, payment terms, monedas, categorías, risk status, performance score y Banking Details son campos sensibles de gobierno; cambiar cualquiera exige aprobación. Trade name, direcciones y contactos son cambios no sensibles de gobierno: `PROCUREMENT_BUYER` puede aplicar una sucesora con el mismo estado operativo sin Approval, pero siempre con concurrencia y audit. El servidor calcula la clasificación desde el diff persistido; el caller no puede declarar un cambio como no sensible. La activación inicial y todo cambio de estado son siempre sensibles.

- **REQ-03 — Ciclo, Approval y segregación.** `PROCUREMENT_BUYER` con assignment `ORGANIZATION` crea/edita un draft y presenta un cambio sensible. La presentación congela el candidate, lo lleva a `PENDING_APPROVAL` y crea o recupera un caso mediante `supplier-governance-approval-adapter/v1`, con un único requirement `PROCUREMENT_APPROVER + SUPPLIER_MASTER`, scope `ORGANIZATION`, acciones `APPROVE|REJECT|REQUEST_CHANGES` y editor/originator excluido. `APPROVE` mueve atómicamente el puntero operacional al candidate y materializa `ACTIVE`, `SUSPENDED` o `BLOCKED`; `REJECT|REQUEST_CHANGES|CANCELLED` conserva la versión operacional anterior y devuelve el candidate a un nuevo draft revisable, sin reescribir la decisión. La secuencia empresarial es `DRAFT → PENDING_APPROVAL → ACTIVE → SUSPENDED|BLOCKED`; desde `SUSPENDED` o `BLOCKED` se permite `PENDING_APPROVAL → ACTIVE` mediante otra aprobación. No hay salto directo, autoaprobación, aprobación por role sin un único grant vigente, ni bypass de `ADMIN`. Un usuario que posea ambos roles puede editar o aprobar cambios distintos, pero nunca el mismo proposal.

- **REQ-04 — Validación del contenido.** Legal/trade name admiten respectivamente 1–300 y 0–300 Unicode scalars; Tax ID, 1–64; hay 0–20 direcciones y 1–20 contactos. Cada contacto tiene nombre y al menos email o teléfono; cada dirección y contacto tiene UUID estable para distinguir revisiones. Payment terms contiene código 1–64 y `net_days` 0–365. Supported currencies es un set no vacío de ISO 4217; categories supplied, un set no vacío de `SPEND_CATEGORY` refs current activas de SPEC 07. `risk_status=UNASSESSED|LOW|MEDIUM|HIGH|CRITICAL`; `performance_score` es null o decimal 0–100 con hasta cuatro decimales y `performance_source`/`measured_at` son obligatorios cuando hay score. Performance no se presenta como calculado por el sistema. Referencia inexistente, stale, inactiva, duplicada o de otra organización falla `422`; catálogo/owner 0/2, timeout o respuesta inválida falla `503`, sin versión ni audit de éxito parcial.

- **REQ-05 — Banking Details cifrados y lectura mínima.** Cada cuenta bancaria es una raíz estable con versiones append-only y contiene account holder, bank name/country, currency, account number local o IBAN, SWIFT/BIC nullable, account type y default por currency; debe existir al menos account number o IBAN, y hay como máximo un default current por moneda. El plaintext canónico `supplier-banking-details/v1` se cifra con AEAD y key version de un provider externo a SQL; las tablas generales solo conservan ref, ciphertext, nonce/tag, suffix enmascarado y metadata no secreta. El plaintext nunca forma parte de digests públicos, audit genérico, eventos, logs, trazas, métricas, health, Problem Details ni URLs.
  Las lecturas de master autorizadas a Procurement, AP y Auditor muestran solo banco, moneda, tipo y últimos cuatro caracteres; `ADMIN` carece de esa lectura empresarial. Una operación separada de reveal revela la versión operacional current solo a `AP_SPECIALIST` con scope `ORGANIZATION`, o la versión pending únicamente al `PROCUREMENT_APPROVER` que tenga la task activa exacta; cada reveal crea audit append-only con actor, propósito tipado, supplier/banking version y UTC, sin copiar el valor. `AUDITOR`, `PROCUREMENT_BUYER`, otro approver y cualquier lectura no-reveal reciben solo máscara. Todo cambio bancario sigue REQ-03 y el approver debe ser distinto del editor. Key provider ausente, ambiguo o incapaz de autenticar ciphertext devuelve `503`, no degrada a plaintext ni confirma cambios.

- **REQ-06 — Approved Supplier Catalog versionado.** Una `ApprovedSupplierCatalogEntry` pertenece a la organización y su selector exacto es `(supplier_id, spend_category_code, product_id nullable)`. Cada versión contiene Supplier ref, Spend Category ref, Product ref nullable, negotiated unit price decimal positivo, moneda ISO 4217, unit code, external contract reference, `valid_from` inclusivo, `valid_to` exclusivo, estado `ACTIVE|INACTIVE`, un attachment inmutable y predecessor. Supplier y categoría deben ser current `ACTIVE`; si Product no es null, su owner exact-one debe confirmar current activo. Para un mismo selector no existen dos versiones `ACTIVE` con intervalos superpuestos; una entrada por Product y otra general de categoría sí pueden coexistir. Roots/versions/current pointer son append-only salvo el puntero; `expected_version`, key y motivo son obligatorios y carreras/replays conflictivos dan `409`.

- **REQ-07 — Gobierno, vigencia y attachment del catálogo.** `PROCUREMENT_BUYER` crea o revisa un draft; toda publicación, cambio de precio/acuerdo/vigencia/selector, desactivación o reactivación usa Approval según REQ-03 con operación `APPROVE_SUPPLIER_CATALOG_CHANGE`, target versionado y SoD. Una entrada `ACTIVE` es efectiva solo si `valid_from <= now < valid_to`; pasado `valid_to` se deriva `EXPIRED` sin mutar la versión. Una futura o explícitamente inactiva se deriva `INACTIVE`. Publicar exige attachment `supplier-agreement-attachment/v1` confirmado, 1 byte–10 MiB, PDF/PNG/JPEG, SHA-256 calculado server-side, object key opaca y metadata persistida; nombre aportado no decide path. Descarga usa URL temporal máxima de 15 minutos solo para Procurement y `AUDITOR` organizacional, queda auditada y nunca hace público el bucket. Fallo de upload/confirmación no deja una versión publicable; objetos huérfanos en staging son recuperables/limpiables sin borrar attachments referenciados.

- **REQ-08 — Selección reproducible y facts de Purchase Request.** Para cada línea, el lookup exact-one `approved-supplier-fact-owner/v1` recibe la versión persistida, el supplier ref nullable, Spend Category ref, Preferred/Required Product ref nullable y `attested_at` servidor; no recibe un boolean o agreement status del usuario. Sin supplier devuelve `preferred_supplier=false`, `external_agreement_status=NONE` y entry null. Con supplier, primero considera entries exactas del Product declarado; si ninguna aplica, considera el selector general de categoría. La restricción de solapamiento produce como máximo una entry efectiva: si existe, publica `true/ACTIVE`; si no, la versión current más específica y de mayor `valid_from` produce `false/INACTIVE|EXPIRED`, y ausencia produce `false/NONE`. Empates o datos corruptos fallan `503`, nunca eligen por orden de consulta. El documento exacto `supplier-policy-fact-snapshot/v1` congela refs y versiones consultadas, entry nullable, estado derivado, instante y `catalog_content_digest` nullable; ese único digest compromete precio, acuerdo, vigencia, selector y attachment de la versión según la preimage publicada, pero no se expone como fact Policy ni en la API ordinaria. El snapshot no contiene Banking, contactos ni Tax ID.

- **REQ-09 — Owners reales, provider v2 y compatibilidad.** Producción registra exactamente un owner `supplier-domain/supplier-db/v1` para `(ACTIVE_IN_ORGANIZATION,SUPPLIER)`, un `IPolicyReferenceCatalog` exact-one `SUPPLIER` y un lookup `approved-supplier-fact-owner/v1`. Owner y catálogo responden positivo solo para el UUID/version current operacional `ACTIVE` de la misma organización; stale, `DRAFT|PENDING_APPROVAL|SUSPENDED|BLOCKED` o inexistente son no utilizables sin revelar existencia. La assertion Supplier usa sin cambios `purchase-request-reference-attestation/v1`; nuevas attestations ligan además `purchase-request-completeness-manifest/v2` y provider `purchase-request-policy-facts/v2` conforme a los schemas cerrados de Datos y contratos, con un snapshot de REQ-08 por línea y facts `SUPPLIER`, `PREFERRED_SUPPLIER` y `EXTERNAL_AGREEMENT_STATUS` reales. Una versión ya atestiguada conserva su snapshot aunque cambie Supplier/Catalog; obtener facts actuales exige nueva Purchase Request version. Attestation/manifest/provider v1 y manifests históricos permanecen legibles y verificables; no se recalculan con datos actuales.

- **REQ-10 — Active Supplier prerequisite y frontera futura.** Nuevas submissions usan el descriptor exacto `policy-approval-adapter/v4` de Datos y contratos: conserva íntegra la proyección budget v3 de SPEC 08 y, para `REQUIRE_ACTIVE_SUPPLIER`, particiona targets por Supplier ref exacta y crea un prerequisite `active-supplier-owner/v1` por partición. Una línea sin supplier, target omitido o snapshot discordante falla `503` sin caso. Attempts v1/v2/v3 persistidos reanudan su adapter original. Un processor exact-one crea o recupera un `SupplierPrerequisiteAttempt` por prerequisite, vuelve a comprobar id/version current `ACTIVE` y señala `SATISFIED` o, ante estado empresarial no elegible, `FAILED`; corrupción, owner/processor 0/2 o error técnico deja el prerequisite `WAITING` para retry. Attempt, lease/fencing, check key, signal key y evidence siguen los contratos exactos abajo; dos instancias no emiten dos señales. El mismo servicio expone a dominios futuros una verificación server-side versionada que devuelve `ELIGIBLE|NOT_ELIGIBLE` para una Supplier ref, pero esta spec no registra producers de PO. Una aprobación previa no garantiza elegibilidad futura: Sourcing/PO deberán comprobar de nuevo la versión current; así `BLOCKED` nunca equivale a autorización para una PO nueva.

- **REQ-11 — API, lectura, auditoría y errores.** La API autenticada ofrece comandos versionados para crear/editar/presentar Supplier y Catalog, cambios de estado, reveal bancario, y lecturas current/history. Cualquier usuario empresarial activo puede listar `supplier-reference/v1` de Suppliers `ACTIVE` y `approved-supplier-reference/v1` efectivo para construir una PR: ids/versiones, legal/trade display name, categorías, monedas, selector, precio/moneda/unidad y vigencia; omite Tax ID, direcciones, contactos, riesgo, score, Banking y attachment.
  `PROCUREMENT_BUYER|PROCUREMENT_APPROVER` organizacionales leen el master y catálogo con Banking enmascarado; `AP_SPECIALIST` lee identidad fiscal, direcciones, contactos, payment terms, monedas y proyección bancaria current enmascarada, y solo obtiene plaintext por el reveal de REQ-05; `AUDITOR` organizacional lee versiones, decisiones y audit, siempre con Banking enmascarado; `ADMIN` no obtiene lectura empresarial ni aprobación por su rol. Fuera de scope responde `404`. Toda mutación, approval result, reveal, descarga, replay conflictivo y corrupción crea el audit causal exacto de Datos y contratos y, cuando cruza módulos, outbox/inbox deduplicado. Problem Details conserva `400`, `401`, `403`, `404`, `409`, `413`, `422 /problems/supplier-reference-invalid` y `503 /problems/supplier-dependency-unavailable`; ninguna respuesta distingue un Supplier de otra organización ni contiene PII o datos bancarios.

- **REQ-12 — Health, migración y operación fail-closed.** Readiness comprueba storage/pointers/versiones/digests, key provider, attachment storage, los adapters de aprobación, processor/backlog, owner Supplier, catálogo Policy y lookup Approved Catalog. Reutiliza exactamente `PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:SUPPLIER` y `POLICY_REFERENCE_CATALOG_UNAVAILABLE:SUPPLIER`, y añade `SUPPLIER_STORAGE_UNAVAILABLE`, `SUPPLIER_DATA_CORRUPTED`, `SUPPLIER_ENCRYPTION_UNAVAILABLE`, `SUPPLIER_ATTACHMENT_STORAGE_UNAVAILABLE`, `APPROVED_SUPPLIER_CATALOG_CORRUPTED`, `SUPPLIER_APPROVAL_ADAPTER_UNAVAILABLE`, `ACTIVE_SUPPLIER_PROCESSOR_UNAVAILABLE` y `ACTIVE_SUPPLIER_BACKLOG`. Un catálogo vacío es válido; 0/2 registros, pointer/digest/ciphertext corrupto, backlog debido mayor de 60 segundos o dependencia inaccesible degradan sin ids, nombres, Tax IDs, suffixes, precios ni digests. Ningún fallo se interpreta como Supplier activo, preferido o con acuerdo activo.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `Supplier` | id, organization, current operational version nullable, working version nullable, lifecycle rowversion | Raíz durable; un solo proposal abierto y ninguna eliminación física. |
| `SupplierVersion` | supplier/version, predecessor, campos de REQ-02/04, status, banking refs, actor, UTC, key, motivo, content digest | Append-only; `DRAFT|PENDING_APPROVAL` no son refs utilizables. |
| `SupplierFiscalIdentity` | supplier, country code, tax id key, first/last approved version | Reserva única; una identidad aprobada no se reasigna a otra raíz. |
| `SupplierBankingDetail` / `Version` | id/version, supplier, metadata, masked suffix, ciphertext, nonce/tag, key version, predecessor | Plaintext solo dentro de la frontera autorizada; append-only e integrity authenticated. |
| `SupplierChangeProposal` | supplier/candidate/base version, change kind, sensitive field set, requested status, editor, key/fingerprint, case/requirement, state | `DRAFT|PENDING|APPROVED|REJECTED|CHANGES_REQUESTED|CANCELLED`; no sustituye Supplier status. |
| `ApprovedSupplierCatalogEntry` / `Version` | id/version, selector, refs, price/currency/unit, agreement, validity, status, attachment, digest, actor/motivo | Un current aprobado y un draft/pending como máximo; no overlap efectivo por selector. |
| `SupplierAgreementAttachment` | id/version, object key, file name, media type, length, SHA-256, state | `STAGED|CONFIRMED`; confirmado es inmutable y nunca se borra mientras esté referenciado. |
| `SupplierPolicyFactSnapshot` | line/source refs, supplier/category/product refs, catalog entry/version nullable, preferred, agreement status, evaluated UTC, digests | Uno por línea en manifest v2; reproduce los facts sin consultar current. |
| `SupplierPrerequisiteAttempt` | prerequisite/case/targets/supplier ref, state/result, check/signal keys, evidence ref, due/retry, lease/fencing, error | Único por prerequisite; `COMPLETED` es terminal y conserva `signal_result=SATISFIED|FAILED`. |
| `SupplierAuditRecord` | action, actor union, cause, changed fields, correlation, effect key, organization, target, UTC | Append-only y atómico con la mutación local; sin valores bancarios ni PII copiada. |

### Schemas públicos y límites

- `supplier-reference/v1` contiene exactamente `{categories_supplied,id,legal_name,supported_currencies,trade_name,version}`. `categories_supplied` usa refs exactas `SPEND_CATEGORY` de SPEC 07. Solo lista current `ACTIVE` de la organización; `trade_name` está presente como null cuando no existe.
- `approved-supplier-reference/v1` contiene exactamente `{catalog_entry_ref,external_contract_reference,negotiated_price,product_ref,spend_category_ref,supplier_ref,unit_code,valid_from,valid_to}` y solo muestra entries efectivas `ACTIVE` según REQ-07.
- `supplier-banking-masked/v1` contiene exactamente `{account_type,bank_country_code,bank_name,banking_detail_ref,currency,is_default,masked_account}`. `masked_account` revela como máximo los cuatro últimos caracteres; si el valor tiene cuatro o menos, no revela ninguno.
- `supplier-agreement-attachment/v1` contiene exactamente `{content_type,file_id,file_name,length,sha256,version}`. El object key y la URL no forman parte del documento.
- Códigos de payment terms/unit usan 1–64 ASCII `[A-Z][A-Z0-9_.:-]*`; motivos, 1–1.000 Unicode scalars; command keys, 1–128 `[A-Za-z0-9._:-]`. Timestamps son UTC. Importes usan `decimal(38,12)` y strings decimales canónicos en wire.

### Contratos Purchase Requests y Policy v2

- `supplier-policy-fact-snapshot/v1` contiene exactamente `{agreement_status,canonicalization_version,catalog_content_digest,catalog_entry_ref,contract_version,evaluated_at,preferred_supplier,product_ref,source_line,spend_category_ref,supplier_ref}`. `catalog_content_digest`, `catalog_entry_ref`, `product_ref` y `supplier_ref` están siempre presentes y pueden ser null; los demás no. `catalog_entry_ref={id,version}`, `source_line={content_digest,id,version}` y las refs usan los objetos exactos de SPEC 06/07. Cada Purchase Request Line tiene exactamente un snapshot, incluso sin Supplier.
- `purchase-request-supplier-fact-snapshots/v1` contiene exactamente `{canonicalization_version,contract_version,organization_id,request_id,request_version,snapshots}`; `snapshots` es el set ordenado sin duplicados de `{line_id,line_version,snapshot_digest}` y cubre exactamente las líneas del manifest. Su SHA-256 es `supplier_fact_snapshots_digest`.
- `purchase-request-completeness-manifest/v2` contiene exactamente `{canonicalization_version,contract_version,line_count,lines,organization_id,policy_manifest_digest,provider_contract_version,provider_id,reference_attestation_digest,request_content_digest,request_id,request_version,supplier_fact_snapshots_digest}`. `lines` conserva el schema v1 `{content_digest,id,version}`; `provider_id=purchase-request-domain`, `provider_contract_version=purchase-request-policy-facts/v2` y `contract_version=purchase-request-completeness-manifest/v2`. Su SHA-256 es el nuevo `domain_attestation_digest`; `policy_manifest_digest` y `purchase-request-reference-attestation/v1` no cambian.
- `purchase-request-policy-facts/v2` conserva el `FactRequest` y `PolicyFactBundle` exactos de SPEC 02. Cambia solo provider contract version, completeness manifest v2 y tres line facts: `SUPPLIER` refleja la ref atestiguada o se omite si es null; `PREFERRED_SUPPLIER` y `EXTERNAL_AGREEMENT_STATUS` se leen del snapshot exacto. Provenance de `SUPPLIER` apunta a la assertion v1; las otras dos keys apuntan a `supplier-facts/<supplier_fact_snapshots_digest>#<line-id>/<snapshot_digest>`. El fact provider recalcula reference attestation, cada snapshot, el set digest y manifest v2 antes de responder.
- El `fact_manifest_digest`, `evaluation_input_digest`, `evaluation_result_digest` y `purchase-request-materiality/v1` conservan sus preimages aprobadas: cambian naturalmente sus valores porque el provider contract, completeness manifest, facts y provenance v2 son distintos. No se cambia `purchase-request-line-content/v1`, `line_content_digest`, `policy_manifest_digest` ni ningún documento v1.

### Adapter v4 y prerequisite Supplier

- El descriptor exacto es `{adapter_id=policy-approval-adapter,contract_version=v4,subject_type=PURCHASE_REQUEST,operation=SUBMIT_PURCHASE_REQUEST,requester_required=true,allows_requester_as_originator=true,supersession_delta_supported=true}`. Input/output, targets, DAG, acciones, snapshot y supersession delta son los de adapter v3; Budget conserva byte por byte sus parámetros y source control digest v3.
- Para cada control `REQUIRE_ACTIVE_SUPPLIER`, v4 carga el fact `SUPPLIER` de cada target confirmado y agrupa por `{id,version}`. Cada grupo produce un prerequisite con owner `{adapter_id=active-supplier-owner,adapter_version=v1}`, parameters JSON exacto `{"supplier_ref":{"id":"<uuid-d>","version":<positive-int>}}` y únicamente sus targets. El set de grupos cubre cada target del control una vez; null, +1/-1 target, Supplier fuera del snapshot o target repetido falla `503` sin submission.
- La key de cada prerequisite es `ACTIVE_SUPPLIER:<partition_digest>`, donde `partition_digest` es SHA-256 con `approval-canonical-json/v2` de propiedades exactas `{canonicalization_version,requirement_key,supplier_ref}`. `source_control_digest` conserva la preimage de SPEC 05 `{parameters,phase,requirement_key,targets,type}` con el requirement key original y los targets de esa partición. El `submission_fingerprint` conserva la preimage de SPEC 03, con `adapter_version=v4` y el grafo particionado completo; no cambia `approval-canonical-json/v2`.
- `ActiveSupplierPrerequisiteProcessorRegistration` contiene exactamente `{adapter_id,adapter_version,processor_id}` y resuelve uno para `active-supplier-owner/v1`; su processor debe ejecutar con el mismo `issuer+client_id` que Approval resolvió y persistió. Cero/dos registros o workload distinto degrada readiness y no reclama attempts.
- Un `SupplierPrerequisiteAttempt` usa `PENDING → PROCESSING → CHECKED → SIGNALLING → COMPLETED`; `CHECKED` conserva `signal_result=SATISFIED|FAILED`. Un error antes del check devuelve a PENDING; después conserva CHECKED o SIGNALLING con `next_attempt_at`, y nunca sustituye un error técnico por FAILED. El lease dura 30 segundos, se renueva como máximo cada 10, y fencing obsoleto no confirma check, evidence ni signal. `check_key=supplier:<prerequisite_id>:check` y `signal_key=supplier:<prerequisite_id>:signal` son deterministas.
- `active-supplier-evidence/v1` contiene exactamente `{attempt_id,canonicalization_version,checked_at,contract_version,organization_id,parameters_digest,prerequisite_id,result,signal_key,supplier_ref,targets}` y usa `policy-canonical-json/v1`; `targets` es el set exacto Approval y `parameters_digest` es SHA-256 de los bytes exactos de parameters. Su digest se envía con `evidence_reference=supplier://<attempt_id>`. El `signal_fingerprint` conserva `approval-canonical-json/v2` y la preimage exacta de SPEC 03 `{canonicalization_version,evidence_digest,evidence_reference,owner_client_id,owner_issuer,prerequisite_id,prerequisite_version,result,signal_key}`. Replay reutiliza attempt, check, evidence y signal; una preimage distinta o evidencia corrupta permanece fail-closed.

### Approval y eventos

- `supplier-governance-approval-adapter/v1` se registra exact-one para `(SUPPLIER_CHANGE,APPROVE_SUPPLIER_CHANGE)` y `(SUPPLIER_CATALOG_CHANGE,APPROVE_SUPPLIER_CATALOG_CHANGE)`. Ambos exigen requester/editor, permiten que sean originator y los excluyen de elegibilidad; no hacen carry-forward entre proposals.
- El target exacto contiene `{id,material_snapshot_digest,type,version}`, donde type es `SUPPLIER_VERSION|APPROVED_SUPPLIER_CATALOG_VERSION`. El snapshot liga base/candidate refs, diff de campos, requested status y attachment/banking refs, nunca plaintext bancario.
- El consumer acepta `approval-result/v3` y `approval-case-lifecycle/v1`, verifica organization/case/subject/target/digest y deduplica por `event_id+contract_version`. Solo `APPROVE` de una decisión humana vigente aplica el proposal; eventos duplicados, tardíos o de otro proposal conservan historia sin mover pointers.
- El actor exacto es `{system_id,type,user_id,workload_client_id,workload_issuer}` con `type=USER|WORKLOAD|SYSTEM` y exactamente una variante completa. `cause` es null o `{contract_version,event_id,source}`; `source=APPROVAL|SUPPLIER|PURCHASE_REQUEST`. `target` es `{id,type,version}`. `SupplierAuditRecord` contiene exactamente `{action,actor,cause,changed_fields,correlation_reference,effect_key,occurred_at,organization_id,target}`; `changed_fields` es un set de nombres, nunca valores. `effect_key` es la command key o `approval:<event_id>|reveal:<key>|download:<key>` y es única por `(organization_id,action,effect_key)`, por lo que aplicar un evento o acceso dos veces no duplica efecto/audit.
- `supplier-status-changed/v1` contiene `{contract_version,event_id,occurred_at,organization_id,previous_status,supplier_id,supplier_version,status}`; no contiene nombres, identidad fiscal ni causa libre. Su outbox es atómico con el cambio aprobado para futuros consumers.

### Matching del Approved Supplier Catalog

- `product_ref` de una línea es Preferred Product si existe; en su ausencia, Required Product; ambas no coexisten por SPEC 06. Una entry product-specific solo coincide con el mismo `{id,version}`. Si no existe coincidencia product-specific, puede aplicar la entry con `product_ref=null` de la misma Supplier y Spend Category.
- Para cada nivel de especificidad, una entry current se clasifica en el instante congelado: `ACTIVE` si su status es ACTIVE y está dentro de vigencia; `EXPIRED` si status ACTIVE y `valid_to<=at`; `INACTIVE` si status INACTIVE o `at<valid_from`. La selección no cruza Supplier ni Category.
- `PREFERRED_SUPPLIER=true` si y solo si la entry seleccionada es `ACTIVE`. Policy recibe el status, no una decisión de waiver. Precio, acuerdo y attachment quedan fuera del fact bundle v2; `catalog_content_digest` dentro del snapshot los compromete junto con selector/vigencia, y la API Policy no lo expone.

### Canonicalización y digests

Todos los digests del dominio Supplier no bancarios usan `policy-canonical-json/v1` + SHA-256: UTF-8 sin BOM/whitespace, propiedades exactas presentes y ordenadas ordinalmente, strings NFC, UUID `D` minúsculo, timestamps UTC con siete decimales, decimales como strings invariantes y sets ordenados por bytes canónicos sin duplicados. Los source/submission/signal digests de Approval conservan `approval-canonical-json/v2` según SPEC 03/05/08; no se reetiquetan bajo la canonicalización de Supplier. Cambiar una preimage exige nueva `canonicalization_version`; cambiar el schema documental sin cambiar preimage exige otra `contract_version`.

| Digest/fingerprint | Propiedades exactas del preimage |
| --- | --- |
| `supplier_identity_key_digest` (`supplier-identity-key/v1`) | `canonicalization_version`, `contract_version`, `country_code`, `organization_id`, `tax_id_key` |
| `supplier_content_digest` (`supplier-version/v1`) | `addresses`, `banking_refs`, `canonicalization_version`, `categories_supplied`, `contacts`, `contract_version`, `country_code`, `legal_name`, `payment_terms`, `performance_score`, `performance_source`, `risk_status`, `status`, `supplier_id`, `supported_currencies`, `tax_id`, `trade_name`, `version` |
| `approved_catalog_content_digest` (`approved-supplier-catalog-entry/v1`) | `attachment`, `canonicalization_version`, `contract_version`, `external_contract_reference`, `negotiated_price`, `product_ref`, `spend_category_ref`, `status`, `supplier_ref`, `unit_code`, `valid_from`, `valid_to`, `version` |
| `supplier_policy_fact_snapshot_digest` (`supplier-policy-fact-snapshot/v1`) | `agreement_status`, `canonicalization_version`, `catalog_content_digest`, `catalog_entry_ref`, `contract_version`, `evaluated_at`, `preferred_supplier`, `product_ref`, `source_line`, `spend_category_ref`, `supplier_ref` |
| `supplier_fact_snapshots_digest` (`purchase-request-supplier-fact-snapshots/v1`) | `canonicalization_version`, `contract_version`, `organization_id`, `request_id`, `request_version`, `snapshots` |
| `domain_attestation_digest` (`purchase-request-completeness-manifest/v2`) | `canonicalization_version`, `contract_version`, `line_count`, `lines`, `organization_id`, `policy_manifest_digest`, `provider_contract_version`, `provider_id`, `reference_attestation_digest`, `request_content_digest`, `request_id`, `request_version`, `supplier_fact_snapshots_digest` |
| `active_supplier_evidence_digest` (`active-supplier-evidence/v1`) | `attempt_id`, `canonicalization_version`, `checked_at`, `contract_version`, `organization_id`, `parameters_digest`, `prerequisite_id`, `result`, `signal_key`, `supplier_ref`, `targets` |
| `supplier_change_fingerprint` | `base_version`, `candidate_content_digest`, `canonicalization_version`, `change_key`, `change_kind`, `editor_id`, `expected_version`, `organization_id`, `reason`, `requested_status`, `supplier_id` |
| `active_supplier_partition_digest` (Approval) | `canonicalization_version`, `requirement_key`, `supplier_ref`; usa `approval-canonical-json/v2`, no la canonicalización Supplier |

Banking plaintext no se incluye en estos preimages. El intento idempotente de un comando bancario persiste primero su key y refs server-side; un replay compara el plaintext canónico descifrado en memoria y recupera la misma versión, mientras una carga distinta da `409`. Nunca se almacena un hash sin clave del número de cuenta.

Vector mínimo de identidad, bytes UTF-8 completos sin salto final:

```json
{"canonicalization_version":"policy-canonical-json/v1","contract_version":"supplier-identity-key/v1","country_code":"PE","organization_id":"11111111-1111-1111-1111-111111111111","tax_id_key":"00000000000"}
```

SHA-256 esperado: `633c1bd7a7e84cd6524c8f448fc105b783c087acda1216296e13f34f5ff5c91d`.

## Impacto sobre especificaciones anteriores

| Contrato anterior | Regla nueva y alcance |
| --- | --- |
| SPEC 05 REQ-03 y mapping `REQUIRE_ACTIVE_SUPPLIER` | `policy-approval-adapter/v4` conserva el owner/parameters v1 pero particiona un control multi-target por Supplier ref para no exigir artificialmente un único proveedor en toda la request. v1/v2/v3 históricos permanecen inmutables. |
| SPEC 06 REQ-04/REQ-05 y proyección Policy | Supplier deja de ser owner futuro: attestation v1 incorpora el owner real y manifest/provider v2 congelan el snapshot de homologación. `PREFERRED_SUPPLIER`/`EXTERNAL_AGREEMENT_STATUS` dejan de ser constantes para nuevas versiones presentadas; line content, reference attestation y sus digests v1 no cambian. |
| SPEC 06 REQ-06 y SPEC 08 adapter v3 | Nuevos submission attempts fijan adapter v4, que replica budget v3 y añade partición Supplier. Attempts ya persistidos reanudan su versión; no se reetiquetan fingerprints, manifests, materiality digests, cases ni prerequisites. |
| SPEC 07 REQ-06/REQ-09 | El slot `ACTIVE_IN_ORGANIZATION/SUPPLIER`, el catálogo Policy `SUPPLIER` y el lookup de homologación pasan de on-demand a obligatorios de readiness con la misma semántica exact-one y gramática de health. Los slots PRODUCT/RISK_SCHEMA/FX continúan opcionales/on-demand. |

La vigencia de estas modificaciones empieza al integrar SPEC 09. Purchase Requests y evaluaciones v1, prerequisites y casos v1–v3, así como sus bytes/digests, permanecen verificables y no se enriquecen desde Supplier/Catalog current.

## Migración, despliegue y reversión

- La migración crea roots/versions/pointers de Supplier, identidad fiscal, Banking cifrado, proposals, Approved Catalog, attachments, snapshots, attempts, audit/outbox/inbox, índices y constraints. No fabrica Suppliers baseline ni modifica tablas/digests históricos de Organization, Policy, Approval, Purchase Requests o Budget.
- El preflight exige SPEC 01–08 integradas; key provider y attachment storage operativos; adapters Supplier exact-one; owner Supplier, catálogo Policy, lookup y processor exact-one; cero identidades fiscales duplicadas; y cero manifests v2 sin snapshot completo. Purchase Request drafts con refs sintéticas no se autocorrigen: deben revisarse con una ref emitida por Supplier Master.
- Orden: schema/readers y cifrado; administración Supplier/Catalog; adapters/consumer de aprobación; owner/catálogos/lookups; provider PR v2 y adapter v4 manteniendo readers anteriores; processor en pausa; health/preflight; luego processor y tráfico nuevo.
- Antes de tráfico se ejercitan activación, cambio no sensible, cambio fiscal/bancario, suspend/block/reactivate, aprobación/rechazo/crash, entry general/product-specific, expiry/overlap, attachment, attestation/facts, prerequisite y dos instancias.
- Rollback detiene nuevas mutaciones y submissions, mantiene readers, consumer de resultados y processor hasta drenar proposals/attempts. Una vez emitidos manifest/provider v2 o adapter v4 no se vuelve a una versión incapaz de leerlos: se corrige hacia adelante. No hay down migration destructiva ni descifrado masivo.
- `docs/supplier-operations.md` documenta configuración de keys sin exponerlas, rotación por nueva banking version, bootstrap manual de Suppliers, preflight, rollout, backlog/retry, storage/attachments huérfanos, corrupción, recovery y forward-fix. Ningún procedimiento edita pointers, ciphertext, status, digests o audit a mano.

## Seguridad y privacidad

- Organización y usuario se derivan del JWT; roles/authorities efectivos provienen de SQL conforme a SPEC 01. Payloads no autocertifican actor, organization, approval, owner result ni status derivado.
- Supplier Master contiene PII empresarial y Banking altamente sensible. Listas minimizan campos; búsquedas por Tax ID, contactos y detalles bancarios solo están disponibles a roles operativos autorizados y nunca permiten enumerar otra organización.
- Banking se cifra en aplicación con claves fuera de SQL y authenticated encryption. Backups, read replicas y consultas generales no contienen plaintext. La rotación crea nueva versión cifrada; no reescribe historia sin un procedimiento de re-encryption auditable y forward-only.
- El approver asignado recibe reveal solo durante su task exacta; una reasignación o terminalidad revoca la siguiente lectura. AP accede solo a la versión operacional current. URLs de attachments son temporales, scoped y no se registran completas.
- Logs, métricas, trazas, health y Problem Details omiten nombres, Tax IDs, direcciones, contactos, cuentas, suffixes, motivos libres, precios, filenames, URLs, payloads, snapshots y digests completos. Audit conserva identidades internas, versiones y field names; los valores se consultan desde la versión bajo autorización.

## Requisitos no funcionales

- **NFR-01 — Historia y determinismo.** Cada estado, contenido, homologación, approval y fact se reconstruye desde versiones/evidencia append-only; los goldens independientes fijan bytes y SHA-256, y ningún snapshot histórico consulta current.
- **NFR-02 — Segregación y mínimo privilegio.** Editor y approver del mismo proposal nunca coinciden; Banking plaintext solo cruza las dos lecturas explícitas de REQ-05 y cada acceso es revocable/auditable.
- **NFR-03 — Exactly-once lógico.** Keys, expected versions, constraints, inbox/outbox, attempts, leases y fencing producen una sola versión, aplicación de approval, snapshot o signal bajo retry, timeout, crash y dos instancias.
- **NFR-04 — Fail-closed entre módulos.** Owner, catálogo, lookup, adapter, processor, storage, key o evidencia 0/2/ausente/corrupta nunca produce Supplier activo, preferred, acuerdo activo, case completo ni signal SATISFIED.
- **NFR-05 — Operación recuperable.** Un prerequisite debido alcanza señal o retry explícito en máximo 60 segundos; health distingue cada dependencia sin datos empresariales y el runbook permite drenar/recuperar sin mutar historia.
- **NFR-06 — Privacidad verificable.** Capturas automatizadas de API, logs, spans, métricas, health y audit minimizado no contienen los datos prohibidos por Seguridad y privacidad; ciphertext alterado no se revela ni se acepta.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | estados/diffs sensibles, schemas, normalización, matching, vigencia, masking, permisos, parámetros y goldens | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | SQL Server, append-only, unicidad fiscal/overlap, concurrencia, cifrado, approvals, inbox/outbox, leases/fencing y migración | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Docker/Testcontainers |
| API/E2E | JWT por rol, Supplier/Catalog, reveal/download, Problem Details, health y PR→Policy→Approval→active supplier real | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` con Docker/Testcontainers/LocalStack |
| Contrato cross-module | Supplier aprobado → entry activa → PR con supplier ref → attestation/provider v2 → facts reales → adapter v4 → prerequisite → signal | Suite API/E2E sin owner Supplier, lookup, processor, Policy catalog ni Approval doubles |
| Operación | rollout 0/1/2, keys/storage caídos, ciphertext corrupto, attachment huérfano, backlog, restart y rollback compatible | Pruebas automatizadas y `docs/supplier-operations.md` |

Todos los criterios son automáticos. Los negativos incluyen Tax ID duplicado/case drift, stale expected version, autoaprobación, grant inválido, cambio sensible disfrazado, supplier/category/product stale, overlap de vigencia, attachment adulterado/sobredimensionado, reveal por actor incorrecto, owner/catalog/lookup/processor 0/2, snapshot corrupto, targets con dos suppliers, signal perdida, lease/fencing stale y PII en telemetría.

## Decisiones

- **DEC-01 — Punteros operacional y de trabajo separados.** Un cambio pending no desactiva ni reescribe la versión ya aprobada; se descarta usar un único current que haría al Supplier temporalmente inutilizable durante revisión.
- **DEC-02 — No todo cambio exige Approval.** Identidad, términos, categorías, riesgo, performance, Banking y estados son sensibles; display/contacto/dirección pueden avanzar por Buyer con audit. Se descarta pedir aprobación empresarial para correcciones puramente operativas.
- **DEC-03 — Banking separado y cifrado.** SupplierVersion referencia Banking versions, no contiene plaintext ni hash público. Se descarta guardar cuentas en JSON general o confiar solo en masking/TDE.
- **DEC-04 — Catálogo por categoría con Product opcional.** La forma general funciona antes de Product Master y la específica prevalece cuando existe Product atestiguado. Se descartan un catálogo solo-producto y un acuerdo sin precio estructurado.
- **DEC-05 — El catálogo publica facts, no waiver.** `PREFERRED_SUPPLIER` y agreement status son evidencia; Policy conserva la decisión sobre RFQ y los demás controles no se eliminan implícitamente.
- **DEC-06 — Snapshot al presentar.** Homologación y acuerdo se congelan junto con la attestation PR; se descarta consultar current durante replay porque alteraría materiality y resultados históricos.
- **DEC-07 — Adapter v4 particiona Supplier.** Un prerequisite conserva el contrato singular `active-supplier-owner/v1`; se descarta mezclar Suppliers en parámetros nuevos o alterar adapters v1–v3.
- **DEC-08 — Reactivación aprobada de SUSPENDED y BLOCKED.** Ambos pueden recuperarse con proposal nuevo, autoridad y SoD; se descarta convertir un bloqueo corregible en eliminación o UUID nuevo que chocaría con identidad fiscal.

## Plan de implementación

### Bloque 1 — Supplier Master e identidad

- **T-01 — Dominio, contratos y persistencia.** Implementar roots/versiones/pointers, identidad fiscal, datos/validaciones, diff sensible, canonicalización, constraints, migración y audit. Añadir unitarias, goldens e integración concurrente. Cubre: REQ-01, REQ-02, REQ-04, REQ-11, NFR-01, NFR-03, NFR-04, CA-01, CA-02.
- **T-02 — Gobierno y Approval.** Implementar drafts/proposals, adapters exact-one, requirement Supplier Master, SoD, consumer idempotente, estados/reactivación y API por rol. Cubre: REQ-02, REQ-03, REQ-11, NFR-02, NFR-03, NFR-04, CA-02, CA-03.

**Resultado verificable:** un Supplier se activa y evoluciona con una sola versión operacional, identidad única, approval reproducible y ningún bypass administrativo.

### Bloque 2 — Banking y catálogo homologado

- **T-03 — Banking protegido.** Implementar versiones cifradas, key provider, idempotencia sin hashes de cuenta, masking, reveals AP/approver, revocación y audit minimizado. Cubre: REQ-05, REQ-11, REQ-12, NFR-02, NFR-03, NFR-04, NFR-06, CA-04.
- **T-04 — Approved Supplier Catalog y attachments.** Implementar roots/versiones, selectors, precios/vigencia/overlap, workflow, staging/confirmación/descarga y matching determinista. Cubre: REQ-06, REQ-07, REQ-08, REQ-11, NFR-01, NFR-02, NFR-03, NFR-04, NFR-06, CA-05, CA-06.

**Resultado verificable:** una homologación aprobada y vigente se lista y se selecciona de forma inequívoca; Banking y agreements solo atraviesan superficies autorizadas.

### Bloque 3 — Purchase Requests, Policy y owner activo

- **T-05 — Owners y facts versionados.** Registrar owner Supplier, catálogo Policy y lookup exact-one; implementar attestation/manifest/provider v2, snapshots, proyección real, compatibilidad v1 y negativos. Cubre: REQ-08, REQ-09, NFR-01, NFR-03, NFR-04, CA-06, CA-07.
- **T-06 — Adapter v4 y processor.** Particionar controles por Supplier, preservar budget v3, implementar attempts/leases/fencing/evidence/signal y verificación futura de elegibilidad. Cubre: REQ-10, NFR-03, NFR-04, NFR-05, CA-07.

**Resultado verificable:** PR→Policy→Approval usa Supplier/Catalog reales, y cada prerequisite se satisface solo tras comprobar una versión current ACTIVE.

### Bloque 4 — Operación y certificación

- **T-07 — API, health y observabilidad.** Completar lecturas/errores, health exacto, telemetría minimizada, límites y pruebas de autorización/privacidad. Cubre: REQ-11, REQ-12, NFR-02, NFR-04, NFR-05, NFR-06, CA-04, CA-08.
- **T-08 — Rollout, recovery y E2E real.** Implementar preflight, compatibilidad v1–v4, fault injection, migración/rollback y runbook; ejecutar recorrido cross-module sin doubles. Cubre: REQ-03, REQ-07, REQ-09, REQ-10, REQ-12, NFR-01, NFR-03, NFR-04, NFR-05, NFR-06, CA-03, CA-05, CA-06, CA-07, CA-08.

**Resultado verificable:** readiness y suites demuestran rollout/recovery, compatibilidad histórica y recorrido real sin facts sintéticos, signal falsa ni filtración sensible.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-04, NFR-01, NFR-03 | Crear/editar concurrente deja una sola sucesora y pointers válidos; Country+Tax ID duplicado, predecessor/expected stale, categoría/moneda inválida o score sin provenance falla sin versión/audit de éxito. Historia reconstruye contenido y el golden de identidad coincide. | Automática: unitarias, fixture SHA-256 independiente e integración SQL multi-DbContext/constraints. |
| CA-02 | REQ-02, REQ-03, REQ-11, NFR-02, NFR-03, NFR-04 | Buyer aplica un cambio no sensible ACTIVE, pero cualquier campo sensible o estado crea proposal/case. Solo un Procurement Approver con grant Supplier Master aprueba; editor, ADMIN, grant parcial/vencido o target distinto falla. Reject/change conserva operational y SUSPENDED/BLOCKED vuelven a ACTIVE solo por nueva aprobación. | Automática: matriz dominio/eligibilidad, integración Approval e API/E2E JWT. |
| CA-03 | REQ-03, REQ-11, NFR-03, NFR-04 | Crash/retry antes/después de crear case o aplicar result recupera el mismo proposal/case/version; eventos duplicados/tardíos no mueven pointers. Los dos subject/operations resuelven un adapter y 0/2 bloquea sin aprobación parcial. | Automática: integración inbox/outbox, fault injection y dos instancias. |
| CA-04 | REQ-05, REQ-11, REQ-12, NFR-02, NFR-04, NFR-06 | SQL/telemetría/API ordinaria no contienen plaintext; máscara no revela cuentas cortas. Solo AP obtiene current y solo el approver asignado obtiene pending, cada reveal auditado; terminalidad/reasignación revoca. Ciphertext/tag/key alterado o provider caído da `503` sin plaintext ni cambio confirmado. | Automática: integración con key provider controlado, API/E2E de roles, inspección DB y captura de logs/spans/health/audit. |
| CA-05 | REQ-06, REQ-07, NFR-01, NFR-02, NFR-03 | Entry general y product-specific aprobadas conservan precio/acuerdo/attachment y la específica prevalece; overlap del mismo selector, Supplier/category/product stale, attachment inválido o autoaprobación falla. Expiry cambia la proyección por reloj sin mutar historia; descarga autorizada expira en ≤15 min. | Automática: unitarias con fake clock, SQL concurrente, LocalStack y API/E2E. |
| CA-06 | REQ-08, REQ-09, NFR-01, NFR-03, NFR-04 | PR sin Supplier proyecta false/NONE; con Supplier y entry efectiva proyecta true/ACTIVE; general/product-specific, inactive/expired/none producen la tabla exacta. Replay devuelve el snapshot original tras cambios current y una nueva PR version obtiene snapshot nuevo. Owner/catalog/lookup 0/2 o match ambiguo da `503` sin manifest/bundle parcial. | Automática: contrato/goldens, integración PR/Policy y carreras con cambio de catálogo. |
| CA-07 | REQ-09, REQ-10, NFR-03, NFR-04, NFR-05 | Un control sobre líneas con dos Suppliers produce dos prerequisites v1 ligados a targets exactos; uno ACTIVE puede SATISFIED y otro BLOCKED FAILED. Error técnico queda WAITING, crash/lease/fencing/replay emite una sola signal y backlog se diagnostica en ≤60 s. Adapter v4 conserva exactamente demands budget v3 e historia v1–v3. | Automática: contrato bidireccional, SQL worker multiinstancia y E2E PR→Policy→Approval sin doubles. |
| CA-08 | REQ-11, REQ-12, NFR-04, NFR-05, NFR-06 | Cada rol ve solo su proyección; cross-scope es `404`, ADMIN no lee/aprueba y errores siguen la taxonomía. Health distingue todos los códigos 0/1/2, storage/key/corrupción/backlog sin PII/precio/suffix; migración y rollback conservan versions, ciphertext, manifests, cases y digests históricos, y el runbook recupera staging/attempts sin edición manual. | Automática: API/E2E, health/telemetría, migración sobre baseline, rollback compatible y runbook ejercitado. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Un pending sustituye al Supplier activo | Presentar un cambio hace fallar PRs sin decisión | Punteros separados, DEC-01 y CA-02. |
| Clasificación manipulable | Buyer cambia Banking como no sensible | Diff server-side, lista cerrada y CA-02/CA-04. |
| Filtración bancaria o de PII | Plaintext aparece en SQL general, logs, audit o URL | Cifrado separado, reveals mínimos y CA-04/CA-08. |
| Homologación equivale a waiver implícito | Catalog entry elimina RFQ sin Policy | DEC-05, facts tipados y CA-06. |
| Matching ambiguo de acuerdos | Dos entries activas arrojan precio/fact distinto según query | No-overlap, precedencia Product y fallo cerrado en CA-05/CA-06. |
| Control combinado mezcla Suppliers | Un prerequisite valida solo el primer Supplier | Partición adapter v4 y CA-07. |
| ACTIVE queda obsoleto antes de PO | Approval previo permite ordenar a Supplier luego BLOCKED | Recheck current obligatorio en la futura frontera PO y servicio de REQ-10. |
| Owner configurado parece operativo | Readiness sano pero prerequisite nunca se procesa | Processor exact-one, backlog y CA-07/CA-08. |
| Rollback reinterpreta facts históricos | Provider v1 recalcula preferred/agreement desde current | Versiones v2, readers compatibles y CA-06/CA-08. |
