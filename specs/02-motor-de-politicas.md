# SPEC 02 — Motor de políticas de compras

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** Lista para integrar
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** 63fec425400bc197524e23cb3a130ab2d7d64f6eba823944b3ebee8923e6d688
> **Fecha:** 2026-09-08
> **Actualizada:** 2026-09-09
> **Aprobada el:** 2026-09-09
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Proveer un Motor de políticas configurable, versionado y determinista que evalúe snapshots de una compra y produzca controles reproducibles sin crear ni decidir tareas de aprobación.
> **Depende de:** SPEC 01
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

La fuente funcional `procure-to-pay-management-platform-v2.md` establece que una compra no puede gobernarse solo por importe: el resultado también depende de tipo de compra, categoría de gasto, Department, Cost Center, proveedor, riesgo de datos, necesidad contractual y condición de proveedor preferido. Las políticas deben operar en scopes `LINE`, `REQUEST` y `SOURCING_PO`, acumular controles compatibles, conservar versión e inputs y reevaluarse ante cambios materiales.

La SPEC 01 ya implementó organización, perfiles, roles, Approval Authority, scopes y un resolver de elegibilidad que recibe un requisito concreto. Todavía no existen Purchase Requests, Cost Centers, Supplier Master, sourcing ni Approval Workflow. Esta SPEC crea la frontera independiente que transforma hechos tipados y versionados en requisitos; los dominios posteriores aportarán esos hechos y el Approval Workflow convertirá los requisitos en tareas, verificará excepciones y persistirá decisiones empresariales.

## Alcance

### Incluye

- Administración de conjuntos de políticas por `ADMIN` organizacional, con borradores, publicación, vigencia, retiro y auditoría.
- Modelo declarativo de reglas tipadas y catálogo cerrado de condiciones, operadores y efectos de Release 1.
- Evaluaciones en scopes `LINE`, `REQUEST` y `SOURCING_PO` sobre snapshots inmutables aportados por el caller.
- Selección inequívoca de la versión vigente según el reloj del servidor y bloqueo seguro ante configuración ausente o ambigua.
- Combinación acumulativa y determinista de reglas coincidentes, con precedencia del control más estricto.
- Generación de descriptores de aprobación, revisión, presupuesto, Procurement, sourcing, PO, compra directa, documentos de soporte, proveedor activo y bloqueo.
- Agregación del total bruto en moneda base de todas las líneas de una Purchase Request para impedir reducir controles dividiendo el importe entre líneas.
- Persistencia append-only de cada evaluación exitosa, su input canónico, reglas coincidentes, requisitos resultantes, versión de política y relación con evaluaciones anteriores.
- Reevaluación idempotente y cálculo de qué requisitos fueron agregados, endurecidos, retirados o permanecen equivalentes.
- Contrato fail-closed para aplicar únicamente excepciones ya aprobadas y verificables por el futuro Approval Workflow.
- API versionada y puerto de aplicación para administrar, simular y ejecutar evaluaciones sin imponer nombres concretos de rutas.
- Errores, seguridad, observabilidad, migración y pruebas de esta capacidad.

### No incluye

- Creación, asignación, agrupación, estado o decisión de Approval Requirements y Approval Tasks.
- Acciones `APPROVE`, `REJECT`, `REQUEST_CHANGES` o `DELEGATE`.
- Selección de una persona elegible; el consumo del resolver de elegibilidad de SPEC 01 pertenece al Approval Workflow.
- Autorización de excepciones, quotation waivers o bypasses; el motor solo verifica una referencia de aprobación emitida por el workflow futuro.
- Purchase Requests, Purchase Request Lines, Cost Centers, budgets, Supplier Master, RFQ, quotations, awards, POs, invoices, matching o payments.
- Ejecución de budget checks, reservas, sourcing, emisión de PO o validación material de documentos de soporte.
- Un editor visual, una DSL genérica, scripts, expresiones arbitrarias o extensiones ejecutables cargadas por usuarios.
- Los importes ilustrativos `LOW_VALUE`, `STANDARD`, `CONTROLLED` y `HIGH_VALUE` de la fuente funcional como configuración por defecto.
- Detección de fraccionamiento entre Purchase Requests distintas; Release 1 de esta capacidad agrega todas las líneas de una misma solicitud.
- Matching policies, tolerancias de facturas o reglas de pago.
- Interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Ciclo administrativo de políticas.** Solo un usuario activo con `ADMIN` de scope `ORGANIZATION` puede crear un `PolicySetVersion` en `DRAFT`, modificar sus metadatos y reglas con control de versión, validarlo, publicarlo o retirarlo, siempre con motivo. Publicar crea de forma atómica una versión de contenido inmutable, su digest y un `PolicyActivationRecord` append-only con `effective_from`. Retirar no modifica ese contenido: agrega un `PolicyRetirementRecord` inmutable con el primer `effective_to` de la activación. Un retiro no se cancela ni reactiva la versión; corregir, revertir o cubrir un hueco exige publicar otra versión.

- **REQ-02 — Publicación segura y vigencia inequívoca.** Una activación empieza en `effective_from` inclusivo y, si tiene retiro, termina en `effective_to` exclusivo, ambos UTC. Al publicar o retirar, esos instantes no pueden ser anteriores al reloj del servidor; `effective_to` debe ser posterior a `effective_from`. No pueden existir intervalos de activación superpuestos para la organización. Publicar un sucesor de una versión abierta agrega, en la misma transacción, el retiro de la anterior exactamente en el `effective_from` de la nueva. La publicación valida códigos únicos, operadores y efectos compatibles, referencias internas y externas, y una regla fallback explícita por cada scope habilitado. El fallback tiene condición `DEFAULT` y efecto `ALLOW` o `BLOCK`: solo se evalúa cuando ninguna regla no-default del mismo scope coincide. No participa en la acumulación cuando existe otra coincidencia ni concede permisos por ausencia de configuración.

- **REQ-03 — Selección fail-closed.** Una evaluación ordinaria usa el reloj del servidor. Si existen cero activaciones vigentes responde `409 /problems/policy-configuration-unavailable`. Si existen varias por corrupción, falla la carga o el digest no coincide, responde `503 /problems/policy-dependency-unavailable`. En ambos casos no usa defaults embebidos/última versión conocida, no persiste bundle empresarial y emite diagnóstico correlacionable sin reglas sensibles.

- **REQ-04 — Lenguaje declarativo tipado.** Una regla pertenece a un único scope y contiene código estable, descripción administrativa, conjunción de predicados tipados y uno o más efectos tipados. Los facts y tipos de Release 1 son: `gross_amount_base: MoneyBase`; `purchase_type: GOOD|SERVICE|SUBSCRIPTION`; `spend_category: VersionedCodeRef`; `beneficiary_department`, `cost_center`, `cost_center_department`, `supplier`, `preferred_product` y `required_product: VersionedEntityRef|null`; `cost_center_active` y `cost_center_department_active: bool`; `preferred_supplier`, `contract_required` y `non_standard_terms: bool`; `external_agreement_status: NONE|ACTIVE|INACTIVE|EXPIRED`; `data_risk: VersionedCodeRef`; y `risk_answer: TypedAnswerRef` con código de pregunta, versión de schema y valor booleano o código enumerado. `cost_center_department` identifica al Department propietario atestiguado por la versión del Cost Center; `preferred_product` y `required_product` no pueden coexistir. Los operadores permitidos son `EQ/NEQ/IN/NOT_IN` para enums y referencias, `IS_TRUE/IS_FALSE` para booleanos, y `GT/GTE/LT/LTE/BETWEEN` para dinero; `BETWEEN` declara inclusividad de cada extremo. Una disyunción se expresa mediante reglas separadas. No se ejecutan código, reflection, SQL, plantillas ni funciones aportadas por usuarios.

- **REQ-05 — Fuentes confiables y snapshots completos.** Una ejecución empresarial recibe solo `FactRequest(subject_type, subject_id, subject_version, operation, requested_at_utc, workload_principal, correlation_reference)`. Un registry in-process debe resolver exactamente un `IPolicyFactProvider` por `subject_type + operation`, con `provider_id` estable y versión de contrato. Fact provider y `IPolicyReferenceCatalog` comparten un timeout máximo de 5 segundos cada uno y respetan cancelación. `GetFactsAsync` devuelve `PolicyFactBundle` o un error tipado; no acepta bundles por HTTP ni firmas autocertificadas. El bundle incluye organización, Legal Entity, moneda base, facts/provenance y `CompletenessManifest`, cuyo digest recalcula el motor. Para `REQUEST`, el manifiesto declara request id/version y conteo/conjunto completo de line ids/versiones. Para `SOURCING_PO`, declara sourcing/PO proposal id/version, request id/version, conjunto exacto de line ids/versiones, current request evaluation bundle id/result digest, proveedor/award/términos versionados. Un registry de catálogos resuelve exactamente un `IPolicyReferenceCatalog` por `catalog` de `VersionedCodeRef`, `entity type` de `VersionedEntityRef` o `question schema`; cada registro tiene `resolver_id` y `contract_version` estables. Valida publicación/evaluación, relación Cost Center→Department y estado activo. Cero o múltiples fact providers/resolvers, timeout o fallo responden `503 /problems/policy-dependency-unavailable`; bundle inválido/adulterado responde `400`; no se persiste resultado parcial. Solo simulación `ADMIN` acepta snapshot directo y no empresarial. Descripciones, documentos, tokens y PII quedan fuera.

- **REQ-06 — Evaluación exhaustiva y composite.** `EvaluatePurchaseRequest` crea un `PolicyEvaluationBundle` raíz: contiene una `ScopeEvaluation(scope=LINE)` por cada line id/version del manifest y una `ScopeEvaluation(scope=REQUEST)` sobre el conjunto/agregado; todas usan la misma policy version, fact bundle y transacción. Después produce un `CombinedEvaluationResult` aplicando el algoritmo cross-scope de REQ-07. `EvaluateSourcingPo` crea otro bundle con una única `ScopeEvaluation(scope=SOURCING_PO)` y referencia el bundle actual de request según REQ-12. Cada `ScopeEvaluation` conserva el `scope` singular exigido por la fuente. En cada scope se evalúan todas las reglas no-default coincidentes; el fallback solo se usa si no coincide ninguna. No hay first-match ni dependencia del orden de almacenamiento. Con los mismos inputs canónicos se obtienen los mismos resultados.

- **REQ-07 — Combinación por máxima exigencia.** Primero cada `ScopeEvaluation` combina efectos por `(requirement_key, type)`. Para combinar LINE→REQUEST, un control REQUEST se expande sobre cada line id/version del manifest; controles LINE ya cubren su línea. Por cada `(key, type, line)` se aplica la máxima exigencia y luego se reagrupan líneas solo cuando type, fase y todos los parámetros efectivos —incluidos role, authority y DecisionScope— son idénticos; el combined control conserva `origin_scopes` y origin rules como sets. `BLOCK` en cualquier scope bloquea el bundle; `REQUIRE_PO` vence a direct purchase; cotizaciones usa máximo; documentos se unen; budget/Procurement/supplier/reviews se conservan. Approval con la misma key exige role, authority type y fase iguales al publicar y combina mayor rank/importe por línea. Una misma key con tipos/roles/fases incompatibles impide publicar. Keys distintas permanecen separadas. El combined result es `BLOCKED` si algún hijo lo está, `REQUIREMENTS_GENERATED` si queda cualquier control y `PASSED` solo si todos los hijos pasan sin controles.

- **REQ-08 — Contrato de controles generados.** Cada control de `ScopeEvaluation` contiene `requirement_key`, tipo, scope singular, líneas/sujeto, fase, parámetros, reglas/revisiones y motivos; un combined control sustituye el scope singular por el set `origin_scopes`. `REQUIRE_APPROVAL` usa exactamente uno de dos contratos: (a) `AuthorityRequirementKind.NONE`, permitido solo para IT/Legal, con authority type, level id/version/code/rank, amount y currency obligatoriamente `null`; o (b) `REQUIRED`, con type, `AuthorityLevelSnapshot(id, version, code, rank)`, amount/currency y `DecisionScopeDescriptor`, todo validado contra SPEC 01. Department Approval usa el `cost_center_department` activo y versionado atestiguado por `IPolicyReferenceCatalog`, no `beneficiary_department`; conserva la relación Cost Center→Department y ambos facts. Hasta la SPEC de Cost Centers no invoca el resolver con scope `COST_CENTER`. El motor nunca elige usuario ni crea tarea.

- **REQ-09 — Resultado de evaluación.** Cada `ScopeEvaluation` y el `CombinedEvaluationResult` termina en `PASSED`, `REQUIREMENTS_GENERATED` o `BLOCKED`. `PASSED` exige un `ALLOW` de regla coincidente o fallback y cero controles; requirements produce controles no bloqueantes; `BLOCKED` exige `BLOCK` coincidente o excepción no verificable. El bundle incluye id, secuencia monotónica por subject/version, operation, actor, UTC, policy version/digest, fact/manifest digests y contenido canónico, scope evaluations, reglas, resultado combinado, controles, exceptions y correlation. Un bloqueo empresarial se persiste con razones tipadas; un error técnico de provider/configuración no crea bundle.

- **REQ-10 — Scope `LINE` y riesgo.** La evaluación por línea puede generar controles distintos para líneas de la misma solicitud a partir de Purchase Type, Spend Category, Department, Cost Center, proveedor y riesgo. Un importe bajo no elimina IT o Legal Review cuando los hechos de riesgo o contrato las exigen. Cada requisito identifica exactamente las líneas cubiertas; el motor no agrupa líneas en una Approval Task.

- **REQ-11 — Scope `REQUEST` y antifraccionamiento intradocumento.** El motor calcula por sí mismo el total bruto de todas las líneas declaradas por el `CompletenessManifest`, comprueba que pertenezcan a la misma organización, Legal Entity, moneda base y versión de solicitud, y evalúa controles financieros contra ese agregado además de cada resultado `LINE`. El requisito más estricto entre niveles prevalece para las líneas cubiertas. El servicio solicitante no proporciona el total agregado ni puede escoger un subconjunto; la futura SPEC de Purchase Requests implementará el provider que atestigua completitud.

- **REQ-12 — Scope `SOURCING_PO` y actualidad.** El bundle de sourcing/PO referencia el bundle de request con mayor `evaluation_sequence` confirmado para el mismo request id/version y exacto conjunto orden-independiente de line ids/versiones. Sigue siendo actual aunque su policy version se retire; deja de serlo si existe un bundle posterior para ese subject/version o si cualquier id, versión o manifest/result digest difiere. La evaluación puede exigir Procurement, cotizaciones válidas, proveedor activo, Procurement Approval, PO o documentos. Un proveedor/acuerdo preferido solo reduce cotizaciones si una regla lo permite; nunca elimina controles no reducibles. Son cambios materiales: importe o moneda; Cost Center o Beneficiary Department; Spend Category o Purchase Type; proveedor; Preferred/Required Product; respuestas de riesgo; condiciones de pago/contrato; y cantidad, precio o alcance de PO. Cualquiera exige nueva versión del sujeto y reevaluación; una policy version nueva no reinterpreta por sí sola compras sin cambios.

- **REQ-13 — Idempotencia, historia y reevaluación.** `workload_principal` es la pareja estable `issuer + client_id` de un service JWT allowlisted; rotar secreto/certificado no cambia la identidad. Para llamadas internas usa `issuer=internal://procure-to-pay` y `client_id` igual al código allowlisted del adapter registrado. `evaluation_key` tiene 1–128 caracteres `[A-Za-z0-9._:-]` y es única en `(organization_id, issuer, client_id, operation)`. El `idempotency_fingerprint` es SHA-256 canónico del comando recibido: operation, subject type/id/version, previous bundle id/result digest, cause y exception reference ids/target keys/requested reductions; excluye reloj, correlation y datos que solo obtiene el motor (provider, manifest y policy seleccionada). Al encontrar la key, el motor recalcula ese fingerprint sin llamar dependencias: si coincide devuelve el bundle original; si difiere responde `409`. Solo una key nueva consulta providers y crea una evaluación. Una reevaluación referencia la anterior y declara causa `MATERIAL_FACT_CHANGE`, `POLICY_VERSION_CHANGE` o `APPROVED_EXCEPTION`; conserva ambas y calcula un diff estable `ADDED`, `HARDENED`, `REMOVED` o `UNCHANGED`. Un control es equivalente solo si coinciden key, tipo, sujetos, fase y parámetros efectivos; cualquier aumento de authority, importe, cotizaciones, documentos o restricciones es `HARDENED`. El motor no marca tareas como `SUPERSEDED` ni ajusta presupuesto.

- **REQ-14 — Protocolo de excepción tipada.** En esta entrega solo `REQUIRE_QUOTATIONS` admite `REDUCE_MIN_VALID_QUOTATIONS(from, to)`, con `1 <= to < from`. Al combinar efectos con la misma key, si alguno es `NOT_EXCEPTIONABLE`, el control combinado no admite excepción; si todos tienen allowance, `minimum_allowed` efectivo es el máximo de sus floors, el AuthorityLevelSnapshot efectivo es el de mayor rank y una única evidencia debe cubrir la unión de scopes/líneas. Role debe ser `PROCUREMENT_APPROVER` y authority type `PROCUREMENT`. Ningún otro efecto es excepcionable; `MATCH_EXCEPTION` pertenece a matching y no autoriza quotation waiver. Ningún otro efecto es excepcionable; `MATCH_EXCEPTION` pertenece a matching y no autoriza quotation waiver. El motor envía a `IApprovedPolicyExceptionVerifier`, con timeout máximo de 5 segundos y cancelación, reference id, workflow decision id/version, base bundle id/result digest, policy version/digest, subject/manifest digest, target key, from/to, requester id, instante, nonce y correlation. Un registry exige exactamente un verifier in-process para el tipo `APPROVAL_WORKFLOW`, con `verifier_id`/`contract_version` estables y timeout/cancelación; mientras no exista workflow se registra un único verifier default-deny. Nunca se acepta una respuesta del request HTTP. El verifier responde `VERIFIED` o un motivo tipado y, si verifica, entrega digest de decisión, approver id, EligibilityEvidence de SPEC 01, vigencia, scope y prueba `segregation_satisfied=true`. El motor verifica bindings, límites y authority, persiste ese snapshot y una clave única `(workflow_decision_id, base_bundle_id, target_key)`. Repetir el mismo binding es idempotente; usar la decisión en otra evaluación/key produce `BLOCKED`. Ausencia de verifier, digest/binding inválido, expiración, revocación, otro sujeto/versión, autoaprobación o evidencia insuficiente conserva el control y bloquea. SoD, presupuesto insuficiente y `BLOCK` de invariante nunca son excepcionables.

- **REQ-15 — Simulación separada de ejecución.** `ADMIN` puede simular un borrador o versión publicada con un snapshot de prueba. La respuesta se marca `SIMULATION`, no selecciona la versión ordinaria por vigencia, no se usa como requisito empresarial, no acepta excepciones y no se almacena como `PolicyEvaluationBundle`; sí genera evidencia administrativa minimizada de actor, versión probada, instante y hash del input. Usuarios de negocio y servicios ordinarios no pueden invocar simulaciones.

- **REQ-16 — Autorización, lectura y auditoría.** Administración y simulación exigen `ADMIN` organizacional. Un `AUDITOR` activo con scope `ORGANIZATION` puede consultar versiones, reglas, evaluaciones y evidencia en modo read-only. La ejecución empresarial exige un workload identity allowlisted y autenticado; el registro conserva `requested_by_actor_type`, id del workload y, cuando aplique, id del usuario originador aportado por el fact provider. Nunca se confía en un perfil `PENDING_SETUP`/`INACTIVE` ni en claims empresariales del IdP. Crear/editar/publicar/retirar/simular escribe evidencia append-only con actor, UTC, motivo, before/after o hash, versión y correlation reference en la misma transacción administrativa.

- **REQ-17 — Errores y límites.** Problem Details: `401` autenticación; `403` permiso; `404` no visible; `400` forma/facts/manifest; `409` versión, idempotencia o cero políticas vigentes; `422` otra invariante; `503 /problems/policy-dependency-unavailable` para múltiples políticas vigentes, corrupción/digest, timeout o ausencia/fallo de registry/provider/catalog/verifier. El verifier default-deny disponible responde negocio `BLOCKED`, no `503`. Exceder 500 líneas/request, 2.000 rules/policy, 32 predicates o 16 effects/rule, 256 risk answers/line, 5 MiB snapshot o 10 MiB policy produce `413`. Policy rule codes y `requirement_key` usan 1–64 ASCII `[A-Z][A-Z0-9_]*`; `evaluation_key` usa REQ-13; descripción máximo 500 y motivo 1–1.000 Unicode scalars; provenance máximo 4 KiB/ref e incluido en total. Se valida antes de persistir y sin filtrar contenido.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `PolicySetVersion` | id, organización, secuencia, estado, scopes, contenido canónico, digest, row version | `DRAFT` es editable; el contenido `PUBLISHED` es inmutable. |
| `PolicyActivationRecord` | policy version, `effective_from`, actor, motivo, UTC | Append-only; activa una versión publicada. |
| `PolicyRetirementRecord` | activation id, `effective_to`, actor, motivo, UTC | Append-only, máximo uno por activación; no se cancela ni reabre. |
| `PolicyRule` | id, código estable, scope, descripción, predicados, efectos, revisión | Código único dentro de la versión; predicados con AND; OR requiere otra regla. |
| `PolicyPredicate` | fact key tipada, operador, valor/rango | Catálogo de REQ-04 y compatibilidad tipo-operador validada antes de publicar. |
| `PolicyEffect` | tipo, `requirement_key`, parámetros, fase, exception allowance | Catálogo cerrado; incompatibilidades impiden publicar. |
| `MoneyBase` | amount decimal exacto, ISO 4217 currency | No negativo, sin coma flotante; currency igual a moneda base del bundle. |
| `VersionedCodeRef` | catálogo, código, versión/digest | Código no vacío y existencia verificada contra el provider del catálogo al publicar/evaluar. |
| `VersionedEntityRef` | tipo, id, versión | Identidad estable y versión positiva atestiguada por su dominio propietario. |
| `TypedAnswerRef` | question code, schema version, value kind, bool o enum code | Exactamente un valor del tipo declarado por el schema versionado. |
| `WorkloadPrincipal` | issuer, client_id | Pareja autenticada/allowlisted y estable ante rotación de credencial. |
| `FactRequest` | subject type/id/version, operation, server UTC, workload, correlation | Única entrada empresarial del caller; no contiene facts. |
| `PolicyFactProviderRegistration` | subject type, operation, provider id, contract version | Exactamente uno por combinación; adapter in-process, timeout 5 s. |
| `PolicyReferenceCatalogRegistration` | reference catalog/entity/question type, resolver id, contract version | Exactamente uno por tipo consultado; timeout 5 s. |
| `PolicyExceptionVerifierRegistration` | `APPROVAL_WORKFLOW`, verifier id, contract version | Exactamente uno; default-deny hasta integrar workflow; timeout 5 s. |
| `CompletenessManifest` | subject/version, line count/set, relaciones y source versions según operation | El motor recalcula digest y rechaza omisiones/versiones mezcladas. |
| `PolicyFactBundle` | subject ref, facts, provenance, completeness manifest/digest, provider id/version | Lo produce un adapter in-process registrado; el motor recalcula su digest. |
| `PolicyInputSnapshot` | sujeto/versiones, organización, Legal Entity, facts, importes, moneda y manifests | Canónico, inmutable y minimizado; contenido y hash se conservan. |
| `PolicyEvaluationBundle` | id/sequence, `evaluation_key`, `idempotency_fingerprint`, caller/operation, actor, policy version/digest, UTC, fact manifest, scope evaluations, combined result, previa, diff, exceptions, correlation | Raíz append-only; índice único por scope de REQ-13. |
| `ScopeEvaluation` | scope singular, subject/lines, matched/fallback rule, controls, result | Hijas LINE+REQUEST o única SOURCING_PO; inmutables. |
| `GeneratedControl` | key, tipo, fase, scope singular u `origin_scopes`, sujetos/líneas, parámetros, origen y motivo | Descriptor inmutable de scope o combined; no es task/decisión. |
| `ApprovalControlDescriptor` | role, authority level id/version/code/rank, amount/currency, decision scope | Snapshot compatible con `AuthorizationMatrix` y niveles de SPEC 01. |
| `ExceptionVerificationSnapshot` | verifier id/version, workflow decision/version/digest, bindings, nonce, approver, EligibilityEvidence, SoD, scope, vigencia | Resultado inmutable de adapter in-process; binding único por evaluación/control. |
| `PolicyAdministrativeAuditRecord` | actor, UTC, acción, versión, before/after o hash, motivo, correlation | Append-only y atómico con la mutación administrativa. |

Catálogo de scopes:

| Código | Unidad evaluada | Hechos obligatorios mínimos |
| --- | --- | --- |
| `LINE` | Una Purchase Request Line versionada | importes/FX, Purchase Type, Spend Category, Beneficiary Department, Cost Center, relación/versiones Cost Center→owner Department, productos preferred/required y facts de riesgo/contrato; supplier puede ser nulo. |
| `REQUEST` | Una versión completa de Purchase Request | manifest completo de una o más líneas, sus facts/versiones y total base calculable solo por el motor. |
| `SOURCING_PO` | Propuesta versionada para líneas de una PR | request id/version, conjunto exacto de líneas, current bundle/digest, supplier, quotations, preferencia/acuerdo, award, importes y términos. |

Catálogo inicial de efectos:

| Efecto | Parámetros principales | Combinación |
| --- | --- | --- |
| `ALLOW` | motivo | Solo produce `PASSED` si no queda otro control. |
| `BLOCK` | código y motivo | Prevalece sobre todos los efectos. |
| `REQUIRE_APPROVAL` | key, fase, role, authority, scope | Máxima authority por key compatible; keys distintas permanecen separadas. |
| `REQUIRE_BUDGET_CHECK` | key, líneas, Cost Centers, importe base | Se acumula; no ejecuta ni reserva presupuesto. |
| `REQUIRE_PROCUREMENT` | key, fase | Se conserva si cualquier regla lo exige. |
| `REQUIRE_QUOTATIONS` | key, mínimo válido, allowance opcional | Usa el máximo; único efecto reducible mediante `REDUCE_MIN_VALID_QUOTATIONS`. |
| `REQUIRE_PO` | key | Prevalece sobre compra directa. |
| `ALLOW_DIRECT_PURCHASE` | key | Solo aplica si no se exige PO ni existe bloqueo. |
| `REQUIRE_SUPPORTING_DOCUMENT` | key, tipos aceptados/mínimo | Une obligaciones sin aceptar un conjunto menos estricto. |
| `REQUIRE_ACTIVE_SUPPLIER` | key | Se conserva aunque Procurement no participe. |

Reglas monetarias y temporales:

- Los thresholds y Approval Authorities usan el total bruto esperado a pagar en moneda base: subtotal + impuestos + cargos adicionales - descuentos.
- Los importes no usan coma flotante binaria, no son negativos y conservan moneda/provenance de conversión. El motor no calcula FX; verifica y congela el equivalente recibido.
- `effective_from` es inclusivo y `effective_to` exclusivo. Persistencia y comparación usan UTC.
- Una regla monetaria declara inclusividad de cada límite; la publicación rechaza un rango vacío o invertido. Reglas distintas pueden solaparse porque sus controles deben acumularse.
- Los ejemplos `<= 500`, `501–5,000`, `5,001–20,000` y `> 20,000` no se cargan automáticamente.
- Los códigos de Purchase Type iniciales son `GOOD`, `SERVICE` y `SUBSCRIPTION`; referencias de dominios futuros son versionadas, nunca texto libre sin catálogo.

Canonicalización contractual `policy-canonical-json/v1`:

- Todo preimage es un objeto raíz cuya primera propiedad al ordenar es `canonicalization_version: "policy-canonical-json/v1"`; se serializa UTF-8 sin BOM, sin whitespace. Propiedades de objeto siempre presentes, ordenadas lexicográficamente ordinal; opcional ausente es `null`; campo desconocido se rechaza.
- Strings usan Unicode NFC; UUID formato `D` minúsculo; enums/códigos contractuales mayúsculos; booleanos JSON. Decimales son strings invariantes sin exponente, ceros iniciales/finales (`0`); timestamps UTC `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`.
- Son sets y se ordenan por bytes canónicos completos: scopes; rules por code/revision; predicates de una regla; effects por requirement key/type/parameters; line refs/facts por line id/version; risk answers por question/schema; matched rules; origin rules; target lines; document types; generated controls por key/type/scope/subjects; exception targets/digests. Duplicados se rechazan. Solo audit subchanges y la cadena previous→new son listas ordenadas y conservan su orden declarado.
- `policy_content_digest` hashea `{canonicalization_version, policy_schema_version, organization_id, scopes, rules}`; excluye ids de persistencia, estado, activación y audit.
- `fact_manifest_digest` hashea `{canonicalization_version, provider_id, provider_contract_version, subject_ref, organization_id, legal_entity_id, base_currency, facts, provenance, completeness_manifest}`.
- `exception_verification_digest` hashea `{canonicalization_version, verifier_id, verifier_contract_version, workflow_decision, bindings, nonce, authority_evidence, segregation_satisfied, scope, validity}`.
- `evaluation_input_digest` hashea `{canonicalization_version, evaluated_at_utc, operation, subject_ref, policy_content_digest, activation_id, fact_manifest_digest, previous_bundle_id, previous_result_digest, exception_verification_digests}`.
- `evaluation_result_digest` hashea `{canonicalization_version, evaluation_input_digest, scope_evaluations, combined_controls, combined_result, diff}`. El fingerprint de idempotencia usa exactamente el comando canónico de REQ-13 y por diseño no incluye `fact_manifest_digest` ni `evaluated_at_utc`.
- Cada digest es SHA-256 hexadecimal minúsculo sobre esos bytes. Cambiar schema, orden o preimage exige nueva `canonicalization_version`; los vectores golden forman parte de CA-03.

Contrato de fases para descriptores, sin crear secuencia ejecutable:

```text
DEPARTMENT
PRE_PROCUREMENT
PROCUREMENT
PRE_PO
```

El workflow posterior conservará el principio funcional: Department primero; Finance, IT y Legal pueden ser paralelos; Procurement inicia al completar controles previos; Procurement Approval ocurre antes de emitir PO cuando la política lo exige.

## Migración, despliegue y reversión

- La persistencia se añade bajo un schema explícito del módulo y no modifica ni elimina tablas de SPEC 01. Las referencias a organización y evidencia administrativa respetan sus identificadores y convenciones de concurrencia.
- La migración se aplica antes de exponer administración o evaluación. Tras desplegar, no existe una política activa por defecto: health/diagnóstico informa `POLICY_CONFIGURATION_REQUIRED` y las evaluaciones fallan cerrado hasta que `ADMIN` publique una versión explícita.
- El despliegue puede importar un borrador desde configuración versionada, pero nunca publicarlo implícitamente ni cargar los thresholds ilustrativos. La publicación sigue siendo una acción administrativa autenticada, motivada y auditada.
- Antes de habilitar callers empresariales se prueba una simulación y una evaluación controlada de la versión publicada; la configuración exacta pertenece a datos operativos, no al binario.
- Retirar una versión no borra evaluaciones previas. Para rollback se publica una nueva versión basada en una versión anterior y con nueva vigencia; no se altera la versión histórica ni su digest de contenido.
- Una reversión de aplicación conserva versiones, reglas, auditoría y evaluaciones. No se autoriza una migración descendente destructiva en entornos con historia; la recuperación es volver a una versión compatible y corregir hacia adelante.

## Seguridad y privacidad

- JWT identifica al actor; roles y scopes locales de SPEC 01 autorizan administración y lectura. Claims del IdP no sustituyen `ADMIN`/`AUDITOR` locales.
- El lenguaje de políticas no ejecuta contenido aportado por usuarios ni permite acceso a red, archivos, environment, base de datos o reflection.
- La API y los puertos aplican antes de evaluar los límites de REQ-17; configuración más restrictiva puede proteger infraestructura, pero no puede elevarlos sin una nueva revisión contractual.
- Snapshots y auditoría almacenan solo hechos necesarios para reproducir la decisión. No incluyen tokens, credenciales, adjuntos, descripciones de compra ni datos personales del requester.
- Las lecturas de evaluación exigen scope organizacional y no revelan hechos o reglas mediante diferencias entre `403` y `404` fuera del alcance visible.
- La `ApprovedPolicyExceptionReference` está ligada a evaluación, sujeto, versión, controles y vigencia; no es reutilizable para otra compra o reevaluación.
- Presupuesto insuficiente, SoD y bloqueos de invariantes no pueden declararse excepcionables desde configuración.

## Requisitos no funcionales

- **NFR-01 — Determinismo reproducible.** Reglas, manifests, snapshots, excepciones y controles usan `policy-canonical-json/v1` y SHA-256. Con el mismo input completo —incluido `evaluated_at_utc`— se obtienen bytes, resultado y digest idénticos; reintentos idempotentes en otro instante devuelven el bundle original en vez de recalcular.
- **NFR-02 — Atomicidad e inmutabilidad.** Publicación y audit administrativo se confirman o revierten juntas; una evaluación y todo su snapshot/resultado se persisten atómicamente; versiones publicadas y evaluaciones no pueden mutarse desde la capacidad.
- **NFR-03 — Concurrencia e idempotencia.** Publicación, edición de draft y retiro usan control optimista más restricción persistente de vigencias no solapadas. `evaluation_key` evita duplicados aun con reintentos concurrentes.
- **NFR-04 — Disponibilidad segura.** La ausencia, ambigüedad o carga incompleta de políticas nunca degrada a `ALLOW`; health diferencia falta de configuración de una indisponibilidad técnica sin exponer su contenido.
- **NFR-05 — Observabilidad minimizada.** Administración, selección de versión, evaluación, bloqueo, conflicto, reevaluación y verificación de excepción emiten métricas, logs estructurados y trazas OpenTelemetry con correlation reference, policy version, scope, resultado y duración, sin snapshots completos, tokens ni datos personales.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | Catálogo de facts/operadores/efectos, defaults, combinación, pipeline LINE→REQUEST, límites monetarios, `policy-canonical-json/v1`, SHA-256, diffs y excepción fail-closed | `dotnet test ProcureToPay.sln` con casos en `tests/ProcureToPay.UnitTests/` |
| Integración | Contenido/activación/retiro sobre SQL Server, publicación sucesora concurrente, auditoría/evaluación atómicas, key scoped, snapshots append-only y rollback por nueva versión | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | JWT, workload identity, `ADMIN`, `AUDITOR`, simulación, publicación/retiro, ejecución por referencia, Problem Details/413, configuración ausente y minimización | `dotnet test ProcureToPay.sln` con escenarios en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato futuro | Providers controlados de facts/catálogos, manifest completo, descriptores sin entidades futuras y verifier de excepción controlado/default-deny | Pruebas de contrato dentro de la solución; adapters reales se revalidarán en las SPECs de Purchase Requests y Approval Workflow |
| Migración/operación | Base existente de SPEC 01, migración aditiva, health `POLICY_CONFIGURATION_REQUIRED`, publicación explícita y reversión conservando historia | Prueba automatizada sobre SQL Server efímero y procedimiento operativo documentado |

Todos los criterios se verifican automáticamente. Los casos negativos incluyen: cero o múltiples versiones vigentes, snapshot incompleto, regla/tipo incompatible, límites monetarios, orden de reglas alterado, dos publicaciones concurrentes, reintento con payload distinto, `ADMIN` revocado, lectura no autorizada, referencia de excepción reutilizada o no verificable y fallo forzado entre dato y auditoría.

## Decisiones

- **DEC-01 — Motor separado del Approval Workflow.** Esta entrega termina en descriptores y evaluaciones; no crea requirements, tasks ni decisiones. Se descarta unir ambas capacidades porque configuración/evaluación aportan valor y evidencia verificable por separado y el workflow tiene estados, delegación y asignación propios.
- **DEC-02 — Reglas declarativas tipadas.** Release 1 usa catálogos cerrados de hechos, operadores y efectos. Se descartan DSL, scripts y código desplegable por regla por riesgo de seguridad, dificultad de validación y pérdida de determinismo.
- **DEC-03 — Sin política implícita.** Ausencia de una versión publicada vigente bloquea la evaluación. Se descartan `ALLOW` por defecto y thresholds embebidos; una baseline solo existe después de publicación administrativa explícita.
- **DEC-04 — Todas las coincidencias se acumulan.** No se usa first-match. Los controles compatibles se combinan y domina el más estricto, conforme a la fuente funcional.
- **DEC-05 — Fraccionamiento dentro de la solicitud.** El scope `REQUEST` suma todas las líneas de la versión completa. La detección entre solicitudes distintas se descarta en esta entrega porque la fuente no define ventana temporal, identidad de necesidad ni tratamiento de falsos positivos.
- **DEC-06 — Facts externos mediante providers confiables.** El motor no adelanta Purchase Requests, Cost Centers, Supplier Master ni sourcing. La evaluación empresarial recibe una referencia y obtiene un bundle completo/versionado de un provider interno; se descarta confiar en snapshots enviados por callers ordinarios. Simulación sigue admitiendo facts directos porque no produce evidencia empresarial.
- **DEC-07 — Excepción inicial limitada a quotation waiver.** El workflow posterior autoriza y prueba la excepción; el motor solo puede reducir el mínimo de cotizaciones dentro del allowance publicado y con `PROCUREMENT_APPROVER` + authority `PROCUREMENT`. Se descartan bypass de `ADMIN`, flags de input y reducción de approval, PO, budget, supplier, documentos, SoD o BLOCK; `MATCH_EXCEPTION` queda para matching.
- **DEC-08 — Historia por nuevas versiones.** Publicadas y evaluaciones son inmutables. Retiro, corrección, reevaluación y rollback crean historia enlazada en lugar de reinterpretar decisiones previas.
- **DEC-09 — Keys explícitas para aprobaciones múltiples.** La máxima autoridad se combina dentro de una misma `requirement_key`; keys distintas sobreviven como requisitos separados. Se descarta inferir secuencia adicional desde Job Title o cantidad de reglas coincidentes.

## Plan de implementación

### Bloque 1 — Contrato tipado y evaluación pura

- **T-01 — Modelo de configuración.** Implementar contenido, activación/retiro append-only, reglas, facts, predicados, efectos, defaults, niveles versionados y validaciones contra catálogos. Cubre: REQ-01, REQ-02, REQ-04, REQ-07, REQ-08, NFR-01, CA-01, CA-02, CA-04.
- **T-02 — Evaluador determinista.** Implementar `policy-canonical-json/v1`, SHA-256, pipeline LINE→REQUEST, `SOURCING_PO`, combinación estricta, actualidad, materialidad y resultados reproducibles. Cubre: REQ-05, REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, NFR-01, CA-03, CA-04, CA-05, CA-06, CA-07.

**Resultado verificable:** reglas puras producen el mismo resultado ante permutaciones de almacenamiento y aplican acumulación, thresholds y bloqueos sin crear tareas.

### Bloque 2 — Persistencia, historia y vigencia

- **T-03 — Persistencia aditiva.** Mapear contenido, activaciones, retiros, rules, auditoría y evaluaciones append-only a SQL Server con schema, índices, digest y migración compatible con SPEC 01. Cubre: REQ-01, REQ-02, REQ-09, REQ-13, REQ-16, NFR-02, NFR-03, CA-01, CA-08, CA-10, CA-11.
- **T-04 — Selección e idempotencia.** Implementar selección fail-closed por reloj/digest, `evaluation_key` scoped, workload actor, concurrencia, historial, reevaluación y diff. Cubre: REQ-03, REQ-06, REQ-09, REQ-13, REQ-16, NFR-01, NFR-03, NFR-04, CA-03, CA-08, CA-09.

**Resultado verificable:** SQL Server impide vigencias ambiguas y duplicados concurrentes, y conserva evaluaciones anteriores al reevaluar o retirar políticas.

### Bloque 3 — Seguridad, administración e integración futura

- **T-05 — Administración y simulación.** Exponer contratos versionados para drafts, validación, publicación, retiro, lectura y simulación, protegidos por `ADMIN`/`AUDITOR` y con Problem Details. Cubre: REQ-01, REQ-02, REQ-15, REQ-16, REQ-17, NFR-02, NFR-03, CA-01, CA-02, CA-10, CA-11, CA-12.
- **T-06 — Fuentes y puerto de evaluación.** Implementar workload allowlist, providers de facts/catálogos, manifest/attestation, ejecución por referencia y descriptores authority/scope compatibles con SPEC 01. Cubre: REQ-05, REQ-06, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, REQ-16, REQ-17, CA-03, CA-05, CA-06, CA-07, CA-12.
- **T-07 — Quotation waiver fail-closed.** Implementar request/response del verifier, nonce/binding/digest, `REDUCE_MIN_VALID_QUOTATIONS`, authority `PROCUREMENT`, SoD, replay idempotente y adapter default-deny, sin workflow. Cubre: REQ-14, REQ-16, REQ-17, NFR-03, NFR-04, CA-09, CA-10, CA-12.

**Resultado verificable:** la API y el puerto de aplicación administran y evalúan con autorización local; ninguna excepción reduce controles sin evidencia externa válida.

### Bloque 4 — Operación y evidencia

- **T-08 — Observabilidad y health.** Instrumentar administración/evaluación, diagnóstico de configuración y documentación de despliegue, publicación, retiro y recuperación sin datos sensibles. Cubre: REQ-03, REQ-15, REQ-16, REQ-17, NFR-04, NFR-05, CA-08, CA-10, CA-11, CA-12.
- **T-09 — Pruebas de contrato y regresión.** Implementar toda la estrategia unitaria, integración, API/E2E, migración, concurrencia, seguridad y contratos futuros. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, REQ-17, NFR-01, NFR-02, NFR-03, NFR-04, NFR-05, CA-01, CA-02, CA-03, CA-04, CA-05, CA-06, CA-07, CA-08, CA-09, CA-10, CA-11, CA-12.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra el contrato completo y un operador puede detectar y corregir falta de políticas sin habilitar compras por defecto ni perder historia.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-16, NFR-02, NFR-03 | Publicar crea contenido/digest y activación atómicos; publicar un sucesor cierra la activación abierta en el mismo instante sin solape; retirar agrega un único evento y no muta/reactiva contenido. Regla inválida, default ausente, reference no verificable, versión obsoleta o carrera falla sin audit de éxito. | Automática: dominio, integración SQL Server y API/E2E concurrente. |
| CA-02 | REQ-02, REQ-04, REQ-15 | Solo se aceptan los facts/tipos/operadores/efectos enumerados, con referencias de catálogo versionadas verificables; código arbitrario se rechaza. La simulación directa queda no empresarial, no persiste `PolicyEvaluationBundle` y no acepta excepciones. | Automática: unitarias de catálogo/validación y API/E2E de simulación. |
| CA-03 | REQ-03, REQ-05, REQ-06, REQ-09, NFR-01, NFR-04 | Cero activaciones vigentes da `409`; múltiples, digest corrupto o dependencia indisponible da `503`, sin bundle. Con activación/provider válidos, representaciones equivalentes y mismo `evaluated_at_utc` producen bytes canónicos, SHA-256, reglas, controles y resultado idénticos, sin thresholds embebidos. | Automática: vectores golden, providers controlados, integración y API/E2E. |
| CA-04 | REQ-04, REQ-06, REQ-07, REQ-08 | Dentro y entre LINE/REQUEST se expande por línea, aplica máxima exigencia y reagrupa solo descriptores idénticos; `BLOCK` vence a `ALLOW`, PO a compra directa, cotizaciones usa máximo y documentos se unen. Una key conserva AuthorityLevel versionado máximo y keys distintas siguen separadas. | Automática: matriz cross-scope, adapter de niveles y serialización. |
| CA-05 | REQ-08, REQ-10 | Una línea barata con riesgo/contrato produce Department Approval al Department propietario del Cost Center e IT/Legal Review con provenance, líneas, fases, roles y authority, nunca al Beneficiary Department por error, sin candidato ni Approval Task. | Automática: unitaria `LINE`, provider controlado y contrato del puerto. |
| CA-06 | REQ-05, REQ-06, REQ-07, REQ-11 | `EvaluatePurchaseRequest` evalúa todas las líneas y luego REQUEST, suma el bundle atestiguado y no acepta un total del caller; dividir importe no reduce Finance, y manifest con línea omitida/duplicada o versión/organización mezclada falla sin resultado parcial. | Automática: unitarias e integración con manifests válidos/adulterados. |
| CA-07 | REQ-07, REQ-08, REQ-12 | `SOURCING_PO` solo acepta la última evaluación de la misma versión/conjunto de líneas. Preferred supplier reduce cotizaciones solo por regla; controles más estrictos permanecen. Cada cambio material enumerado exige nueva versión/reevaluación, mientras publicar política no reinterpreta compras intactas. | Automática: unitaria e integración de actualidad, materialidad y combinación. |
| CA-08 | REQ-01, REQ-03, REQ-09, REQ-13, NFR-02, NFR-03, NFR-04 | La key solo deduplica dentro de organización+workload+operación; el mismo fingerprint completo devuelve el bundle original y cualquier diferencia da `409`. Activación/retiro/nueva versión no altera historia y health distingue cero políticas de corrupción/dependencia sin exponer reglas. | Automática: integración concurrente, persistencia append-only y health API/E2E. |
| CA-09 | REQ-13, REQ-14 | Reevaluar conserva historia/diff. Solo `REDUCE_MIN_VALID_QUOTATIONS` pasa del máximo `from` a `to >=` el máximo floor; cualquier fuente no excepcionable domina. Exige verifier, bindings/digests/nonce, `PROCUREMENT_APPROVER` + `PROCUREMENT`, SoD y vigencia; replay idéntico es idempotente y otro binding bloquea sin reducir. | Automática: contrato del verifier, límites from/to, manipulación/replay y default-deny. |
| CA-10 | REQ-14, REQ-16, REQ-17, NFR-04, NFR-05 | `ADMIN` no bypassa; autoaprobación, SoD, presupuesto insuficiente y BLOCK de invariante no son excepcionables; actor workload/originador e intentos quedan correlacionados sin snapshot completo ni PII. | Automática: dominio, API/E2E de permisos y telemetría capturada. |
| CA-11 | REQ-01, REQ-09, REQ-16, REQ-17, NFR-02, NFR-05 | `ADMIN` administra, `AUDITOR` solo lee, workload allowlisted ejecuta y otros reciben `403`; `400/404/409/413/422/503` usan Problem Details; límites se aplican antes de persistir y un fallo transaccional no deja datos/evidencia huérfanos. | Automática: API/E2E, casos exactos de límite±1 e integración con fallo forzado. |
| CA-12 | REQ-05, REQ-08, REQ-12, REQ-14, REQ-15, REQ-17 | Sin PR, Cost Center, Supplier o Workflow reales, adapters controlados prueban manifests, catálogos, authority y excepciones; providers ausentes fallan cerrado y `COST_CENTER` no se envía al resolver de SPEC 01 hasta habilitar su catálogo. | Automática: contratos con adapters controlados/default-deny y regresión de SPEC 01. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Política ausente permite compras | Una evaluación devuelve `PASSED` sin versión publicada o default explícito | Selección fail-closed, health y CA-03/CA-08. |
| Orden de reglas cambia el resultado | Permutar reglas altera controles o hash | Evaluación exhaustiva, canonicalización estable y CA-03/CA-04. |
| Thresholds ilustrativos se convierten en defaults | Aparecen 500/5,000/20,000 tras migración sin publicación administrativa | Migración sin baseline implícita, inspección de datos y CA-03. |
| Acoplamiento prematuro a dominios futuros | Policy Engine crea tablas o estados de PR/Supplier/Workflow | Snapshots y descriptores por contrato, límites de DEC-01/DEC-06 y CA-12. |
| Fraccionamiento entre líneas reduce autoridad | Varias líneas pequeñas evitan Finance pese a total alto | Agregación interna por REQUEST y CA-06. |
| Excepción reutilizable o autoaprobada | Una referencia reduce controles de otra versión o el `ADMIN` la crea directamente | Binding exacto, verifier externo default-deny y CA-09/CA-10. |
| Historia reinterpretada al cambiar política | Una evaluación antigua muestra reglas o resultado nuevos | Versiones/evaluaciones inmutables, snapshots completos y CA-08. |
| Snapshot filtra datos innecesarios | Logs o registros contienen tokens, descripciones o identidad personal | Allowlist de facts, telemetría minimizada y CA-10/CA-11. |
