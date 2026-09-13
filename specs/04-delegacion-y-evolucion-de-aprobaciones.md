# SPEC 04 — Delegación y evolución de aprobaciones

> **Formato:** sdd/v3
> **Estado:** Implementada
> **Ejecución:** Integrada
> **Vigencia:** Sustituida parcialmente por SPEC 05
> **Revisión:** 2
> **Digest contractual:** 0ffbc76e8ef5a1c88d251ebad74a99d6cb45cebffd0b6f553fddc5210049933f
> **Fecha:** 2026-09-11
> **Actualizada:** 2026-09-13
> **Aprobada el:** 2026-09-13
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Permitir ausencias y cambios materiales mediante delegaciones acotadas, supersesión, carry-forward estricto y revocación de evidencia sin ampliar autoridad ni reescribir decisiones históricas.
> **Depende de:** SPEC 01
> **Modifica:** SPEC 03
> **Reemplaza:** Ninguna

## Contexto

SPEC 03 entrega casos, assignments y decisiones humanas inmutables, pero un proceso real también debe sobrevivir vacaciones, revocaciones de autoridad y nuevas versiones del documento. La fuente funcional exige que toda delegación tenga vigencia y audit, que el delegado nunca reciba autoridad superior a la propia y que un cambio material no conserve una aprobación de forma heurística.

Esta spec extiende el contrato implementado por SPEC 03: añade estados terminales, actores derivados, evidencia identificable y eventos de lifecycle sin reinterpretar decisiones humanas ya emitidas. La integración de quotation waiver y sus wire contracts queda en SPEC 05.

## Alcance

### Incluye

- Creación, activación y revocación de delegaciones por rol, scope e intervalo.
- Transformación del conjunto de candidatos y reconciliación programada de tareas afectadas.
- Nueva versión de casos, supersesión selectiva y continuación tras `REQUEST_CHANGES`.
- Carry-forward únicamente por igualdad canónica, creando una decisión nueva y trazable.
- Revocación irreversible de evidencia sin alterar la decisión histórica.
- Historia de versiones, delegaciones, reasignaciones, carry-forward y revocaciones para actores autorizados.
- Persistencia, observabilidad, operación, errores y pruebas de estas extensiones.

### No incluye

- Ingreso base, DAG, asignación directa, decisión humana u outbox del núcleo; pertenecen a SPEC 03.
- Interpretación de controles de SPEC 02, quotation waiver o verifier HTTP; pertenecen a SPEC 05.
- Transferir role assignments, grants, límites o authority mediante una delegación.
- Cadenas, auto-delegación, quórums, votación, selección manual de assignee o override de decisión.
- Diffs semánticos propios de Purchase Request, Supplier, Invoice o Payment; el adapter propietario declara materialidad y digests.
- Reversión automática de efectos que otro dominio ya consumió.
- Notificaciones externas, escalamiento por SLA o interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Delegación programada, autorizada e idempotente.** El delegante debe estar activo y poseer al crear un `RoleAssignment` vigente que cubra por completo el rol y `DecisionScopeDescriptor` delegados durante todo el intervalo solicitado. Puede crear o revocar su delegación; `ADMIN` organizacional puede hacerlo por indisponibilidad operativa con motivo obligatorio. Cada comando exige `delegation_command_key` de 1–128 caracteres `[A-Za-z0-9._:-]`, única en `(organization_id, actor_type, actor_id, key)`, y el fingerprint exacto de Datos y contratos; excluye UTC/correlation del servidor. Replay idéntico devuelve el mismo registro y otra carga da `409`. La delegación identifica delegante, delegado, role, scope, `valid_from` inclusivo, `valid_to` exclusivo, estado `SCHEDULED|ACTIVE|REVOKED|EXPIRED` y versión. Se rechazan auto-delegación, intervalos inválidos, cadenas y periodos no revocados solapados para el mismo delegante/rol/scope.

- **REQ-02 — Autoridad no transferible y routing efectivo.** Para un requirement cubierto por una delegación vigente se elimina al delegante del conjunto directo y se agrega al delegado solo si este también aparece por mérito propio en el resultado original de `IOrganizationEligibilityService`; los usuarios efectivos se deduplican. La regla de menor carga y desempate de SPEC 03 opera sobre ese conjunto, sin preferencia automática por el delegado. El delegado debe cubrir por sí mismo role, authority, nivel, límite, scope y vigencia, y nunca puede superar ninguno mediante la delegación.

- **REQ-03 — Reconciliación de delegaciones.** Crear una delegación persiste jobs únicos `(delegation_id, transition, scheduled_at)` para `ACTIVATE` en `valid_from` y `EXPIRE` en `valid_to`.
  - El worker reclama el job con lease/fencing de SPEC 03 y confirma atómicamente transición, audit y corrida: `SCHEDULED→ACTIVE` crea `DELEGATION_CHANGE/ACTIVATED`; `ACTIVE→EXPIRED` crea `DELEGATION_EXPIRY/EXPIRED`.
  - Crear con `valid_from <= now < valid_to` persiste `ACTIVATE` ya `COMPLETED` y confirma en la transacción inicial `ACTIVE`, audit y corrida `DELEGATION_CHANGE/ACTIVATED` causada por el audit raíz `CREATE`; `EXPIRE` queda pendiente. Revocar `SCHEDULED|ACTIVE` cancela jobs pendientes y crea `DELEGATION_CHANGE/REVOKED`.
  - La corrida es única por `(organization_id, trigger, delegation_id, delegation_version, transition, scheduled_at)`. `DELEGATION_CHANGE` exige `trigger_audit_id` del create/revoke; `DELEGATION_EXPIRY` usa la raíz `SYSTEM` del audit de expiración, causada por delegación/version y `valid_to`. Restart o reclaim reutiliza job, corrida, raíz y cursor.
  - La corrida reevalúa tasks `PENDING`: libera una assignment inválida y resuelve el conjunto efectivo, pudiendo quedar `UNASSIGNED`. Completa en 60 segundos desde `valid_from`, revocación o `valid_to`; cada assignment conserva causa, delegación/version o nulos y evidencia. Las decisiones terminales no cambian.

- **REQ-04 — Nueva versión y supersesión de caso completo.** Un workload propietario puede abrir una nueva versión que referencia el caso previo, con `submission_key`, `supersession_key`, fingerprint exacto y versión esperada.
  - La solicitud contiene un mapping biyectivo y de cardinalidad idéntica: cada target completo anterior y cada replacement aparecen exactamente una vez. No puede omitir, duplicar, fusionar ni agregar un target sin origen. Un requirement agrupado nunca se parte dentro del caso anterior.
  - La operación atómica crea el caso nuevo, cambia el anterior a `SUPERSEDED` y lleva todos sus requirements, prerequisites y tasks no terminales a `SUPERSEDED`. Estos estados son terminales, no cuentan para completion ni carga y nunca reabren; las decisiones terminales previas permanecen inmutables.
  - El caso nuevo recrea cada target mapeado una vez: solo aprobaciones que cumplan REQ-05 nacen como carry-forward; el resto crea tasks nuevas conforme a SPEC 03. Tras `REQUEST_CHANGES`, únicamente este caso enlazado puede continuar; un prerequisite `FAILED` se corrige mediante el nuevo caso.
  - La transacción emite un `approval-case-lifecycle/v1` `SUPERSEDED` por cada target anterior y los `approval-result/v2` que correspondan a transiciones del caso nuevo; un fallo revierte caso nuevo, estados y outbox juntos.

- **REQ-05 — Carry-forward por igualdad canónica.** Una aprobación previa se conserva únicamente cuando coinciden organization, subject y mapping de target estable, versión/digest material, requirement descriptor, role, authority, scope, dependencias, exclusiones y acciones permitidas; su `DecisionAuthorityEvidence` compartida debe seguir `VALID` al confirmar. Se crea una nueva `ApprovalDecision` terminal con `action=APPROVE`, `origin=CARRY_FORWARD`, actor `SYSTEM/APPROVAL_WORKFLOW`, `actor_user_id=null`, sin task ni `decision_key`, id/digest nuevos y causa hacia la decisión fuente inmediata. Nunca se atribuye al usuario original una acción nueva. La decisión conserva `source_decision_id`, `root_human_decision_id` y referencia la misma `evidence_id`/versión inmutable; una evidencia puede respaldar la decisión humana raíz y sus decisiones derivadas. El requirement nuevo nace `APPROVED` sin task, habilita dependientes y emite `approval-result/v3`. `DecisionCarryForwardRecord` conserva decisiones fuente/raíz/nueva, caso/requirement nuevos, mapping, digests y prueba canónica. Cualquier diferencia, evidencia revocada o incertidumbre exige task y decisión humanas nuevas. Los adapters pueden declarar tipos no transferibles; `POLICY_EXCEPTION` siempre lo es sin consumir digests de SPEC 05.

- **REQ-06 — Revocación de evidencia y operación sin bypass.** Cada decisión referencia una `DecisionAuthorityEvidence` con UUID, versión y estado `VALID|REVOKED`; decisiones carry-forward comparten la evidencia inmutable de su raíz humana. La migración crea una identidad estable para la evidencia inline de cada decisión humana existente sin cambiar su digest. Solo el workload propietario de cualquiera de los casos enlazados a la evidencia puede revocarla porque el sujeto o binding dejó de ser válido; `ADMIN` organizacional puede hacerlo únicamente como contención de incidente. Ambos requieren motivo, versión esperada y `revocation_key` de 1–128 caracteres `[A-Za-z0-9._:-]`, única por `(organization_id, evidence_id, key)`. La transacción crea un `DecisionEvidenceRevocation` append-only, cambia la evidencia a `REVOKED`, corta toda verificación o carry-forward posterior sin caché tolerante y emite `approval-evidence-revoked/v1`, pero no modifica la decisión ni revierte efectos consumidos. Replay idéntico devuelve la revocación; otro fingerprint da `409`. Nadie puede reactivar evidencia, cambiar una acción terminal, crear carry-forward manual ni escoger assignee.

- **REQ-07 — Historia, visibilidad y auditoría.** El aprobador consulta delegaciones propias y assignments históricos; el originador o workload owner ve la cadena de casos y resultados; `AUDITOR` organizacional lee delegaciones, supersesiones, carry-forward y revocaciones; `ADMIN` opera reconciliación y contención sin acceder automáticamente al documento origen. Fuera del scope visible se responde `404`. Crear/revocar delegación, reasignar, superseder, carry-forward y revocar evidencia genera audit append-only con actor, UTC, motivo, versiones, scope, before/after o subcambios y correlation.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `ApprovalDelegation` | delegante, delegado, role, scope, vigencia, estado, versión, command key/fingerprint, actor, motivo | No concede authority; replay exacto; sin cadenas, solapes ni auto-delegación. |
| `CaseSupersession` | caso anterior/nuevo, subject versions, mapping total de targets, key/fingerprint, actor workload, UTC | Append-only; caso/nodos anteriores quedan terminales `SUPERSEDED`. |
| `DecisionAuthorityEvidence` | id, organización, decisión humana raíz, digest/JSON canónico, versión, estado | Una por raíz humana; puede ser referenciada por sus carry-forward. `REVOKED` bloquea toda la cadena futura. |
| `DecisionCarryForwardRecord` | decisión fuente/raíz/nueva, evidencia, caso/requirement nuevo, mapping, proof digest, UTC | Siempre acompaña una decisión nueva `APPROVE/CARRY_FORWARD` de actor `SYSTEM`. |
| `DecisionEvidenceRevocation` | evidence/decision, revocation key/fingerprint, actor union, UTC, reason, expected version | Append-only, irreversible e idempotente. |
| `ApprovalAssignment` extendido | delegation id/version opcional, causa de asignación/liberación | Conserva la prueba de conjunto efectivo y carga. |
| `ApprovalDelegationTransitionJob` | delegación/version, `ACTIVATE\|EXPIRE`, scheduled UTC, estado, lease/fencing | Único por delegación/transición/instante; confirma transición/audit/run o se cancela por revocación. |
| `ApprovalReconciliationRun` extendido | trigger, transición, delegación/version/instante causal, raíz, lease/checkpoint | Añade `DELEGATION_CHANGE\|DELEGATION_EXPIRY` y la unicidad de REQ-03; reclaim conserva corrida y causa. |
| `approval-case-lifecycle/v1` | event/case/previous case, organización/sujeto, target, resultado, UTC | Solo `SUPERSEDED`; un evento por target anterior; deduplicación por `event_id + contract_version`. |
| `approval-evidence-revoked/v1` | event/evidence/decision, organización, revocation, actor type, reason code, UTC | No expone motivo libre ni evidence JSON; deduplicación por `event_id + contract_version`. |
| `ApprovalAuditRecord` extendido | acción, actor, causa, scope, versiones, before/after o subcambios, motivo, correlation | No contiene snapshots completos ni PII innecesaria. |

Reglas adicionales:

- `valid_from` es inclusivo, `valid_to` exclusivo y `valid_to > valid_from`; ambos son instantes UTC con siete decimales. Dos intervalos se solapan si comparten cualquier instante efectivo; extremos adyacentes `[a,b)` y `[b,c)` son válidos.
- Una delegación se aplica solo cuando role y todos los scopes del requirement están cubiertos por el mismo registro. `delegation_id` y `delegation_version` pasan de nulos a sus valores reales en el preimage `authority_evidence_digest` ya reservado por SPEC 03; no se cambia ese schema.
- Los contratos nuevos usan las reglas de bytes, orden, NFC, UUID, timestamp, nulls y sets de `approval-canonical-json/v2` de SPEC 03. Todas las propiedades indicadas son exactas y están presentes aunque valgan `null`; agregar, quitar o reinterpretar una exige nueva `canonicalization_version`.
- El actor de delegación es `DELEGATOR|ADMIN` y siempre lleva `actor_user_id`. El actor de revocación es unión cerrada `WORKLOAD|ADMIN`: `WORKLOAD` exige issuer/client id y usuario nulo; `ADMIN` exige usuario y workload nulo.

### Preimages canónicos nuevos

| Documento | Propiedades exactas |
| --- | --- |
| `delegation_fingerprint` | `action`, `actor_type`, `actor_user_id`, `canonicalization_version`, `delegatee_user_id`, `delegation_command_key`, `delegation_id`, `delegator_user_id`, `expected_version`, `organization_id`, `reason`, `role`, `scope`, `valid_from`, `valid_to`. `scope` es el objeto exacto `decision-scope/v1`; `CREATE` usa delegation/version nulos y `REVOKE` los valores persistidos. |
| `supersession_fingerprint` | `canonicalization_version`, `new_submission_fingerprint`, `owner_workload_client_id`, `owner_workload_issuer`, `previous_case_id`, `previous_case_version`, `supersession_key`, `target_mapping`. Cada mapping contiene exactamente `materiality_digest`, `materiality_schema_version`, `previous`, `replacement`; ambos targets usan el schema de SPEC 03. |
| `revocation_fingerprint` | `actor_type`, `actor_user_id`, `actor_workload_client_id`, `actor_workload_issuer`, `canonicalization_version`, `evidence_id`, `evidence_version`, `organization_id`, `reason`, `revocation_key`. |
| `requirement_contract_digest` | `actions`, `authority`, `canonicalization_version`, `dependencies`, `exclusions`, `requirement_type`, `role`, `scope`, `stage_code`; sus objetos anidados reutilizan los schemas exactos de SPEC 03. |
| `carry_forward_proof` | `canonicalization_version`, `new_case_id`, `new_requirement_contract_digest`, `new_requirement_key`, `new_target`, `root_human_decision_id`, `source_decision_digest`, `source_decision_id`, `source_evidence_id`, `source_evidence_version`, `source_requirement_contract_digest`, `source_target`. Los dos contract digests y los campos materiales de ambos targets deben ser iguales. |
| `carry_forward_decision_digest` | `action`, `actor_system_id`, `authority_evidence_digest`, `canonicalization_version`, `case_id`, `carry_forward_proof_digest`, `decided_at`, `decision_id`, `decision_scope_digest`, `decision_version`, `evidence_id`, `evidence_version`, `exclusions`, `origin`, `reason`, `requirement_key`, `root_human_decision_id`, `segregation_satisfied`, `snapshot_digest`, `source_decision_id`, `subject_id`, `subject_type`, `subject_version`, `targets`. Usa `APPROVE`, `APPROVAL_WORKFLOW`, `CARRY_FORWARD` y reason fijo `CARRY_FORWARD`; no contiene `actor_user_id`, `task_id` ni `decision_key`. |

`target_mapping`, `targets`, acciones, dependencias y exclusiones son sets ordenados por bytes canónicos y sin duplicados. `new_submission_fingerprint` es exactamente el digest v2 confirmado para el caso nuevo; así el fingerprint de supersesión liga todo el grafo nuevo sin duplicar su preimage. El `proof_digest` es SHA-256 de `carry_forward_proof`; el `decision_digest` persistido para origin `CARRY_FORWARD` es SHA-256 de `carry_forward_decision_digest`, mientras decisiones `HUMAN` conservan sin cambios `workflow_decision_digest` v2 de SPEC 03. Esta spec no consume ningún digest `approval-canonical-json/v1`: la no transferibilidad de `POLICY_EXCEPTION` se decide por type antes de comparar evidencia; SPEC 05, todavía borrador y dependiente de esta entrega, debe revisar sus referencias v1 antes de aprobarse.

### Eventos nuevos

- `approval-case-lifecycle/v1` contiene propiedades exactas `{case_id,contract_version,event_id,occurred_at,organization_id,previous_case_id,result,subject_id,subject_type,subject_version,target}`; `result=SUPERSEDED`, `case_id` es el nuevo y `previous_case_id` el terminal. No se representa como `approval-result/v2`.
- `approval-evidence-revoked/v1` contiene propiedades exactas `{actor_type,case_id,contract_version,decision_id,event_id,evidence_id,evidence_version,occurred_at,organization_id,reason_code,revocation_id}`. `decision_id` es la raíz humana. `reason_code` es `OWNER_INVALIDATION|INCIDENT_CONTAINMENT`; el motivo libre permanece solo en audit. Verificadores consumen este evento o consultan la evidencia por id/version y siempre fallan cerrado ante ausencia, ambigüedad o `REVOKED`.
- `approval-result/v3` conserva las propiedades exactas y combinaciones de `approval-result/v2`; para un `APPROVED` derivado, `decision_digest` es exactamente el `carry_forward_decision_digest` persistido. Solo decisiones `CARRY_FORWARD` emiten v3 en esta entrega; decisiones `HUMAN` y demás resultados conservan v2. Consumers deduplican por `event_id + contract_version` y deben aceptar v3 antes de habilitar productores.
- Los tres eventos se crean en la misma transacción que su mutación, usan entrega al menos una vez y política de retry/dead-letter de SPEC 03. Lifecycle y revocación no amplían las combinaciones cerradas de `approval-result/v2`.

### Golden vectors

Los bloques siguientes son los bytes UTF-8 completos, sin salto final, que los tests deben fijar antes de SHA-256.

`delegation_fingerprint` → `b13f4ba2f009dd886bb3208d58aad5990e7181673acd4fc885d5943e063373e8`:

```json
{"action":"CREATE","actor_type":"DELEGATOR","actor_user_id":"77777777-7777-7777-7777-777777777777","canonicalization_version":"approval-canonical-json/v2","delegatee_user_id":"88888888-8888-8888-8888-888888888888","delegation_command_key":"delegation-1","delegation_id":null,"delegator_user_id":"77777777-7777-7777-7777-777777777777","expected_version":null,"organization_id":"11111111-1111-1111-1111-111111111111","reason":"Vacation coverage","role":"DEPARTMENTAPPROVER","scope":{"organization_id":"11111111-1111-1111-1111-111111111111","schema_version":"decision-scope/v1","scopes":[{"dimension":"ORGANIZATION","reference_id":null,"reference_version":null}]},"valid_from":"2026-10-01T00:00:00.0000000Z","valid_to":"2026-10-08T00:00:00.0000000Z"}
```

`supersession_fingerprint` → `ac8007676e8f81ba4fc580b8f5c03e9683b1573f475d6d1204192a792823485c`:

```json
{"canonicalization_version":"approval-canonical-json/v2","new_submission_fingerprint":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","owner_workload_client_id":"adapter","owner_workload_issuer":"internal://procure-to-pay","previous_case_id":"22222222-2222-2222-2222-222222222222","previous_case_version":3,"supersession_key":"supersession-1","target_mapping":[{"materiality_digest":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","materiality_schema_version":"purchase-request-materiality/v1","previous":{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1},"replacement":{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1}}]}
```

`revocation_fingerprint` → `19da7c45c42adb324b54bf7e45a787dcd640054ddc31c42988c31ecf1cd13be9`:

```json
{"actor_type":"WORKLOAD","actor_user_id":null,"actor_workload_client_id":"adapter","actor_workload_issuer":"internal://procure-to-pay","canonicalization_version":"approval-canonical-json/v2","evidence_id":"99999999-9999-9999-9999-999999999991","evidence_version":1,"organization_id":"11111111-1111-1111-1111-111111111111","reason":"Subject binding invalidated","revocation_key":"revocation-1"}
```

`carry_forward_proof` → `388ed3c277ece39f56dbf3ae51ae705e4ff257f0e743029a02c3bea76fe0736c`:

```json
{"canonicalization_version":"approval-canonical-json/v2","new_case_id":"22222222-2222-2222-2222-222222222223","new_requirement_contract_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","new_requirement_key":"WR-00000000000000000000000000000001","new_target":{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1},"root_human_decision_id":"66666666-6666-6666-6666-666666666666","source_decision_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","source_decision_id":"66666666-6666-6666-6666-666666666667","source_evidence_id":"99999999-9999-9999-9999-999999999991","source_evidence_version":1,"source_requirement_contract_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source_target":{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1}}
```

`carry_forward_decision_digest` → `b0e6b1f008ece288da011247f93d377d3a74ce19bc58d16e9e13db8da18445a4`:

```json
{"action":"APPROVE","actor_system_id":"APPROVAL_WORKFLOW","authority_evidence_digest":"93003e10ecf62a0d795474dc38ea2da5baaf12445c2cedb5ad63741845355850","canonicalization_version":"approval-canonical-json/v2","carry_forward_proof_digest":"388ed3c277ece39f56dbf3ae51ae705e4ff257f0e743029a02c3bea76fe0736c","case_id":"22222222-2222-2222-2222-222222222223","decided_at":"2026-10-01T12:00:00.0000000Z","decision_id":"66666666-6666-6666-6666-666666666668","decision_scope_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","decision_version":1,"evidence_id":"99999999-9999-9999-9999-999999999991","evidence_version":1,"exclusions":["88888888-8888-8888-8888-888888888888"],"origin":"CARRY_FORWARD","reason":"CARRY_FORWARD","requirement_key":"WR-00000000000000000000000000000001","root_human_decision_id":"66666666-6666-6666-6666-666666666666","segregation_satisfied":true,"snapshot_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source_decision_id":"66666666-6666-6666-6666-666666666667","subject_id":"99999999-9999-9999-9999-999999999999","subject_type":"PURCHASE_REQUEST","subject_version":2,"targets":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1}]}
```

Payload exacto `approval-result/v3` → SHA-256 de fixture `a345faae094f8cf32aadee978e21fac7ca27ad15171d7efbb982236a97f03e10`:

```json
{"case_id":"22222222-2222-2222-2222-222222222223","contract_version":"approval-result/v3","decision_digest":"b0e6b1f008ece288da011247f93d377d3a74ce19bc58d16e9e13db8da18445a4","decision_id":"66666666-6666-6666-6666-666666666668","event_id":"12121212-1212-1212-1212-121212121212","occurred_at":"2026-10-01T12:00:00.0000000Z","organization_id":"11111111-1111-1111-1111-111111111111","result":"APPROVED","result_source":{"id":"33333333-3333-3333-3333-333333333334","key":"WR-00000000000000000000000000000001","type":"APPROVAL_REQUIREMENT"},"subject_id":"99999999-9999-9999-9999-999999999999","subject_type":"PURCHASE_REQUEST","subject_version":2,"target":{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST","version":1}}
```

## Migración, despliegue y reversión

- La migración es aditiva sobre el schema Approval de SPEC 03: añade estados `SUPERSEDED`, delegaciones/jobs, supersesiones, carry-forward, evidencia identificable, revocaciones, nuevos tipos de outbox y triggers/checkpoints. Para cada decisión humana existente crea una `DecisionAuthorityEvidence` 1:1 a partir de su digest/JSON inline y enlaza la decisión sin recalcular ni reescribir su digest histórico; los carry-forward posteriores referencian esa fila.
- Se despliega primero la migración, después consumers de `approval-result/v3`, `approval-case-lifecycle/v1` y `approval-evidence-revoked/v1`, y por último productores/workers/API. Hasta habilitar workers no se crean delegaciones ni nuevas versiones; una configuración parcial no debe dejar una transición programada sin job ni emitir un contrato sin consumer compatible.
- El worker reanuda desde checkpoint tras restart y health degrada cuando una reconciliación debida supera 60 segundos. Replay administrativo no modifica eventos ni fingerprints.
- Revertir la aplicación deshabilita nuevas operaciones pero conserva registros. Delegaciones futuras deben permanecer inactivas hasta volver a una versión compatible; no se borran para recuperar.
- Antes de tráfico se prueban activación/expiración, nueva versión, carry-forward positivo/negativo, revocación y recuperación multiinstancia.

## Seguridad y privacidad

- Delegante y delegado se identifican por perfiles locales de SPEC 01; claims del IdP, Job Title o email no conceden cobertura.
- `ADMIN` requiere scope `ORGANIZATION` y motivo para operar por indisponibilidad o incidente; nunca obtiene capacidad de decisión empresarial.
- La evaluación del delegado usa estado confirmado y evidencia propia; no se copia el grant del delegante.
- Revocar evidencia corta verificaciones futuras inmediatamente y no puede quedar oculta por caché.
- Historia y telemetría exponen ids internos, códigos, resultado y duración, no documentos, motivos completos, tokens ni datos sensibles del sujeto.

## Requisitos no funcionales

- **NFR-01 — Consistencia temporal.** Activación, revocación y expiración de delegación se reflejan en assignments afectados en máximo 60 segundos según reloj del servidor.
- **NFR-02 — Inmutabilidad histórica.** Supersesión, carry-forward y revocación agregan registros y eventos; nunca sobrescriben casos, assignments o decisiones previas.
- **NFR-03 — Concurrencia e idempotencia.** Operaciones y workers multiinstancia producen un único estado lógico mediante keys, índices, rowversion, leases y transacciones.
- **NFR-04 — Observabilidad minimizada.** Métricas, logs, trazas y health permiten detectar backlog, conflicto, carry-forward y revocación sin PII o contenido origen.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | Intervalos, scopes, conjunto efectivo, materialidad, propiedades exactas, golden vectors, actor SYSTEM y revocación | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.UnitTests/` |
| Integración | SQL Server, backfill 1:1 de evidencia, solapes, carreras, triggers, workers, checkpoints, outbox y append-only | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | Delegante, `ADMIN`, `AUDITOR`, workload owner, supersesión total, revocación y visibilidad | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato | Materialidad/no-transferibilidad, mappings totales y consumers de ambos eventos nuevos | Pruebas de contrato dentro de la solución |
| Operación | Fake clock, dos workers, restart, SLA de 60 s, health y reversión conservadora | SQL Server efímero y procedimiento automatizado |

Todos los criterios se verifican automáticamente. Casos negativos: delegado no elegible, cadena, solape, expiración durante carrera, cambio de digest, target parcial, carry-forward de tipo prohibido, revocación duplicada distinta y lectura fuera de scope.

## Decisiones

- **DEC-01 — Delegación transforma routing, no autoridad.** El delegado debe ser candidato directo por mérito propio; se descartan transferencia de grants y preferencia automática.
- **DEC-02 — Sin cadenas ni solapes.** Release 1 admite un solo salto y periodos inequívocos para mantener determinismo y auditabilidad.
- **DEC-03 — Nueva decisión para carry-forward.** El caso nuevo conserva resultado mediante una decisión derivada con digest propio, no reutilizando el id anterior ni ocultando historia.
- **DEC-04 — Igualdad declarada y canónica.** El adapter propietario aporta materialidad versionada, pero el workflow compara todos los campos contractuales; ante duda exige aprobación nueva.
- **DEC-05 — Revocación no reescribe hechos.** La decisión ocurrió y permanece; solo se invalida su uso futuro mediante un registro irreversible.
- **DEC-06 — Supersesión de caso completo.** Todo target anterior se mapea y recrea exactamente una vez; la selectividad ocurre al decidir carry-forward versus aprobación nueva, no dejando fragmentos activos del caso anterior.
- **DEC-07 — Carry-forward es efecto SYSTEM causal.** La nueva decisión no suplanta al aprobador original; conserva fuente, raíz humana y evidencia válida.
- **DEC-08 — Evidencia con identidad propia.** Una entidad UUID/version permite revocar y verificar sin usar el digest o el id de decisión como identidad mutable.
- **DEC-09 — Eventos separados y triggers explícitos.** Lifecycle y revocación no amplían `approval-result/v2`; cambios y expiraciones de delegación tienen causas durables distintas.

## Plan de implementación

### Bloque 1 — Delegación y routing

- **T-01 — Modelo de delegación.** Implementar intervalos, scopes, ownership, solapes, lifecycle, persistencia y audit. Cubre: REQ-01, REQ-07, NFR-02, NFR-03, CA-01, CA-02.
- **T-02 — Conjunto efectivo y reconciliación.** Extender routing, evidencia y worker con leases/checkpoint/SLA. Cubre: REQ-02, REQ-03, NFR-01, NFR-03, NFR-04, CA-02, CA-03.

**Resultado verificable:** una ausencia reasigna solo a un delegado elegible y toda activación o expiración se refleja en 60 segundos.

### Bloque 2 — Evolución e historia

- **T-03 — Supersesión versionada.** Implementar nueva versión, targets afectados, continuación tras cambios y eventos. Cubre: REQ-04, REQ-07, NFR-02, NFR-03, CA-04.
- **T-04 — Carry-forward estricto.** Implementar comparación canónica, decisión derivada, record y habilitación de dependencias. Cubre: REQ-05, NFR-02, NFR-03, CA-05.

**Resultado verificable:** cambiar un campo material exige nueva task; igualdad exacta crea una decisión nueva trazada a la humana previa.

### Bloque 3 — Revocación, API y verificación

- **T-05 — Revocación y superficies protegidas.** Implementar idempotencia, eventos, vistas históricas, permisos y Problem Details. Cubre: REQ-06, REQ-07, NFR-02, NFR-03, NFR-04, CA-06, CA-07.
- **T-06 — Operación y compatibilidad.** Registrar workers/health, migrar evidencia existente, desplegar consumers antes que productores y verificar restart, dead-letter y reversión conservadora. Cubre: REQ-03, REQ-06, NFR-01, NFR-04, CA-03, CA-06.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra delegación, evolución y revocación sin depender de Policy.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01 | Solo delegante con role/scope vigente durante todo el intervalo o `ADMIN` con motivo crea/revoca; auto-delegación, cadena, solape e intervalo inválido fallan sin cambios parciales. Replay de `delegation_command_key` idéntico devuelve el mismo registro y otro fingerprint da `409`. | Automática: matriz de intervalos/scopes, golden del fingerprint e integración SQL concurrente. |
| CA-02 | REQ-01, REQ-02 | Durante vigencia se retira al delegante y solo se añade al delegado elegible; menor carga sigue aplicando. Un delegado sin authority, límite o scope suficiente nunca recibe ni decide, y `authority_evidence_digest` fija delegation id/version reales. | Automática: resolver de SPEC 01, fake clock, digest y pruebas negativas. |
| CA-03 | REQ-03, NFR-01, NFR-03 | Crear persiste jobs únicos; activar/revocar crea una sola corrida `DELEGATION_CHANGE` y expirar una `DELEGATION_EXPIRY`, incluso con dos workers o restart. Cada transición reasigna o deja `UNASSIGNED` en 60 segundos, conserva job/run/root/cursor y no cambia decisiones terminales. | Automática: integración multiinstancia de create antes/después de `valid_from`, revocación programada, checkpoint, causa, fencing y reloj. |
| CA-04 | REQ-04, NFR-02 | Nueva versión exige mapping biyectivo de igual cardinalidad, crea atómicamente el caso nuevo, deja caso/nodos anteriores `SUPERSEDED`, recrea cada target una vez y emite `approval-case-lifecycle/v1` por target sin ampliar v2; no reabre `FAILED` ni continúa el mismo caso tras `REQUEST_CHANGES`. Replay devuelve el mismo caso y otra carga da `409`. | Automática: state machine, omisión/duplicado/fusión de target, golden fingerprint, fallo forzado y matriz SQL/outbox. |
| CA-05 | REQ-05, NFR-02 | Igualdad exacta y evidencia compartida `VALID` crean decisión nueva `APPROVE/CARRY_FORWARD` de actor SYSTEM, causa/raíz humana, proof/decision digest, record, requirement aprobado sin task y `approval-result/v3`; cambiar cualquier campo o revocar evidencia exige task nueva y un tipo prohibido no se conserva. | Automática: tabla de materialidad, golden bytes/digests, actor union, evidence lineage y constraints taskless. |
| CA-06 | REQ-06, NFR-02, NFR-03 | Migración crea evidence por raíz humana sin cambiar digests; revocación owner o `ADMIN` invalida toda decisión enlazada y carry-forward futuro sin alterar decisiones. Revocación, audit y `approval-evidence-revoked/v1` confirman atómicamente; replay idéntico no duplica, otro fingerprint da `409` y reactivación no existe. | Automática: migración SQL, cadena derivada, golden fingerprint/evento, fallo transaccional, outbox y API/E2E. |
| CA-07 | REQ-07, NFR-04 | Cada mutación produce audit minimizado y causal; aprobador, originador, `AUDITOR` y `ADMIN` ven únicamente su scope y ninguna superficie permite seleccionar assignee, crear carry-forward manual o cambiar una terminal. | Automática: API/E2E de permisos y captura de telemetría. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Delegación amplía autoridad | Delegado sin grant recibe o decide | Elegibilidad independiente y CA-02. |
| Expiración tardía | Task sigue asignada pasados 60 segundos | Worker con checkpoint, health y CA-03. |
| Cambio material reutiliza aprobación | Digest distinto genera carry-forward | Comparación completa y CA-05. |
| Supersesión cancela targets ajenos | Nueva versión afecta decisiones independientes | Mapping explícito por target y CA-04. |
| Revocación borra historia | Decisión muta o desaparece | Registro append-only y CA-06. |
| Bypass administrativo | `ADMIN` crea carry-forward o decide | Comandos separados y CA-07. |
