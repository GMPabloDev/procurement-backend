# SPEC 03 — Approval Workflow

> **Formato:** sdd/v3
> **Estado:** Borrador
> **Ejecución:** No iniciada
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** Pendiente
> **Fecha:** 2026-09-10
> **Actualizada:** 2026-09-10
> **Aprobada el:** Pendiente
> **Aprobada por:** Pendiente
> **Objetivo:** Proveer un workflow genérico, auditable y determinista que convierta requisitos tipados en tareas asignadas a personas elegibles, conserve decisiones por scope y permita verificarlas sin que `ADMIN` sustituya una aprobación empresarial.
> **Depende de:** SPEC 01, SPEC 02
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

La SPEC 01 implementó identidades empresariales, roles, Approval Authority, scopes y un resolver que devuelve candidatos junto con `EligibilityEvidence`, pero deliberadamente no selecciona personas ni crea tareas. La SPEC 02 define controles de aprobación tipados, fases, reevaluaciones y el contrato fail-closed mediante el que un quotation waiver solo puede aplicarse tras verificar una decisión del workflow; su ejecución todavía está en implementación.

La fuente funcional exige decisiones conservadas por línea, agrupación únicamente cuando requirement, aprobador y decisión coinciden, secuencia mixta —Department primero; Finance, IT y Legal en paralelo; Procurement y PRE_PO después—, ausencia de autoaprobación, estado `UNASSIGNED` cuando no hay candidatos, delegación acotada y una bandeja interna que no dependa de notificaciones externas. Todavía no existen Purchase Requests, Cost Centers, Supplier Master, matching ni payments, por lo que esta entrega crea un núcleo reutilizable y contratos confiables para esos módulos sin adelantar sus estados ni datos propios.

## Alcance

### Incluye

- Recepción idempotente de casos y requisitos de aprobación tipados desde adapters internos autenticados y allowlisted.
- Modelado de casos, requirements, targets por línea o sujeto, dependencias, tareas, asignaciones, decisiones, supersesión, cancelación y evidencia append-only.
- Secuencia por dependencias explícitas y fases compatibles con los descriptores de SPEC 02, permitiendo paralelismo cuando no existe dependencia.
- Resolución de candidatos mediante SPEC 01 y asignación automática al elegible con menor carga activa, con desempate estable.
- Estado `UNASSIGNED`, reconciliación tras cambios de elegibilidad y bloqueo fail-closed cuando no hay aprobador válido.
- Acciones `APPROVE`, `REJECT` y `REQUEST_CHANGES`, con control de concurrencia, idempotencia y revalidación de elegibilidad en el instante de decidir.
- Segregation of Duties mediante exclusiones inmutables aportadas por el dominio propietario y verificadas nuevamente por el workflow.
- Delegaciones programadas, acotadas por rol/scope/vigencia, sin transferencia de autoridad y con reasignación auditable.
- Bandeja interna, consulta de historia y contratos de lectura minimizados para aprobadores, originadores autorizados, `ADMIN` y `AUDITOR`.
- Eventos durables de resultado para que los dominios propietarios proyecten sus estados sin acoplarlos al workflow.
- Creación y verificación de decisiones de quotation waiver compatibles con el verifier de SPEC 02, incluidos binding, nonce, vigencia, revocación, SoD y digests.
- Persistencia SQL Server, migración aditiva, observabilidad, health, errores y pruebas de esta capacidad.

### No incluye

- Purchase Requests, Purchase Request Lines, Cost Centers, budgets, proveedores, RFQ, quotations, awards, POs, invoices, matching, payments o subscriptions.
- Evaluación de políticas o creación de `GeneratedControl`; el workflow consume descriptores producidos por SPEC 02 o requisitos equivalentes de futuros adapters confiables.
- Transiciones de estado propias de los dominios, reservas presupuestarias, sourcing, emisión de PO, pago o mutaciones sobre documentos origen.
- Quórums, votación, aprobación por grupos o múltiples decisiones sobre un mismo requirement; dos aprobaciones sucesivas requieren dos keys y dependencias distintas.
- Aprobación, rechazo o completion manual por `ADMIN`, ni selección manual de un assignee.
- Escalamiento por SLA, recordatorios, email, chat, push o integraciones de notificación.
- Diseñador visual o genérico de workflows, scripts, expresiones arbitrarias o definición de roles/authorities fuera de la matriz de SPEC 01.
- Preservar una aprobación ante cambios materiales sin prueba canónica de que el target y su requirement permanecen idénticos.
- Interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Ingreso confiable e idempotente.** Un adapter in-process registrado de forma exact-one por `subject_type + operation + contract_version`, invocado por un workload autenticado y allowlisted, puede abrir un `ApprovalCase` para una organización y una versión inmutable de sujeto. El comando incluye `submission_key`, referencia/digest del snapshot origen, `requester_id` cuando exista, `originator_id`, actores excluidos por SoD y requirements completos. `submission_key` es única en `(organization_id, workload_issuer, workload_client_id, subject_type, operation)` y su fingerprint canónico cubre todos esos campos excepto correlation y reloj del servidor; repetirlo devuelve el mismo caso sin volver a resolver ni crear tareas, y reutilizar la key con otro contenido devuelve `409`. Una petición HTTP de usuario no puede autocertificar requirements, exclusiones, scopes ni digests.

- **REQ-02 — Requirements, scopes y targets conservados.** Cada `ApprovalRequirement` contiene key estable, tipo, `stage_code`, dependencias, rol, authority, `DecisionScopeDescriptor`, targets versionados, acciones permitidas y digest del descriptor origen. `DecisionScopeDescriptor` usa el objeto JSON `decision-scope/v1` con propiedades exactas `{schema_version, organization_id, scopes}`; cada entrada de `scopes` es `{dimension, reference_id, reference_version}`. `ORGANIZATION` usa `reference_id/reference_version = null` y no se combina; `LEGAL_ENTITY`/`DEPARTMENT` exigen UUID y versión positiva activos. El adapter valida catálogo, conserva el objeto y mapea cada dimensión+UUID a `AuthorizationScopeSet` de SPEC 01. El `string DecisionScope` actual de Policy debe contener exclusivamente este JSON canónico para `policy-approval-adapter/v1`; texto legacy/no canónico se rechaza y no se interpreta. `COST_CENTER` sigue rechazado hasta extender ambos contratos. Cada target conserva identidad y versión de línea o sujeto. Un requirement posee un conjunto indivisible de targets y como máximo una tarea actual. Solo agrupa targets con source key, descriptor/scope, vector de dependencias, acciones, decisión y exclusiones idénticos; tras resolver, todos deben compartir el mismo assignee efectivo. Si las dependencias, exclusiones o routing difieren, el adapter particiona de forma estable antes de crear tareas y conserva `source_requirement_key` más un `workflow_requirement_key` derivado del digest canónico de targets.

- **REQ-03 — Secuencia mixta, stages y prerrequisitos.** Las dependencias forman un grafo acíclico del mismo caso. Cada `DependencyRef` contiene propiedades exactas `{predecessor_kind, predecessor_key, mode, targets}`; `predecessor_kind` es `APPROVAL|EXTERNAL`, `mode` es únicamente `ALL` en Release 1 y cada target es la referencia completa `{type,id,version,material_snapshot_digest}`. El set no vacío debe ser subconjunto por igualdad de los targets del predecessor y del dependent; el edge queda satisfecho solo cuando todos esos targets están `APPROVED` o `SATISFIED`. Todos los targets agrupados comparten el mismo vector completo y ninguna coincidencia solo por id, de otra versión/tipo/digest, puede habilitar o cancelar. El predecessor es otro `ApprovalRequirement` o un `ExternalPrerequisite` tipado con owner adapter/version, source control/digest, targets y estado `WAITING|SATISFIED|FAILED|CANCELLED`. Solo su workload propietario cambia `WAITING → SATISFIED|FAILED` mediante `signal_key`, fingerprint y expected version; `CANCELLED` solo deriva de cancelar/superseder el caso. `FAILED` es inmutable, cancela descendientes sobre `target_ids` y requiere nueva versión/caso para corregirse. Un requirement espera `APPROVED`/`SATISFIED`; al habilitarse crea una tarea `UNASSIGNED` y luego `PENDING`. `stage_code` solo etiqueta: Policy registra `DEPARTMENT`, `PRE_PROCUREMENT`, `PROCUREMENT`, `PRE_PO`, permite Finance/IT/Legal paralelos y futuros adapters registran stages propios como Payment.

- **REQ-04 — Asignación determinista por menor carga.** Al habilitar un requirement, el workflow convierte `decision-scope/v1` a `AuthorizationScopeSet` e invoca `IOrganizationEligibilityService` de SPEC 01 con rol, authority, scope, instante UTC y exclusiones exactas. Si un candidato tiene una delegación vigente que cubre el requirement, se retira al delegante y se añade al delegado solo si este también aparece por mérito propio en el resultado original; se deduplican usuarios efectivos. Sobre ese conjunto calcula la carga como número de tareas actuales `PENDING` asignadas al usuario en la organización, selecciona la menor y desempata por el UUID canónico ascendente. Selección, reserva de carga y creación de assignment se serializan para impedir un mínimo obsoleto concurrente. Se conserva `EligibilityEvidence`, delegación aplicada, carga y desempate; no se rebalancea solo porque cambie la carga después.

- **REQ-05 — Ausencia y pérdida de elegibilidad.** Si no existe candidato, el requirement y su tarea quedan `UNASSIGNED`, el caso queda bloqueado para ese scope y no se elige un fallback. Activar, revocar, desactivar o modificar roles, grants, scopes o delegaciones dispara o permite una reconciliación idempotente. Una tarea `PENDING` cuyo assignee dejó de ser elegible se reasigna según REQ-04 o vuelve a `UNASSIGNED`, conservando toda asignación anterior; una decisión terminal nunca se reasigna ni se reinterpreta.

- **REQ-06 — Segregation of Duties fail-closed.** El contrato de cada adapter declara campos obligatorios de actores por operación y construye exclusiones inmutables; para toda aprobación empresarial `originator_id` es obligatorio y, cuando existe requester distinto, también `requester_id`. Ambos quedan excluidos. Quotation waiver excluye requester, originador del intento y `workload_subject_id` cuando representa una persona; futuros adapters deben declarar buyer, supplier editor, invoice registrar o payment preparer según corresponda. Un actor excluido no aparece como candidato, assignee, delegado efectivo ni decisor. Campo obligatorio ausente, identidad vacía o exclusión no concordante con el contract version se rechaza; `ADMIN`, replay, carry-forward y delegación no pueden reducir el set.

- **REQ-07 — Decisión autorizada e inmutable.** Solo el assignee actual, activo y autenticado puede ejecutar `APPROVE`, `REJECT` o `REQUEST_CHANGES` sobre una tarea `PENDING`. Cada acción exige motivo no vacío, versión esperada y `decision_key`, única en `(organization_id, actor_user_id, decision_key)`. Su fingerprint canónico cubre task/case/requirement ids y versiones, digest de targets, acción, motivo NFC, actor y expected version; excluye UTC del servidor y correlation. Justo antes de confirmar se vuelve a resolver elegibilidad con los mismos inputs y reloj del servidor. Replay con igual fingerprint devuelve la decisión original aunque la tarea ya sea terminal; la misma key con otro fingerprint o una carrera con otra acción devuelve `409`. Decisión, targets, actor, instante, acción, motivo y `EligibilityEvidence` fresca son append-only.

- **REQ-08 — Estados, decisiones y salida durable.** `ApprovalCase` transita `OPEN → BLOCKED|COMPLETED|SUPERSEDED|CANCELLED`; un `BLOCKED` por `UNASSIGNED` puede volver a `OPEN`, mientras uno con prerequisite `FAILED` solo pasa a `SUPERSEDED|CANCELLED`. Requirement transita `WAITING → UNASSIGNED → PENDING → APPROVED|REJECTED|CHANGES_REQUESTED`, y todo estado no terminal puede terminar `SUPERSEDED|CANCELLED`; task refleja desde `UNASSIGNED` y nunca reabre. `APPROVE` habilita edges de sus `target_ids`; `REJECT`/`REQUEST_CHANGES` cancela solo descendants/targets enlazados por esos edges. Tras cambios se usa REQ-09. El caso es `COMPLETED` solo cuando todos los requirements vigentes son terminales y todo external prerequisite vigente está `SATISFIED`; los prerequisites cancelados pertenecen solo a casos `SUPERSEDED|CANCELLED`, y cualquier `WAITING|FAILED` mantiene el caso `BLOCKED`. Cada transición produce outbox por target para que el dominio proyecte sus estados.

- **REQ-09 — Nueva versión, supersesión y carry-forward estricto.** Una nueva versión referencia el caso anterior y targets reemplazados; tareas abiertas afectadas pasan a `SUPERSEDED`. Una decisión solo se conserva si coinciden subject/target estable, descriptor, snapshot material, role, authority, scope, dependencias y exclusiones. Se crea una nueva `ApprovalDecision` terminal con `action=APPROVE`, `origin=CARRY_FORWARD`, nuevo id/digest y referencia al decision/digest humanos previos, junto con `DecisionCarryForwardRecord(previous_decision_id, new_decision_id, new_case_id, new_requirement_id, targets completos, compared_digests)`. El requirement nace `APPROVED` sin task; la nueva evidencia conserva approver/authority originales y hashea la prueba de igualdad sin alterar el registro previo. `POLICY_EXCEPTION` nunca admite carry-forward porque sus bindings pertenecen a una evaluación exacta. Cualquier diferencia exige task y decisión nuevas. Tras `REQUEST_CHANGES`, solo este nuevo caso puede continuar; lo descriptivo se conserva únicamente si el contrato fuente lo excluye de ambos digests.

- **REQ-10 — Delegación programada y acotada.** El delegante debe estar activo y poseer, al crear, un role assignment vigente que cubra todo `decision-scope/v1` delegado; puede crear o revocar su delegación y `ADMIN` puede hacerlo por indisponibilidad operativa, siempre con motivo. La delegación identifica delegante, delegado, rol, scope, `valid_from` inclusivo, `valid_to` exclusivo y estado; no admite auto-delegación, cadenas ni intervalos activos solapados para el mismo delegante/rol/scope. Durante la vigencia se transforma el conjunto efectivo según REQ-04 y la regla de menor carga sigue eligiendo entre todos los usuarios efectivos; el delegado no recibe preferencia automática. Debe calificar por sí mismo para cada requirement, por lo que la delegación nunca amplía authority, límite, scope o vigencia. Activación, revocación o expiración reconcilia tareas `PENDING` en máximo 60 segundos y conserva historia.

- **REQ-11 — Quotation waiver verificable y replay seguro.** Un adapter confiable abre un requirement `POLICY_EXCEPTION/REDUCE_MIN_VALID_QUOTATIONS` ligado a organization/subject/version, `base_bundle_id`, policy version/digest, result/manifest digests, target key, `from`, `to`, floor, binding, nonce único, requester y vigencia. Exige `PROCUREMENT_APPROVER`, authority `PROCUREMENT` y REQ-06. `POST /v1/policy-exceptions/verify` exige service JWT Bearer con issuer/client allowlisted y audience `approval-workflow`; localiza una única decisión por `evidence_digest` y compara todos los campos recibidos con datos persistidos, sin confiar en `ApproverId` o `SegregationSatisfied`. Request y response usan los wire contracts exactos declarados en Datos y contratos; la respuesta incluye verifier, decision, approver, `EligibilityEvidence`, authority digest, scope, SoD, binding, nonce y validez, y Policy debe persistir/verificar el approver devuelto, no el autocertificado por el request. El mismo payload y binding devuelve idempotentemente la misma verificación; reutilizar nonce/decisión con otro bundle, target o payload devuelve no verificada/`409` sin crear decisión. Expiración o revocación también devuelve no verificada.

- **REQ-12 — Evidencia canónica y reproducible.** `approval-canonical-json/v1` usa UTF-8 sin BOM/whitespace; todas las propiedades conocidas están presentes y ordenadas ordinalmente, opcionales ausentes son `null` y campos desconocidos se rechazan. Strings usan NFC; UUID `D` minúsculo; timestamps UTC `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`; enums/códigos mayúsculos; decimales son strings invariantes sin exponente ni ceros no significativos; sets se ordenan por bytes canónicos y rechazan duplicados. `authority_evidence_digest` hashea `{canonicalization_version, eligibility_evidence completo, delegation_id/version|null}`. `workflow_decision_digest` hashea `{canonicalization_version, decision id/version, case/subject/snapshot, requirement/descriptor, targets, action, actor, decided_at, reason, authority_evidence_digest, exclusions, segregation_satisfied, valid_from, valid_to}`. `evidence_digest` hashea `{canonicalization_version, verifier id/version, workflow decision id/version/digest, organization/subject, base bundle/result, policy version/digest, manifest, target, from/to/floor, requester, approver, binding, nonce, authority_evidence_digest, segregation_satisfied, scope, validity}`. SPEC 02 calcula después su distinto `exception_verification_digest` sobre la respuesta verificada; ninguno sustituye al otro. Cada SHA-256 hex minúsculo conserva su preimage exacto. `submission_fingerprint` hashea `{canonicalization_version, organization, workload issuer/client, adapter id/version, subject type/id/version, operation, source snapshot digest, requester, originator, exclusions, requirements, prerequisites}`. `decision_fingerprint` hashea los campos exactos de REQ-07. `signal_fingerprint` hashea `{canonicalization_version, prerequisite id/version, owner workload, result, evidence reference/digest, signal_key}`. `revocation_fingerprint` hashea `{canonicalization_version, evidence id/version, actor type/id, reason, revocation_key}`. Ninguno incluye server UTC o correlation.

- **REQ-13 — Bandeja y visibilidad mínima.** Un usuario activo consulta sus tareas `PENDING`, tareas no asignadas para diagnóstico solo si es `ADMIN`, y su propia historia de decisiones. La respuesta incluye referencias, fase, motivo presentable y datos mínimos aportados por el dominio, no snapshots completos ni saldos/datos bancarios/documentos. El originador autenticado o workload propietario puede consultar el estado de su caso. `AUDITOR` organizacional lee casos, asignaciones, delegaciones, decisiones y evidencia en modo read-only; `ADMIN` opera reconciliación y delegaciones, pero no decide ni accede automáticamente a contenido del documento origen. Los recursos fuera de alcance visible responden `404`.

- **REQ-14 — Auditoría, revocación y operación.** Crear/revocar delegaciones, reconciliar, reasignar, cancelar, superseder, revocar evidencia y decidir genera audit append-only con actor, UTC, motivo, before/after o subcambios, scope, versión y correlation. Solo el workload propietario puede revocar evidencia porque el dominio invalidó su sujeto/binding; `ADMIN` puede revocarla únicamente como contención de incidente. Ambos requieren expected version y `revocation_key` idempotente; la revocación atómica crea `DecisionEvidenceRevocation`, invalida verificaciones futuras y no modifica la decisión. `ADMIN` puede corregir configuración o disparar reconciliación, pero no elegir assignee, crear authority evidence, reactivar evidencia, cambiar una acción terminal ni aprobar.

- **REQ-15 — Errores y límites.** Se usan Problem Details: `401` autenticación; `403` actor sin permiso; `404` recurso no visible; `400` forma, combinación o grafo inválido; `409` idempotencia, versión, estado o decisión concurrente; `413` límites; `422` otra invariante; y `503 /problems/approval-dependency-unavailable` ante fallo/timeout/ambigüedad del resolver, adapter o persistencia requerida. Un caso admite como máximo 2.000 requirements, 10.000 asociaciones a targets y 5 MiB de comando canónico; keys/códigos usan 1–128 caracteres ASCII `[A-Z][A-Z0-9_.:-]*`, `submission_key` y `decision_key` 1–128 `[A-Za-z0-9._:-]`, y motivos 1–1.000 Unicode scalars. El exceso se rechaza antes de persistir.

- **REQ-16 — Cancelación sin bypass.** Solo el workload propietario puede cancelar un caso abierto por una transición válida del dominio y debe aportar versión esperada y motivo. Requirements/tareas no terminales pasan a `CANCELLED`, las decisiones históricas permanecen y se emite el evento durable correspondiente. Cancelar no convierte un rechazo en aprobación, no revoca automáticamente efectos ya consumidos por otro dominio y no elimina datos; la compensación pertenece al dominio propietario.

- **REQ-17 — Exactly-one lógico bajo concurrencia.** Por requirement existe como máximo una tarea actual; por `(requirement_id, target type/id/version/digest)` existe exactamente una decisión terminal cuando el requirement queda `APPROVED|REJECTED|CHANGES_REQUESTED`, incluida la decisión nueva `origin=CARRY_FORWARD`. Creación, assignment, decisión, supersesión, señal de prerrequisito y reconciliación usan índices, leases y transacciones que impiden resultados incompatibles entre instancias. Outbox entrega al menos una vez con `event_id + contract_version` estable. El dispatcher reintenta a 1 s, 5 s, 30 s, 2 min y 10 min, hasta 10 intentos; luego conserva `DEAD_LETTER` y health no saludable hasta replay administrativo. Health queda degradado si el evento pendiente más antiguo supera 5 minutos o una reconciliación debida supera 60 segundos. Tras timeout ningún caller asume ausencia: usa su key de replay.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `ApprovalCase` | id, organización, subject type/id/version, operation, source snapshot digest, workload, submission key/fingerprint, estado, versión | Estados `OPEN`, `BLOCKED`, `COMPLETED`, `SUPERSEDED`, `CANCELLED`; no contiene el documento origen. |
| `DecisionScopeDescriptor` | `{schema_version, organization_id, scopes[{dimension, reference_id, reference_version}]}` | JSON canónico `decision-scope/v1`; el string Policy contiene este objeto, no texto libre. |
| `DependencyRef` | predecessor kind/key, mode `ALL`, targets completos | Cada target incluye type/id/version/material digest y pertenece a ambos nodos. |
| `ApprovalRequirement` | source/workflow key, tipo, stage, dependency refs, role, authority, scope, descriptor digest, exclusions, estado | Un conjunto indivisible de targets y máximo una task actual; estados de REQ-08. |
| `ExternalPrerequisite` | key, owner adapter/version, source control type/digest, typed parameters, targets, estado, version, signal key/fingerprint | Preserva parámetros del control; owner cambia `WAITING` a `SATISFIED` o `FAILED`. |
| `ApprovalTarget` | type, id, version, material snapshot digest | Unidad de trazabilidad; una decisión siempre enumera targets. |
| `ApprovalTask` | requirement, targets, estado, current assignment, versión | `UNASSIGNED`, `PENDING`, `APPROVED`, `REJECTED`, `CHANGES_REQUESTED`, `SUPERSEDED`, `CANCELLED`. |
| `ApprovalAssignment` | task, assignee, assigned/released UTC, carga observada, evidence, delegation opcional, causa | Append-only; una sola asignación actual por tarea. |
| `ApprovalDecision` | requirement/task nullable, targets completos, action, origin, actor, reason, UTC, key/fingerprint, evidence, digests | `origin=HUMAN | CARRY_FORWARD`; exactamente una terminal por requirement/target al completar. |
| `DecisionCarryForwardRecord` | previous/new decision, new case/requirement, targets completos, digests comparados, actor workload, UTC | Nueva decisión APPROVE derivada; conserva la decisión humana original por referencia. |
| `ApprovalDelegation` | delegante, delegado, role, scope, vigencia, estado, actor, motivo, versión | El delegante posee el role/scope al crear; no concede authority ni admite cadena/solape. |
| `WorkflowDecisionEvidence` | decisión/version, preimages/digests, authority evidence, SoD, binding, nonce, validez/revocación | Response completo verificable por SPEC 02 y futuros consumers. |
| `DecisionEvidenceRevocation` | evidence/decision, revocation key, actor type/id, UTC, reason, expected version | Append-only e irreversible; solo workload propietario o ADMIN por incidente. |
| `ApprovalOutboxEvent` | id, versión de contrato, caso/sujeto, requirement, targets, resultado, decisión/digest, correlation, attempts/state | Persistido atómicamente; estados `PENDING`, `DELIVERED`, `DEAD_LETTER`. |
| `ApprovalAuditRecord` | actor type/id, UTC, acción, objetivo/versiones, scope, motivo, before/after/subcambios, correlation | Append-only y minimizado. |

Reglas adicionales del contrato:

- `valid_from` es inclusivo y `valid_to` exclusivo; ambos son UTC y `valid_to > valid_from`.
- Un requirement `REQUIRED` conserva type, level id/version/code/rank, amount/currency y `DecisionScopeDescriptor`; `NONE` solo se admite para IT/Legal según SPEC 01.
- La carga ignora estados distintos de `PENDING`; una tarea agrupada cuenta como una unidad.
- La decisión agrupada no puede dividirse: respuestas distintas requieren requirements/tasks diferentes antes de decidir.
- Unicidad: `submission_key` usa el scope de REQ-01; `decision_key`, `(organization_id, actor_user_id, key)`; `signal_key`, `(organization_id, external_prerequisite_id, key)`; `revocation_key`, `(organization_id, evidence_id, key)`; y `nonce`, `(organization_id, verifier_type, nonce)`. Cada reuse exige fingerprint idéntico.
- Una revocación invalida verificaciones futuras, pero no altera la decisión histórica ni revierte efectos ya consumidos.
- Evento y verifier se versionan; consumers deduplican por `event_id + contract_version` y rechazan versiones incompatibles.
- La respuesta de verificación satisface los campos de SPEC 02 REQ-14; el `exception_verification_digest` continúa calculándose y persistiendo en Policy.

Mapping obligatorio `policy-approval-adapter/v1`:

| Control combinado de SPEC 02 | Proyección en Approval Workflow | Owner/señal |
| --- | --- | --- |
| `REQUIRE_APPROVAL` | Uno o más `ApprovalRequirement`; expande/particiona líneas por descriptor, dependencies, exclusions y routing; `PolicyApprovalDescriptor.DecisionScope` debe contener `decision-scope/v1` canónico. | Workflow resuelve y decide. |
| `REQUIRE_BUDGET_CHECK` | Prerequisite conserva requirement key, targets, Cost Center refs/versiones, amount base y currency. | Adapter futuro Budget; evidence ref/digest y `SATISFIED` o `FAILED`. |
| `REQUIRE_SUPPORTING_DOCUMENT` | Prerequisite conserva requirement key, targets, set de document types y minimum count. | Adapter propietario; señal con conteo/tipos/digest, sin adjuntos. |
| `REQUIRE_ACTIVE_SUPPLIER` | Prerequisite conserva requirement key, targets y supplier id/version. | Adapter futuro Supplier; señal de esa versión exacta. |
| `REQUIRE_QUOTATIONS` | Prerequisite conserva requirement key, targets, minimum valid, exception floor/allowance. | Adapter futuro Sourcing; waiver exige nueva submission enlazada de SPEC 02. |
| `REQUIRE_PROCUREMENT` | Prerequisite conserva requirement key, stage y targets; representa completion de Procurement. | Adapter futuro Procurement; señal versionada. |
| `ALLOW`, `REQUIRE_PO`, `ALLOW_DIRECT_PURCHASE` | No crean task ni prerequisite; permanecen en el resultado Policy y el dominio propietario los aplica. | Sin señal al workflow. |
| `BLOCK` o bundle `BLOCKED` | La submission se rechaza `409`; nunca se crean approvals para sortear el bloqueo. | Policy/domain debe corregir o reevaluar. |

El adapter crea un prerequisite vigente por cada control no humano de la tabla y todo prerequisite, tenga o no dependent humano, es condición obligatoria de `ApprovalCase.COMPLETED`. Rechaza control desconocido, parámetro/digest omitido, scope string no canónico, target omitido o owner ambiguo; si el owner aún no existe, queda `WAITING` y bloquea fail-closed. Ningún control se convierte implícitamente en tarea humana ni se ignora.

Wire contract `workflow-verification-request/v1` (todas las propiedades presentes):

| Campo | Regla |
| --- | --- |
| `contract_version`, `evidence_digest`, `workflow_decision_id` | Versión exacta, SHA-256 y decisión esperada. |
| `organization_id`, `subject_type`, `subject_id`, `subject_version` | Deben coincidir con el caso y la evaluación. |
| `base_bundle_id`, `base_result_digest`, `policy_version_id`, `policy_content_digest`, `manifest_digest` | Binding completo de SPEC 02. |
| `target_requirement_key`, `from`, `to`, `floor` | Reducción exacta publicada; `floor <= to < from`. |
| `requester_id`, `originator_id`, `binding`, `nonce` | SoD y replay; nonce único según este contrato. |

Wire contract `workflow-verification-response/v1`:

| Campo | Regla |
| --- | --- |
| `contract_version`, `verified`, `verifier_id`, `verifier_contract_version` | `verified=false` no aporta autoridad. |
| `evidence_digest`, `binding`, `nonce`, `workflow_decision_id`, `workflow_decision_version`, `workflow_decision_digest` | Coincidencia exacta con request/persistencia. |
| `approver_id`, `eligibility_evidence`, `authority_evidence_digest`, `decision_scope`, `segregation_satisfied` | Evidencia server-side completa; Policy usa estos valores devueltos. |
| `valid_from`, `valid_to`, `revoked_at` | `verified=true` exige instante dentro del intervalo y `revoked_at=null`. |

El endpoint exige Bearer antes del lookup. Request malformado da `400`, service identity inválida `401/403`, decisión ausente/no visible `404`, reuse conflictivo `409`, binding expirado/revocado/insuficiente `422` o `verified=false` según el adapter de SPEC 02; una respuesta exitosa siempre usa `workflow-verification-response/v1` completo.

## Migración, despliegue y reversión

- La persistencia se añade bajo un schema explícito de Approval y no modifica ni elimina tablas de Organization o Policy. Las FKs internas preservan historia; referencias a sujetos futuros se guardan como identidades/versiones tipadas, no como FKs a tablas que aún no existen.
- Se despliega primero la migración, luego la aplicación con el resolver de SPEC 01 y adapters allowlisted, y por último se habilitan submissions. Health distingue schema no aplicado, resolver indisponible, outbox pendiente >5 minutos, `DEAD_LETTER` y reconciliación atrasada >60 segundos sin exponer subjects ni motivos.
- Mientras SPEC 02 no esté implementada y disponible, el adapter de controles y el verifier de quotation waiver permanecen deshabilitados/fail-closed; el Approval Workflow no elimina ese bloqueo con defaults.
- Dispatcher y reconciliador usan leases persistentes e idempotencia multiinstancia. Al reiniciar recuperan pendientes; outbox aplica los intentos de REQ-17, conserva dead letters y solo `ADMIN` puede disparar replay sin editar el payload.
- Una reversión de aplicación conserva casos, tareas, delegaciones, auditoría y outbox. No se autoriza una migración descendente destructiva; la recuperación consiste en volver a una versión compatible, pausar nuevos submissions si fuera necesario y corregir hacia adelante.
- Antes de habilitar tráfico se prueba con adapters controlados: caso simple, paralelismo, `UNASSIGNED`, delegación, replay y quotation waiver verificado. No se crean usuarios, grants o políticas por defecto.

## Seguridad y privacidad

- Usuarios de negocio se autentican con JWT y autorización local de SPEC 01; claims empresariales del IdP no conceden tareas ni decisiones. Submissions, señales de prerrequisito, cancelaciones y consultas técnicas requieren workloads allowlisted.
- La llamada Policy→Workflow al verifier usa service JWT Bearer con audience dedicada, issuer/client allowlisted, timeout y rotación de credencial fuera de la base; nunca se registra el token. Sin credencial válida responde `401/403` y Policy falla cerrado.
- El decisor debe ser el assignee actual y seguir siendo elegible en el instante de confirmar. Assignment previo, delegación, `ADMIN`, Job Title o posesión de un enlace no bastan.
- Las exclusiones SoD se almacenan con el requirement y forman parte del digest. No pueden reducirse durante reasignación, delegación, carry-forward o replay.
- La delegación no transfiere permisos: el delegado debe poseer por sí mismo role, authority, límite, scope y vigencia suficientes. Se prohíben cadenas y auto-delegación.
- Respuestas y telemetría no incluyen tokens, subjects de IdP, snapshots completos, datos bancarios, saldos, documentos ni PII innecesaria. El contenido presentable lo aporta el dominio mediante campos limitados y minimizados.
- El verifier de exception usa comparación exacta de digests/bindings y respuesta fail-closed; nunca crea una aprobación a partir de datos de verificación recibidos.
- `ADMIN` puede restaurar operación, no producir una decisión empresarial, alterar una decisión terminal ni ocultar historia. `AUDITOR` nunca muta.

## Requisitos no funcionales

- **NFR-01 — Determinismo reproducible.** Con el mismo caso canónico, snapshot de elegibilidad/carga y reloj de evaluación se obtienen el mismo assignee, tarea, bytes y digests; el replay devuelve los artefactos originales en vez de recalcularlos con otra carga o instante.
- **NFR-02 — Atomicidad e inmutabilidad.** Assignment, carga, decisión, audit y outbox correspondientes se confirman o revierten juntos. Requirements, decisiones, evidencias y asignaciones liberadas no se sobrescriben.
- **NFR-03 — Consistencia de autoridad y delegación.** Una decisión revalida autorización contra estado confirmado; una revocación no puede prolongarse mediante caché. Activación, revocación o expiración de una delegación reconcilia las tareas afectadas en un máximo de 60 segundos, medido con el reloj del servidor.
- **NFR-04 — Disponibilidad segura.** Fallos del resolver, inconsistencias, ausencia de adapter o retraso no degradan a aprobación ni conservan silenciosamente un assignee inválido; el scope afectado permanece bloqueado y recuperable mediante replay/reconciliación.
- **NFR-05 — Observabilidad minimizada.** Submissions, asignaciones, estados `UNASSIGNED`, decisiones, conflictos, supersesiones, delegaciones, reconciliación, dispatch y verificaciones emiten métricas, logs estructurados y trazas OpenTelemetry con correlation, tipos, resultado y duración, sin motivo completo, snapshot ni PII.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | DAG humano/externo, state machines, scopes tipados, agrupación, menor carga/desempate, SoD, delegación, carry-forward, fingerprints, canonicalización y digests | `dotnet test ProcureToPay.sln` con casos en `tests/ProcureToPay.UnitTests/` |
| Integración | SQL Server, índices exactly-one, carreras de assignment/decision/signals, reconciliación, outbox/dead letter, replay y migración aditiva | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | JWT de usuario y servicio, bandeja, acciones, prerrequisitos, visibilidad, `ADMIN`/`AUDITOR`, Problem Details, límites y verifier | `dotnet test ProcureToPay.sln` con escenarios en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato | Adapters controlados de SPEC 02 y futuros dominios, eventos duplicados, eligibility snapshots y `POST /v1/policy-exceptions/verify` | Pruebas de contrato dentro de la solución con adapters in-process y HTTP controlados |
| Migración/operación | Base con SPEC 01/02, health, dispatcher/reconciliador multiinstancia, restart y reversión conservando historia | Prueba automatizada sobre SQL Server efímero y procedimiento operativo documentado |

Todos los criterios se verifican automáticamente. Los casos negativos incluyen actor excluido, authority revocada entre assignment y decisión, cero candidatos, empate concurrente, delegación inválida/expirada, dependencia cíclica, task stale, doble decisión, replay con payload distinto, binding adulterado, evidencia revocada, evento duplicado y fallo entre decisión, audit y outbox.

## Decisiones

- **DEC-01 — Núcleo genérico con adapters tipados.** El workflow es reutilizable y no conoce estados internos de Purchase Request, Supplier, Invoice o Payment. Se descartan tanto limitarlo a compras como adelantar todos los dominios; estos aportarán subjects, exclusions, presentación y proyección mediante contratos versionados.
- **DEC-02 — Menor carga con desempate determinista.** Entre candidatos válidos se asigna a quien tenga menos tareas `PENDING`, con empate por `UserId` canónico. Se descartan bandeja compartida y selección manual porque trasladan la carrera o discrecionalidad al momento de decidir.
- **DEC-03 — Delegación programada y scoped.** Release 1 incluye vigencia, scope y reasignación, pero el delegado debe ser elegible por mérito propio. Se descartan transferir grants, encadenar delegaciones y convertir `DELEGATE` en una decisión terminal.
- **DEC-04 — Una decisión por requirement/target.** Dos aprobaciones sucesivas son requirements distintos con keys y dependencias explícitas. Se descartan quorum y votación implícita porque no están definidos en la fuente funcional.
- **DEC-05 — El dominio propietario proyecta su estado.** El workflow emite resultados durables y no modifica documentos ajenos. Esto permite implementar el núcleo antes de Purchase Requests y evita transacciones distribuidas que reinterpreten estados futuros.
- **DEC-06 — Elegibilidad en assignment y decisión.** Se conserva evidencia de ambos instantes y la decisión exige una revalidación fresca. Se descarta confiar indefinidamente en el snapshot de assignment porque roles, grants, scope y vigencia pueden cambiar.
- **DEC-07 — Carry-forward solo por igualdad canónica.** Una aprobación previa se referencia únicamente si target, requirement, exclusiones y snapshot material son idénticos. Ante duda se exige nueva aprobación; no hay heurística por texto o por Job Title.
- **DEC-08 — Sin bypass administrativo.** `ADMIN` corrige configuración, delegaciones y operación, pero nunca elige assignee ni decide. Se descarta un override de emergencia porque vulneraría autoridad, SoD y trazabilidad empresarial.
- **DEC-09 — Dependencias explícitas, no workflow designer.** Un DAG acotado enlaza approvals y controles externos; `stage_code` solo etiqueta. El adapter de Policy aplica sus fases y futuros adapters registran las propias. Se descarta una DSL o catálogo global rígido porque el orden ya queda verificable por dependencias.

## Plan de implementación

### Bloque 1 — Contrato y modelo de dominio

- **T-01 — Casos, requirements y tareas.** Implementar adapters exact-one, `decision-scope/v1`, targets, DAG con prerrequisitos externos, state machines, agrupación, límites y validaciones. Cubre: REQ-01, REQ-02, REQ-03, REQ-08, REQ-15, REQ-16, CA-01, CA-02, CA-06.
- **T-02 — Decisiones y evidencia canónica.** Implementar lifecycle terminal, idempotencia, carry-forward, supersesión, cancelación y `approval-canonical-json/v1`. Cubre: REQ-07, REQ-08, REQ-09, REQ-12, REQ-16, REQ-17, NFR-01, CA-05, CA-06, CA-07, CA-10.

**Resultado verificable:** el dominio representa decisiones por target, rechaza grafos/agrupaciones inválidos y produce digests estables sin depender de módulos futuros.

### Bloque 2 — Routing, elegibilidad y delegación

- **T-03 — Assignment por menor carga.** Integrar el resolver de SPEC 01, exclusiones SoD, serialización de carga, estado `UNASSIGNED` y revalidación antes de decidir. Cubre: REQ-04, REQ-05, REQ-06, REQ-07, REQ-17, NFR-01, NFR-03, NFR-04, CA-03, CA-04, CA-05.
- **T-04 — Delegaciones y reconciliación.** Implementar vigencia, scopes, ausencia de cadenas/solapes y worker idempotente de reasignación en 60 segundos. Cubre: REQ-05, REQ-06, REQ-10, REQ-14, REQ-17, NFR-03, NFR-04, CA-04, CA-08, CA-12.

**Resultado verificable:** múltiples instancias asignan una sola tarea al candidato correcto, y los cambios de autoridad/delegación nunca habilitan una decisión inválida.

### Bloque 3 — Persistencia, historia y entrega durable

- **T-05 — Schema Approval y migración.** Mapear casos, requirements, prerequisites, targets, assignments, delegaciones, decisiones, revocaciones, audit y outbox a SQL Server con índices exactly-one, rowversion y migración aditiva. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-07, REQ-08, REQ-09, REQ-10, REQ-12, REQ-14, REQ-16, REQ-17, NFR-02, CA-01, CA-03, CA-06, CA-07, CA-08, CA-10, CA-12.
- **T-06 — Dispatcher y proyección de resultados.** Implementar outbox versionado, leases, replay y contratos de consumer idempotente, sin mutar dominios propietarios. Cubre: REQ-08, REQ-16, REQ-17, NFR-02, NFR-04, NFR-05, CA-06, CA-12.

**Resultado verificable:** una decisión y su evidencia/evento son atómicos, sobreviven restart y no producen dos resultados lógicos bajo carreras o reentrega.

### Bloque 4 — API e integración con Policy

- **T-07 — Bandeja, acciones y administración.** Exponer contratos versionados para inbox, lectura, decisiones, señales externas, delegaciones, revocación, reconciliación y cancelación, con JWT/workload, scopes, minimización y Problem Details. Cubre: REQ-01, REQ-03, REQ-05, REQ-07, REQ-10, REQ-13, REQ-14, REQ-15, REQ-16, NFR-04, NFR-05, CA-02, CA-04, CA-05, CA-08, CA-11.
- **T-08 — Adapter Policy y verifier.** Consumir descriptores/fases de SPEC 02, abrir quotation waivers y servir `POST /v1/policy-exceptions/verify` autenticado, con response completo, replay, binding, nonce, vigencia, revocación y digests. Cubre: REQ-03, REQ-06, REQ-11, REQ-12, REQ-14, REQ-15, NFR-01, NFR-04, CA-02, CA-09, CA-10, CA-11.

**Resultado verificable:** adapters controlados crean tareas a partir de controles tipados y SPEC 02 solo acepta una excepción realmente aprobada y ligada a la evaluación exacta.

### Bloque 5 — Verificación y operación

- **T-09 — Verificación automatizada.** Implementar matrices unitarias, integración SQL/multiinstancia, API/E2E y contratos bidireccionales, con una prueba identificada para cada CA y caso negativo de la estrategia. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, REQ-17, NFR-01, NFR-02, NFR-03, NFR-04, NFR-05, CA-01, CA-02, CA-03, CA-04, CA-05, CA-06, CA-07, CA-08, CA-09, CA-10, CA-11, CA-12.
- **T-10 — Operación documentada.** Documentar despliegue, service auth, health/umbrales, dead-letter/replay, reconciliación, privacidad, revocación y reversión; automatizar el procedimiento sobre SQL Server efímero. Cubre: REQ-11, REQ-14, REQ-17, NFR-03, NFR-04, NFR-05, CA-09, CA-11, CA-12.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra el contrato y un operador puede detectar/reparar bloqueo, outbox o reconciliación sin crear aprobaciones manuales ni perder historia.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-15, REQ-17 | Workload/adapter exact-one crean una vez; el JSON exacto `decision-scope/v1` mapea dimensión+UUID y preserva versión, mientras texto legacy/campo extra falla `400`. Targets solo se agrupan con descriptor, dependencies, exclusions y assignee iguales. Replay devuelve ids; payload distinto `409`, caller `403` y límite±1 aceptación/`413`. | Automática: unitarias de schema/scope/grouping/fingerprint/límites, integración SQL y API/E2E. |
| CA-02 | REQ-02, REQ-03 | Cada efecto SPEC 02 sigue la tabla y cada prerequisite conserva todos sus parámetros; incluso sin dependent humano impide `COMPLETED` hasta `SATISFIED`. Edges `ALL` comparan type/id/version/digest completos. Department precede, Finance/IT/Legal paralelos y Procurement/PRE_PO esperan controles; replay signal es idempotente y FAILED requiere nuevo caso. | Automática: matriz efecto/parámetro, DAG por target completo, signals y adapters controlados. |
| CA-03 | REQ-04, REQ-17, NFR-01 | Con cargas `0, 1, 1` se elige carga `0`; con empate, menor UUID. Una delegación retira al delegante, añade al delegado elegible y la selección por carga sigue aplicando al set efectivo. Dos asignaciones concurrentes se serializan sin doble current assignment ni rebalance posterior. | Automática: unitarias deterministas e integración concurrente multi-DbContext. |
| CA-04 | REQ-05, REQ-10, NFR-03, NFR-04 | Cero candidatos crea `UNASSIGNED` y bloquea solo el scope afectado. Tras alta/revocación/delegación, reconciliar asigna, reasigna o vuelve a `UNASSIGNED`; activación/expiración se refleja en máximo 60 segundos sin cambiar decisiones terminales. | Automática: fake clock, worker multiinstancia e integración con Organization. |
| CA-05 | REQ-06, REQ-07 | Requester/originator y demás excluidos nunca son candidatos ni decisores. Revocar role/grant después del assignment hace fallar la acción y reasigna/bloquea; solo el assignee todavía elegible decide una vez con motivo y versión, y `ADMIN` recibe `403`. | Automática: unitarias SoD e API/E2E de revocación, concurrencia y permisos. |
| CA-06 | REQ-07, REQ-08, REQ-16, REQ-17, NFR-02 | Las state machines rechazan reapertura y transiciones no enumeradas. APPROVE habilita dependencias; REJECT/REQUEST_CHANGES cancelan descendientes sin afectar targets independientes; REQUEST_CHANGES solo continúa mediante nueva versión. Decisión, audit y outbox son atómicos y replay no duplica efecto. | Automática: tabla exhaustiva de transiciones, fallo forzado, consumer idempotente y API/E2E. |
| CA-07 | REQ-09, REQ-17 | Nueva versión supersede solo lo afectado. Igualdad exacta crea una decisión nueva `APPROVE/CARRY_FORWARD` por new requirement/targets, referida a la humana original, deja requirement `APPROVED` sin task, habilita dependents y emite evento; cambiar digest exige task nueva. Policy exceptions nunca se conservan. | Automática: tabla material/descriptiva, constraints de decisión y lifecycle del nuevo caso. |
| CA-08 | REQ-10, REQ-14, NFR-03 | Solo un delegante que posee role/scope o `ADMIN` con motivo crea la delegación. El set efectivo excluye al delegante y solo incluye al delegado elegible, sin preferencia sobre menor carga. Auto-delegación, cadena, solape o intervalos inválidos se rechazan; lifecycle y tareas quedan auditados. | Automática: unitarias de intervalos/scopes/carga e integración del reconciliador. |
| CA-09 | REQ-06, REQ-11 | Verifier rechaza Bearer inválido. Request v1 completo se compara server-side; response v1 devuelve approver/evidence/scope/SoD/validez y Policy usa el approver devuelto. Replay idéntico devuelve evidencia; otro payload/bundle/target/nonce da conflicto/no verificada. Expiración/revocación conserva el control. | Automática: serialización wire exacta, auth, contrato HTTP bidireccional e integración Policy. |
| CA-10 | REQ-12, NFR-01 | Golden vectors fijan bytes/SHA-256 de submission, decision, signal, revocation, authority, decisión y verification, incluidos nulls/decimales. Permutar sets no cambia digest; cambiar campo sí; NFC se normaliza y duplicados/campos desconocidos se rechazan. Cada key/nonce respeta su scope único. | Automática: golden independientes, constraints SQL y replay de preimages. |
| CA-11 | REQ-13, REQ-14, REQ-15, NFR-05 | Assignee ve solo su inbox minimizada; originador ve su caso; `AUDITOR` lee evidencia; `ADMIN` reconcilia/delega pero no decide ni ve contenido fuente por defecto. `401/403/404/400/409/413/422/503` usan Problem Details sin filtrar PII o existencia fuera de scope. | Automática: API/E2E con JWTs/roles controlados y captura de logs/trazas. |
| CA-12 | REQ-05, REQ-08, REQ-10, REQ-14, REQ-17, NFR-02, NFR-03, NFR-04, NFR-05 | Sobre base SPEC 01/02, dos instancias recuperan leases sin duplicar assignment/evento. Health degrada a >5 min de outbox o >60 s de reconciliación y falla con dead letter; los 10 intentos/replay ADMIN no cambian payload. Restart y rollback de aplicación conservan historia. | Automática: SQL Server efímero, fake clock, restart/multiinstancia, health E2E y runbook probado. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Assignment injusto o no determinista | Empates producen usuarios distintos o una carrera concentra tareas | Carga persistida, transacción serializable, desempate canónico y CA-03. |
| Aprobación con autoridad revocada | Un assignee decide después de perder role/grant/scope | Revalidación en la misma operación, reconciliación y CA-05. |
| Delegación amplía autoridad | Un delegado sin grant suficiente recibe o decide una tarea | Elegibilidad independiente, sin cadenas, evidencia propia y CA-08. |
| Cambio material reutiliza aprobación | Una nueva versión conserva decisión pese a cambiar target o scope | Digests materiales, carry-forward por igualdad exacta y CA-07. |
| Doble decisión o evento | Dos instancias confirman acciones incompatibles o consumer procesa dos veces | Índices exactly-one, rowversion, keys idempotentes y outbox deduplicable. |
| `ADMIN` sustituye al negocio | Operación administrativa marca una tarea aprobada o elige assignee | API separada, ausencia de comando override y pruebas CA-05/CA-11. |
| Verifier acepta evidencia autocertificada | Payload modificado reduce cotizaciones sin decisión persistida | Lookup server-side, binding/digests exactos, fail-closed y CA-09/CA-10. |
| Acoplamiento a dominios aún inexistentes | Workflow crea estados/tablas de PR, supplier, invoice o payment | Contratos tipados, referencias externas sin FK y outbox según DEC-01/DEC-05. |
| Exposición de datos sensibles en inbox | Tareas o telemetría contienen documento, saldo o datos bancarios | Proyección minimizada, allowlist de campos y CA-11. |
