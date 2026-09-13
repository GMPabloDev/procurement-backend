# SPEC 05 — Integración de Policy con Approval Workflow

> **Formato:** sdd/v3
> **Estado:** Implementada
> **Ejecución:** Integrada
> **Vigencia:** Vigente
> **Revisión:** 1
> **Digest contractual:** 001c559d72845b0f77919df50bad5e7b74eeecb5e6dc8921d52bba932cb20f70
> **Fecha:** 2026-09-11
> **Actualizada:** 2026-09-13
> **Aprobada el:** 2026-09-13
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Convertir controles persistidos de SPEC 02 en approvals y prerrequisitos de SPEC 03 y permitir que Policy verifique un quotation waiver únicamente contra una decisión autorizada, vigente y ligada a la evaluación exacta.
> **Depende de:** Ninguna
> **Modifica:** SPEC 02, SPEC 03, SPEC 04
> **Reemplaza:** Ninguna

## Contexto

SPEC 02 ya está implementada y produce `GeneratedControl`; mantiene un verifier default-deny hasta que exista Approval Workflow. SPEC 03 define casos, DAG, asignación, decisiones y evidencia; SPEC 04 agrega evolución y revocación. Falta el adapter que traduzca controles sin inventar semántica y el verifier HTTP que cierre el quotation waiver fail-closed.

La investigación detectó drift entre el contrato aprobado de SPEC 02 y su código actual: `PolicyApprovalDescriptor.DecisionScope` acepta tokens como `ORGANIZATION`/`COST_CENTER` en vez de `decision-scope/v1`, el request HTTP implementado de waiver omite bindings que REQ-14 de SPEC 02 exige, representa cobertura solo con UUIDs sin versión/digest material y todavía permite iniciar el flujo desde un usuario `ADMIN`. Esta spec no cambia aquella decisión: la modifica solo para cerrar esa implementación incompleta y modifica SPEC 03 con una extensión persistida de excepción, sin reinterpretar sus approvals ordinarias.

## Alcance

### Incluye

- Adapter in-process exact-one para transformar un `PolicyEvaluationBundle` vigente en una submission idempotente de Approval Workflow.
- Producción server-side de targets `PURCHASE_REQUEST_LINE` versionados y de su digest material desde el snapshot Policy persistido.
- Validación y producción de `DecisionScopeDescriptor` canónico, cerrando el drift existente en Policy.
- Mapping explícito de cada control de SPEC 02 a requirement humano, prerrequisito externo o ausencia de nodo, con owner versionado exact-one.
- Secuencia Department → Finance/IT/Legal paralelos → Procurement/PRE_PO mediante dependencias por target.
- Extensión persistida `policy-exception-request/v1` ligada a un requirement ordinario para `REDUCE_MIN_VALID_QUOTATIONS`, sin carry-forward.
- `POST /v1/policy-exceptions/verify` autenticado, con lookup server-side, bindings completos, nonce, vigencia, revocación, SoD y evidencia canónica.
- Ajuste del cliente/verifier y eliminación del inicio por usuario actuales de Policy, pruebas bidireccionales, observabilidad y rollout fail-closed.

### No incluye

- Reglas, evaluación o administración de políticas; pertenecen a SPEC 02.
- Cambiar el lifecycle, routing, acciones o digest de las approvals ordinarias de SPEC 03; esta spec solo agrega la extensión enlazada de excepción y su consulta.
- Delegación, supersesión, carry-forward genérico o revocación base; pertenecen a SPEC 04, salvo prohibir carry-forward para la extensión de excepción.
- Ejecución material de budget checks, documentos, supplier, Procurement o sourcing; sus dominios propietarios señalan prerrequisitos futuros.
- Purchase Requests, Cost Centers, Supplier Master, RFQ, quotations, awards, POs, invoices o payments.
- Excepciones distintas de `REDUCE_MIN_VALID_QUOTATIONS`.
- Convertir un control no humano en aprobación manual, ignorarlo porque su owner no existe o permitir bypass de `ADMIN`.
- Interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Consumo exacto e idempotente del bundle.** Un adapter `policy-approval-adapter/v1`, registrado exactamente una vez para su subject/operation, recibe por referencia el `PolicyEvaluationBundle` persistido de mayor `evaluation_sequence` para la misma organización, subject/version y manifest. Recalcula/verifica policy, input, result y manifest digests antes de construir la submission de SPEC 03. Para cada línea cubierta obtiene id/versión del `CompletenessManifest` persistido y calcula el `material_snapshot_digest` desde el `PolicyInputSnapshot` exacto según Datos y contratos; no acepta ids, versiones, tipos ni digests aportados de nuevo por el caller. La `submission_key` y fingerprint ligan bundle id/result digest, policy id/content digest, subject, manifest, controls, requester/originator y targets. Replay idéntico devuelve el mismo caso; otra carga da `409`. Bundle `BLOCKED`, corrupto, no actual, con línea sin snapshot o de otra versión se rechaza sin crear caso.

- **REQ-02 — Scope tipado y cierre de drift.** Antes de habilitar el adapter, `PolicyApprovalDescriptor.DecisionScope` debe producir y validar exclusivamente JSON canónico `decision-scope/v1` compatible con SPEC 03. `ORGANIZATION` usa refs nulas; `LEGAL_ENTITY` y `DEPARTMENT` usan UUID/version del catálogo atestiguado. Tokens legacy, JSON no canónico, campos extra, referencia inactiva y `COST_CENTER` se rechazan al publicar/evaluar Policy y al consumir el bundle. El cambio corrige la implementación de un requisito ya aprobado de SPEC 02; no se interpreta silenciosamente un token ni se migra un bundle histórico como si hubiera contenido datos ausentes.

- **REQ-03 — Mapping cerrado y owner resoluble.** `REQUIRE_APPROVAL` crea uno o más `ApprovalRequirement` y particiona targets de forma estable cuando descriptor, dependencias, exclusiones o routing difieren. `REQUIRE_BUDGET_CHECK`, `REQUIRE_SUPPORTING_DOCUMENT`, `REQUIRE_ACTIVE_SUPPLIER`, `REQUIRE_QUOTATIONS` y `REQUIRE_PROCUREMENT` crean `ExternalPrerequisite` con el owner adapter/version fijo de la tabla y conservan todos los parámetros y digests. En el ingreso, el registry de SPEC 03 debe resolver exactamente un workload allowlisted para cada owner requerido: ausencia o ambigüedad devuelve `503` sin crear caso. Solo un prerequisite cuyo owner ya fue resuelto y persistido queda `WAITING` hasta recibir señal; que el dominio owner todavía no esté desplegado no autoriza omitirlo ni convertirlo en task humana. `ALLOW`, `REQUIRE_PO` y `ALLOW_DIRECT_PURCHASE` no crean nodos y permanecen para el dominio owner. `BLOCK`, resultado `BLOCKED`, tipo desconocido, parámetro omitido o target incompleto impiden submission.

- **REQ-04 — Secuencia por targets y fases.** El adapter genera dependencias explícitas donde Department Approval ocurre primero; Finance, IT y Legal pueden quedar en paralelo si sus controles no declaran otra dependencia; Procurement espera todas las approvals y controles previos requeridos; Procurement Approval/PRE_PO precede el resultado que habilita emitir PO. Cada edge usa target type/id/version/material digest completos. `stage_code` conserva `DEPARTMENT`, `PRE_PROCUREMENT`, `PROCUREMENT` o `PRE_PO`, pero no sustituye el DAG. Un control externo sin dependent humano sigue siendo condición obligatoria de completion.

- **REQ-05 — Submission persistida de quotation waiver.** Solo un workload autenticado y allowlisted invoca el comando `policy-exception-submission/v1`; la ruta legacy iniciada por usuario, incluido `ADMIN`, se elimina o responde `403`. En una transacción, Workflow crea un `ApprovalCase` con `operation=POLICY_EXCEPTION` y `source_snapshot_digest=binding`, un `ApprovalRequirement` ordinario con role `PROCUREMENT_APPROVER`, authority `PROCUREMENT` y acción `APPROVE|REJECT`, y un `PolicyExceptionRequest` inmutable enlazado uno-a-uno por `case_id + workflow_requirement_key`. La extensión conserva `reference_id`, organization, subject/version, `base_bundle_id`, `base_result_digest`, policy version/content digest, manifest digest, target requirement key, `covered_lines` completas, `from`, `to`, `floor`, requester, originator, `workload_subject_id`, `requested_at`, nonce, vigencia solicitada y binding; `correlation_reference` se conserva solo para trazabilidad y no participa en identidad ni digest. Exige scope unión exacta, exclusiones de requester/originator/workload subject y `1 <= floor <= to < from`; `valid_from` es `decided_at` y `valid_to` es el menor entre la vigencia de authority y `requested_valid_to`, exclusivo. SPEC 04 debe rechazar carry-forward cuando el requirement tenga esta extensión. Otra evaluación o cambio material exige request, caso y decisión nuevos.

- **REQ-06 — Verificación server-side autenticada.** `POST /v1/policy-exceptions/verify` exige service JWT Bearer con audience `approval-workflow` e issuer/client allowlisted antes del lookup. Workflow localiza exactamente un `PolicyExceptionRequest`, su requirement, decisión `APPROVE`, assignment/elegibilidad y posible `DecisionEvidenceRevocation` por `evidence_digest + workflow_decision_id/version`; confirma authority, scope, SoD y vigencia, y compara cada campo del request con lo persistido. No confía en approver, role, authority, scope, validez o `segregation_satisfied` enviados por Policy. La transacción de verificación crea o recupera un `PolicyExceptionVerification` inmutable; request malformado da `400`, identidad `401/403`, evidencia no visible `404`, replay conflictivo `409`, binding expirado/revocado/insuficiente `422` o `verified=false`, y dependencia técnica `503`.

- **REQ-07 — Wire completo y cierre del cliente existente.** Request y response usan todas las propiedades exactas de `workflow-verification-request/v1` y `workflow-verification-response/v1` declaradas en Datos y contratos. `HttpQuotationWaiverVerifier` envía service JWT y todos los bindings aprobados en SPEC 02, sustituye la cobertura UUID-only por targets versionados completos y usa únicamente approver/evidence/scope/SoD devueltos por Workflow. `verified=true` exige contrato completo; JSON malformado, campo requerido ausente o versión incompatible es `PolicyDependencyUnavailableException`. `404/409/422` o `verified=false` no reducen el control. Timeout máximo 5 segundos, cancelación y rotación de credenciales no alteran `issuer + client_id`.

- **REQ-08 — Replay, canonicalización y revocación.** `binding` y `evidence_digest` son los SHA-256 de los preimages exactos de Datos y contratos bajo `approval-canonical-json/v2`; `authority_evidence_digest` y `workflow_decision_digest` reutilizan sin cambios los preimages v2 de SPEC 03. Son únicas `(organization_id, verifier_type, nonce)` y `(organization_id, workflow_decision_id, workflow_decision_version)`. Replay con los mismos campos de los preimages —aunque cambie `correlation_reference`— recupera la verificación original; reutilizar nonce o decisión con otro bundle, target, líneas, vigencia o payload contractual da `409`/no verificada sin otra evidencia. Policy calcula después su propio `exception_verification_digest` de SPEC 02. Expiración o `DecisionEvidenceRevocation` de SPEC 04 invalida verificaciones futuras sin reescribir request, decisión, verificaciones históricas ni evaluaciones pasadas.

## Datos y contratos

Mapping obligatorio:

| Control combinado de SPEC 02 | Proyección en Approval Workflow | Owner adapter/version o decisión |
| --- | --- | --- |
| `REQUIRE_APPROVAL` | Requirement(s) por targets, descriptor, dependencias y exclusiones idénticos | Workflow decide. |
| `REQUIRE_BUDGET_CHECK` | Prerequisite con Cost Center refs/versiones, amount base y currency | adapter `budget-check-owner`, versión `v1`. |
| `REQUIRE_SUPPORTING_DOCUMENT` | Prerequisite con document types y minimum count | adapter `supporting-document-owner`, versión `v1`; señal sin adjuntos. |
| `REQUIRE_ACTIVE_SUPPLIER` | Prerequisite con supplier id/version | adapter `active-supplier-owner`, versión `v1`. |
| `REQUIRE_QUOTATIONS` | Prerequisite con minimum valid y allowance/floor | adapter `quotation-status-owner`, versión `v1`; waiver usa caso separado. |
| `REQUIRE_PROCUREMENT` | Prerequisite con stage y targets | adapter `procurement-stage-owner`, versión `v1`. |
| `ALLOW`, `REQUIRE_PO`, `ALLOW_DIRECT_PURCHASE` | Sin task ni prerequisite | El dominio owner aplica el control. |
| `BLOCK` o bundle `BLOCKED` | Submission rechazada | Nunca crea approvals para sortear bloqueo. |

Los códigos de owner son identidades de contrato, no credenciales. Para cada uno, el registry de SPEC 03 resuelve exactamente un workload `issuer + client_id` allowlisted y persiste ese binding en el prerequisite. Hasta que el owner correspondiente esté registrado, una submission que lo necesite devuelve `503`; `WAITING` solo empieza después de resolverlo.

### Targets y extensión persistida

- Todo control se proyecta sobre targets `PURCHASE_REQUEST_LINE`. Id y versión salen de las refs del `CompletenessManifest` persistido; el set debe coincidir exactamente con `GeneratedControl.SubjectIds` y cada id debe resolver una sola ref.
- `material_snapshot_digest` es SHA-256 de `policy-approval-target/v1` bajo las reglas de `policy-canonical-json/v1`. Para cumplir simultáneamente la precedencia y el orden de SPEC 02, su objeto raíz tiene propiedades exactas `{canonicalization_version,target_contract_version,target_snapshot}`: `canonicalization_version` vale `policy-canonical-json/v1`, `target_contract_version` vale `policy-approval-target/v1` y `target_snapshot` contiene `{base_currency,fact_manifest_digest,facts,legal_entity_id,organization_id,provenance,request_id,request_version,target_id,target_type,target_version}`. `facts` contiene objetos exactos `{line,request}` del `PolicyInputSnapshot` persistido y `provenance` las entradas correspondientes; no se vuelve a consultar un provider.
- `PolicyExceptionRequest` (`policy-exception-request/v1`) conserva id/version, case/requirement id y key, todos los campos del preimage de `binding` y `created_at`; no duplica el estado derivable del case/requirement. Es único por `(organization_id,base_bundle_id,target_requirement_key,binding)` y por `(case_id,workflow_requirement_key)`.
- `PolicyExceptionVerification` conserva id/version, request/decision ids y versiones, `evidence_digest`, binding, nonce, resultado, verifier, `verified_at` y referencia de revocación nullable. Sus dos constraints de REQ-08 serializan verificaciones concurrentes; una carrera idéntica relee la fila ganadora y otra preimage devuelve `409`.

`workflow-verification-request/v1` tiene exactamente estas propiedades, todas presentes:

| Campo | Regla |
| --- | --- |
| `contract_version`, `evidence_digest`, `workflow_decision_id`, `workflow_decision_version` | Versión exacta, SHA-256 e identidad esperada. |
| `organization_id`, `subject_type`, `subject_id`, `subject_version` | Coinciden con caso y evaluación. |
| `base_bundle_id`, `base_result_digest`, `policy_version_id`, `policy_content_digest`, `manifest_digest` | Binding completo de SPEC 02. |
| `target_requirement_key`, `covered_lines[{type,id,version,material_snapshot_digest}]`, `from`, `to`, `floor` | Reducción y cobertura exactas; `1 <= floor <= to < from`. |
| `reference_id`, `requester_id`, `originator_id`, `workload_subject_id`, `requested_at`, `requested_valid_to`, `binding`, `nonce` | Referencia/UUID no vacíos, instantes UTC, SoD y replay; `requested_at < requested_valid_to`. |
| `correlation_reference` | Opaca, 1–120 ASCII según SPEC 03; obligatoria en wire y excluida de binding, evidencia e idempotencia. |

`workflow-verification-response/v1` tiene exactamente estas propiedades, todas presentes:

| Campo | Regla |
| --- | --- |
| `contract_version`, `verified`, `verifier_id`, `verifier_contract_version`, `verifier_reference` | `verified=false` mantiene presentes como `null` los campos de autoridad/evidencia no emitidos. |
| `case_id`, `requirement_id`, `requirement_key`, `evidence_digest`, `binding`, `nonce` | Identidades persistidas y coincidencia exacta. |
| `workflow_decision_id`, `workflow_decision_version`, `workflow_decision_digest` | Decisión `APPROVE` ordinaria de SPEC 03 enlazada al request. |
| `approver_id`, `approver_role`, `authority_type`, `eligibility_evidence`, `eligibility_evidence_digest`, `authority_evidence_digest` | Evidencia server-side; `eligibility_evidence` es el JSON canónico exacto persistido por SPEC 03. |
| `decision_scope`, `covered_lines[{type,id,version,material_snapshot_digest}]`, `segregation_satisfied` | Cobertura completa y SoD verdadera. |
| `valid_from`, `valid_to`, `revoked_at` | `verified=true` exige `valid_from <= now < valid_to` y `revoked_at=null`. |

### Preimages y vectores

Todos los objetos usan propiedades exactas presentes, UUID `D` minúsculo, timestamps UTC con siete decimales, sets ordenados por bytes canónicos y sin duplicados. Campo desconocido, nulo no permitido o versión desconocida se rechaza.

| Digest | Propiedades exactas del preimage `approval-canonical-json/v2` |
| --- | --- |
| `binding` | `base_bundle_id`, `base_result_digest`, `canonicalization_version`, `contract_version`, `covered_lines`, `floor`, `from`, `manifest_digest`, `nonce`, `organization_id`, `originator_id`, `policy_content_digest`, `policy_version_id`, `reference_id`, `requested_at`, `requested_valid_to`, `requester_id`, `subject_id`, `subject_type`, `subject_version`, `target_requirement_key`, `to`, `workload_subject_id` |
| `evidence_digest` | todas las propiedades de `binding` más `approver_id`, `approver_role`, `authority_evidence_digest`, `authority_type`, `binding`, `case_id`, `decision_scope`, `eligibility_evidence`, `eligibility_evidence_digest`, `requirement_id`, `requirement_key`, `revoked_at`, `segregation_satisfied`, `valid_from`, `valid_to`, `verifier_contract_version`, `verifier_id`, `verifier_reference`, `workflow_decision_digest`, `workflow_decision_id`, `workflow_decision_version` |

Estos son los bytes UTF-8 completos de los tres vectores propios de esta spec; no llevan salto final. Los preimages de `authority_evidence_digest` y `workflow_decision_digest` siguen siendo exclusivamente los de SPEC 03; el vector de evidencia trata sus digests publicados dentro del JSON como inputs y no redefine su derivación.

```json
{"canonicalization_version":"policy-canonical-json/v1","target_contract_version":"policy-approval-target/v1","target_snapshot":{"base_currency":"EUR","fact_manifest_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","facts":{"line":{"amount_base":"100"},"request":{"purchase_type":"GOOD"}},"legal_entity_id":"aaaaaaaa-0000-0000-0000-000000000000","organization_id":"11111111-1111-1111-1111-111111111111","provenance":{"line.amount_base":"provider://amount","request.purchase_type":"provider://request"},"request_id":"22222222-2222-2222-2222-222222222222","request_version":3,"target_id":"55555555-5555-5555-5555-555555555555","target_type":"PURCHASE_REQUEST_LINE","target_version":7}}
```

SHA-256 target: `7ff665cae24dfb390c58c1e784c83ef9f5781f8966f387beb9e691b2734477de`.

```json
{"base_bundle_id":"33333333-3333-3333-3333-333333333333","base_result_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","canonicalization_version":"approval-canonical-json/v2","contract_version":"workflow-verification-request/v1","covered_lines":[{"id":"55555555-5555-5555-5555-555555555555","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"PURCHASE_REQUEST_LINE","version":7},{"id":"66666666-6666-6666-6666-666666666666","material_snapshot_digest":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","type":"PURCHASE_REQUEST_LINE","version":8}],"floor":1,"from":3,"manifest_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","nonce":"waiver-1","organization_id":"11111111-1111-1111-1111-111111111111","originator_id":"88888888-8888-8888-8888-888888888888","policy_content_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","policy_version_id":"44444444-4444-4444-4444-444444444444","reference_id":"eeeeeeee-1111-1111-1111-111111111111","requested_at":"2026-09-13T11:59:00.0000000Z","requested_valid_to":"2026-10-01T12:00:00.0000000Z","requester_id":"77777777-7777-7777-7777-777777777777","subject_id":"22222222-2222-2222-2222-222222222222","subject_type":"PURCHASE_REQUEST","subject_version":3,"target_requirement_key":"QUOTES","to":1,"workload_subject_id":"99999999-9999-9999-9999-999999999999"}
```

SHA-256 binding: `4e03bdf6961a2576a4027d303b78931748883508e253dc6badb944a065fd22b5`.

```json
{"approver_id":"dddddddd-dddd-dddd-dddd-dddddddddddd","approver_role":"PROCUREMENT_APPROVER","authority_evidence_digest":"1aec4f5a01d0d2aa1bbd6e5e9a4e804fc2fb105e4581a26d99dc8cb42a78f1a0","authority_type":"PROCUREMENT","base_bundle_id":"33333333-3333-3333-3333-333333333333","base_result_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","binding":"4e03bdf6961a2576a4027d303b78931748883508e253dc6badb944a065fd22b5","canonicalization_version":"approval-canonical-json/v2","case_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","contract_version":"workflow-verification-request/v1","covered_lines":[{"id":"55555555-5555-5555-5555-555555555555","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"PURCHASE_REQUEST_LINE","version":7},{"id":"66666666-6666-6666-6666-666666666666","material_snapshot_digest":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","type":"PURCHASE_REQUEST_LINE","version":8}],"decision_scope":{"organization_id":"11111111-1111-1111-1111-111111111111","schema_version":"decision-scope/v1","scopes":[{"dimension":"ORGANIZATION","reference_id":null,"reference_version":null}]},"eligibility_evidence":"{\"evaluated_at\":\"2026-09-13T12:00:00.0000000Z\",\"user_id\":\"dddddddd-dddd-dddd-dddd-dddddddddddd\"}","eligibility_evidence_digest":"c6d702ed546d874fbd3a364b09865c85d9530119b3402f082603358397853b45","floor":1,"from":3,"manifest_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","nonce":"waiver-1","organization_id":"11111111-1111-1111-1111-111111111111","originator_id":"88888888-8888-8888-8888-888888888888","policy_content_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","policy_version_id":"44444444-4444-4444-4444-444444444444","reference_id":"eeeeeeee-1111-1111-1111-111111111111","requested_at":"2026-09-13T11:59:00.0000000Z","requested_valid_to":"2026-10-01T12:00:00.0000000Z","requester_id":"77777777-7777-7777-7777-777777777777","requirement_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","requirement_key":"WR-QUOTES","revoked_at":null,"segregation_satisfied":true,"subject_id":"22222222-2222-2222-2222-222222222222","subject_type":"PURCHASE_REQUEST","subject_version":3,"target_requirement_key":"QUOTES","to":1,"valid_from":"2026-09-13T12:00:00.0000000Z","valid_to":"2026-10-01T12:00:00.0000000Z","verifier_contract_version":"workflow-verification-response/v1","verifier_id":"approval-workflow","verifier_reference":"approval-exception://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","workflow_decision_digest":"11f973d785062fabb12e1305e7d6b9374386fd02215e83a5e0885ac424bc977a","workflow_decision_id":"cccccccc-cccc-cccc-cccc-cccccccccccc","workflow_decision_version":1,"workload_subject_id":"99999999-9999-9999-9999-999999999999"}
```

SHA-256 evidence: `43eba6c5443c410c6d12a5afc589734466e1f8c621c74561ab9ec64b1b3b3ce1`.

`binding` no sustituye ningún campo del request. Policy persiste la forma minimizada de `EligibilityEvidence` definida por SPEC 02; Workflow conserva y devuelve la forma completa. El adapter solo usa snapshots confirmados. Eventos y wire rechazan payload legacy, propiedades extra y fallback de versión.

## Migración, despliegue y reversión

- La migración agrega `PolicyExceptionRequest` y `PolicyExceptionVerification` al schema Approval con FKs, índices y constraints de REQ-08; no altera filas ni preimages v2 existentes. Policy agrega su proyección material derivada al bundle nuevo sin reinterpretar bundles históricos.
- El preflight exige SPEC 03 en `approval-canonical-json/v2`, owners exact-one y cero submission de excepción previa. Primero se despliega `decision-scope/v1`, target material y wire completo en Policy manteniendo verifier default-deny y deshabilitando la ruta legacy de usuario; después se despliega Workflow/verifier; finalmente se registran adapter y owners y se habilitan submissions. Nunca existe una ventana fail-open.
- Bundles históricos con token legacy o sin material snapshot no se someten al workflow. Deben reevaluarse con nueva key; no se mutan ni reciben una migración inferida.
- Health distingue adapter u owner ausente/ambiguo, contrato incompatible, credencial inválida, timeout y verifier no disponible sin exponer bindings o PII.
- Rollback deshabilita adapter/verifier real y restaura default-deny; conserva requests, verificaciones, casos, decisiones, revocaciones y evaluaciones. Tras persistir una extensión no se vuelve a una versión incapaz de leerla: se mantiene Workflow compatible y se corrige hacia adelante.
- Antes de habilitar tráfico se ejecuta `PolicyApprovalWorkflowContractE2ETests` sobre SQL Server efímero atravesando Policy→adapter→caso/decisión→endpoint→Policy, además de token legacy, binding adulterado, nonce/decisión repetidos, owner ausente, expiración y revocación.

## Seguridad y privacidad

- La llamada Policy→Workflow usa service JWT Bearer con audience dedicada, issuer/client allowlisted y secreto/certificado fuera de base y logs.
- Ningún campo del request autocertifica approver, authority o SoD; todos provienen de la decisión y `EligibilityEvidence` persistidas.
- Requester, originator y workload subject personal quedan excluidos tanto en assignment como en verificación; `ADMIN` no puede crear ni verificar una excepción.
- Comparación de digests, ids, scopes, líneas y binding es exacta y fail-closed; diferencias de tiempo no revelan existencia fuera del scope.
- Logs y trazas usan correlation, versiones y resultado, sin token, binding completo, nonce, motivos, snapshots ni identidad personal innecesaria.

## Requisitos no funcionales

- **NFR-01 — Compatibilidad contractual estricta.** Policy y Workflow pasan pruebas bidireccionales con las mismas fixtures y versiones; payload legacy o incompleto nunca se acepta parcialmente.
- **NFR-02 — Determinismo reproducible.** El mismo bundle, decisión, binding y reloj produce bytes/digests/verificación idénticos; replay devuelve evidencia original.
- **NFR-03 — Disponibilidad segura.** Timeout, auth inválida, adapter ambiguo, JSON malformado, revocación o dependencia ausente conservan el control y no generan aprobación.
- **NFR-04 — Observabilidad minimizada.** Métricas, logs, trazas y health distinguen submission, mapping, verificación, rechazo, replay y drift sin secretos ni PII.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | Mapping/owners, target material, scopes JSON, DAG, preimages, goldens, nonce y nulabilidad wire | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | Bundle/snapshot persistidos, exact-one, requests/verificaciones SQL, carreras, expiración y revocación | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Testcontainers |
| API/E2E | JWT service, Problem Details, timeout, schema estricto, vigencia y replay | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` |
| Contrato cross-module | `PolicyApprovalWorkflowContractE2ETests`: bundle persistido → adapter real → caso/DAG → decisión persistida → endpoint real → reevaluación Policy | Suite API/E2E sobre SQL Server efímero, sin verifier controlado |
| Migración/operación | Bundles legacy/sin target, preflight owners, default-deny→real, health y rollback | Integración SQL y procedimiento automatizado |

Todos los criterios se verifican automáticamente. El test cross-module usa el endpoint HTTP y autenticación reales, comprueba bindings/targets/versiones y repite tras expiración, revocación y conflicto; un test con `ControlledWaiverVerifier` no satisface esa evidencia. Casos negativos adicionales: bundle stale/BLOCKED, token scope legacy, control desconocido, owner ausente, edge de otra versión, usuario/ADMIN iniciador, autoaprobación, binding/digest adulterado, nonce o decisión reutilizados, response incompleto y timeout.

## Decisiones

- **DEC-01 — Adapter cerrado, no reinterpretación.** Cada efecto de SPEC 02 tiene una proyección y owner versionado explícitos; un control, snapshot u owner desconocido/incompleto bloquea.
- **DEC-02 — Corregir drift antes de integrar.** `decision-scope/v1`, targets versionados, wire completo y prohibición de inicio por `ADMIN` ya derivan de SPEC 02; esta entrega cierra su implementación sin fallback legacy.
- **DEC-03 — Materialidad desde evidencia Policy.** Todos los nodos apuntan a `PURCHASE_REQUEST_LINE`; el digest se deriva del snapshot y manifest ya persistidos, no de UUIDs sueltos ni una nueva consulta mutable.
- **DEC-04 — Dependencias, no fases implícitas.** La secuencia funcional se materializa como edges por target; `stage_code` solo etiqueta.
- **DEC-05 — Extensión enlazada, no nueva decisión.** `PolicyExceptionRequest` extiende un requirement ordinario uno-a-uno; assignment, acción y `workflow_decision_digest` siguen siendo los v2 de SPEC 03.
- **DEC-06 — Waiver ligado a una evaluación exacta.** La excepción cubre bundle, control, líneas, reducción y vigencia; no es reutilizable ni admite carry-forward.
- **DEC-07 — Verificación desde persistencia.** Workflow obtiene approver, authority, SoD y validez de su decisión; se descartan hints autocertificados y confianza en un binding opaco.
- **DEC-08 — Canonicalización Approval v2.** La integración reutiliza los digests v2 existentes y agrega preimages explícitos; no crea ni reetiqueta evidencia Approval v1.
- **DEC-09 — Rollback a default-deny.** Una incompatibilidad deshabilita integración y conserva controles, nunca activa un bypass temporal.

## Plan de implementación

### Bloque 1 — Frontera Policy→Workflow

- **T-01 — Cerrar contratos productores.** Implementar `decision-scope/v1`, targets materiales desde snapshot, wire estricto/service JWT y eliminar la iniciación legacy por usuario. Añadir unitarias de schema/goldens y API negativas de `ADMIN`, payload y auth. Cubre: REQ-01, REQ-02, REQ-07, NFR-01, NFR-02, NFR-03, CA-01, CA-06, CA-07.
- **T-02 — Adapter y mapping.** Implementar lookup de bundle, exact-one, idempotencia, owners versionados, tabla completa de efectos y errores fail-closed. Añadir unitarias de matriz y SQL de replay/owner ausente. Cubre: REQ-01, REQ-03, NFR-01, NFR-03, NFR-04, CA-01, CA-02, CA-03.

**Resultado verificable:** un bundle canónico crea una sola submission y cualquier bundle/control/target/owner legacy, corrupto o desconocido queda bloqueado con la evidencia del bloque.

### Bloque 2 — Secuencia y waiver

- **T-03 — DAG de controles.** Construir requirements/prerequisites, partición de targets y secuencia por edges completos. Añadir matriz unitaria e integración multi-línea con señales owner. Cubre: REQ-03, REQ-04, CA-03, CA-04.
- **T-04 — Extensión de quotation waiver.** Migrar/persistir `PolicyExceptionRequest`, crear atómicamente caso/requirement/extensión, exclusiones, authority, vigencia y prohibición de carry-forward. Añadir integración SQL de unicidad, carrera y rollback. Cubre: REQ-05, REQ-08, NFR-02, NFR-03, CA-05, CA-07, CA-08.

**Resultado verificable:** approvals y controles automáticos quedan ordenados por target y una reducción solo puede solicitarse mediante una extensión persistida con binding completo.

### Bloque 3 — Verifier y operación

- **T-05 — Endpoint y evidencia.** Implementar service auth, lookup/comparación server-side, `PolicyExceptionVerification`, response completo, replay, expiración y revocación. Añadir contrato HTTP y fault injection para cada status. Cubre: REQ-06, REQ-07, REQ-08, NFR-01, NFR-02, NFR-03, NFR-04, CA-05, CA-06, CA-07, CA-08.
- **T-06 — Recorrido real y rollout.** Implementar `PolicyApprovalWorkflowContractE2ETests` sin doubles, telemetría/health, preflight, despliegue y rollback default-deny. Cubre: REQ-05, REQ-06, REQ-07, REQ-08, NFR-01, NFR-03, NFR-04, CA-05, CA-06, CA-08.

**Resultado verificable:** las suites por bloque y el E2E nombrado demuestran el recorrido Policy↔Workflow; no basta que pase un flujo Policy aislado con verifier controlado.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-07, NFR-01 | Policy produce `decision-scope/v1` y target material exactos y wire completo; token, JSON extra, payload omitido o línea sin ref/snapshot falla. Bundle vigente crea una vez; replay devuelve caso y otra carga da `409`. | Automática: fixtures/goldens compartidas, regresión de drift y SQL/API. |
| CA-02 | REQ-01, REQ-03 | Bundle `BLOCKED`, stale, corrupto o de otra versión no crea caso. Cada efecto sigue la tabla; tipo/parámetro/target desconocido falla. Owner ausente/ambiguo da `503` sin caso; owner exacto deja prerequisite `WAITING`. | Automática: matriz efecto/parámetro/owner y adapter controlado. |
| CA-03 | REQ-03 | Cada prerequisite conserva parámetros, target material y `owner adapter/version + issuer/client_id`; impide completion hasta señal exacta y ningún control automático se convierte en task humana. | Automática: contrato de mapping, registry y signals. |
| CA-04 | REQ-04 | Department precede; Finance/IT/Legal paralelos; Procurement/PRE_PO esperan controles; edges comparan type/id/version/digest y no se habilitan por coincidencia parcial. | Automática: DAG por target y escenarios multi-línea. |
| CA-05 | REQ-05, REQ-06 | Solo workload crea atómicamente caso/requirement/`PolicyExceptionRequest`; usuario o `ADMIN` recibe `403`. Waiver exige role/authority, vigencia, cobertura y exclusiones exactas; lookup rechaza autoaprobación, otra evaluación o decisión no vigente. | Automática: dominio, SQL, auth y contrato HTTP versionado. |
| CA-06 | REQ-06, REQ-07, NFR-01, NFR-03 | Request/response exactos interoperan y Policy usa solo evidencia devuelta. Incompleto/malformado/5xx es dependencia indisponible; `404/409/422`/false no reduce. `PolicyApprovalWorkflowContractE2ETests` atraviesa endpoint, decisión y reevaluación reales. | Automática: E2E nombrado y HTTP fault injection. |
| CA-07 | REQ-08, NFR-02 | Bytes y SHA-256 de los tres vectores propios, más los dos vectores v2 de SPEC 03, coinciden; replay concurrente idéntico recupera una fila y nonce o decisión con otro payload da `409` sin duplicar. | Automática: goldens independientes y constraints SQL. |
| CA-08 | REQ-05, REQ-08, NFR-03, NFR-04 | La extensión `POLICY_EXCEPTION` nunca hace carry-forward; expiración/revocación invalida verificaciones futuras, rollout/rollback mantiene default-deny y telemetría no filtra bindings, nonce, tokens o PII. | Automática: integración SPEC 04, E2E real, health y captura de trazas. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Drift de scope persiste | Policy emite `ORGANIZATION`/`COST_CENTER` en texto | Regresión explícita y CA-01. |
| Wire omite bindings | Verifier decide solo por digest opaco o hints | Contrato exacto, bytes golden y CA-06/CA-07. |
| Target material no reproducible | UUID/version coinciden pero cambia snapshot o se vuelve a consultar provider | Preimage desde bundle persistido y CA-01. |
| Owner inexistente se interpreta como espera | Caso parcial persiste sin workload autorizado para señalar | Resolución exact-one antes del caso y CA-02/CA-03. |
| Control se pierde en mapping | Caso completa con prerequisite no señalado | Tabla cerrada y CA-02/CA-03. |
| Secuencia incorrecta | Procurement inicia antes de controles previos | Edges completos y CA-04. |
| Evidencia autocertificada | Request modifica approver o SoD | Lookup server-side y CA-05. |
| Waiver reutilizable | Misma decisión reduce otro bundle/target | Nonce, binding completo y CA-07. |
| Rollout crea ventana fail-open | Verifier parcial reduce control | Orden default-deny y CA-08. |
