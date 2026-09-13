# Operación del módulo Approval

## Orden de despliegue

1. Verificar el preflight de contrato antes de aplicar la migración o de habilitar submissions y
   dispatcher v2: la base debe tener **cero eventos de contrato desconocido** (heredados v1 o de
   otra versión) en cualquier estado. La API ejecuta este preflight al arrancar
   (`ApprovalContractPreflight`) y, si encuentra eventos heredados, falla cerrado en lugar de
   despacharlos o reescribirlos.
2. Aplicar la migración antes de publicar la API:
   `dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api`.
   El esquema `Approval` es aditivo. La migración de SPEC 04 (`Spec04ApprovalEvolution`) añade
   delegaciones, jobs de transición, supersesiones, evidencia de autoridad, carry-forward,
   revocaciones, columnas de delegación en assignments/corridas y hace backfill 1:1 de la evidencia
   de cada decisión humana existente sin recalcular su digest histórico. Las migraciones de
   Approval **no tienen descenso destructivo**: `Down` lanza `NotSupportedException` a propósito.
   Revertir la aplicación conserva el esquema y toda la historia; no se autoriza una migración
   descendente que borre evidencia.
3. Antes de habilitar los productores de SPEC 04, desplegar los consumidores de
   `approval-result/v3`, `approval-case-lifecycle/v1` y `approval-evidence-revoked/v1`. Los
   productores (workers/API) se habilitan después: un contrato sin consumer exact-one queda en
   dead letter, nunca se descarta en silencio.
4. Publicar la API. El worker productivo (`ApprovalWorkflowWorker`) arranca con la aplicación en
   entornos distintos de `Testing`, ejecuta el preflight y, si este falla, detiene el host.
5. Habilitar submissions solo después de que los adapters y el resolver de SPEC 01 estén
   registrados: sin resolver o con registro ambiguo el módulo falla cerrado (`503`).

## Contratos y versiones

- `approval-canonical-json/v2` es la única canonicalización publicable. Un cambio de preimagen,
  orden o claves exige una nueva `canonicalization_version`; un cambio de schema del documento,
  subir `policy_schema_version`.
- `approval-result/v2` publica los resultados de decisiones humanas. `approval-result/v3` conserva
  el mismo schema y lo emiten únicamente las decisiones derivadas de carry-forward, con
  `decision_digest` igual al `carry_forward_decision_digest` persistido. Cada evento v2/v3 publica
  `result_source` `{id,key,type}` (`APPROVAL_REQUIREMENT` o `EXTERNAL_PREREQUISITE`).
- `approval-case-lifecycle/v1` publica `SUPERSEDED`, un evento por target anterior, y nunca se
  representa como `approval-result`. `approval-evidence-revoked/v1` publica `REVOKED` con
  `reason_code` (`OWNER_INVALIDATION|INCIDENT_CONTAINMENT`); el motivo libre queda solo en audit.
- Los consumidores deduplican por `event_id + contract_version`; el dispatcher entrega al menos
  una vez y nunca reescribe el payload. Verificadores de evidencia fallan cerrado ante ausencia,
  ambigüedad o evidencia `REVOKED`.
- Los preimages nuevos (`delegation_fingerprint`, `supersession_fingerprint`,
  `revocation_fingerprint`, `carry_forward_proof`, `carry_forward_decision_digest`) tienen vectores
  dorados en `tests/ProcureToPay.UnitTests/Approval/ApprovalEvolutionGoldenTests.cs`.

## Worker y operación automática

- El worker recorre todas las organizaciones cada cinco segundos y:
  - despacha los eventos de outbox vencidos del dispatcher;
  - confirma los jobs vencidos de transición de delegación (`ACTIVATE`/`EXPIRE`) bajo su lease y
    fencing token; cada confirmación escribe su transición, audit, corrida y el estado del job en
    una sola transacción;
  - procesa las corridas de reconciliación pendientes bajo su lease.
- Una delegación persistida crea jobs únicos por `(delegation_id, transition, scheduled_at)`. Crear
  con `valid_from <= now < valid_to` confirma `ACTIVE` en la transacción inicial (job `ACTIVATE`
  completado) y deja `EXPIRE` pendiente; revocar cancela los jobs pendientes. Un activador que
  llegue tarde procesa primero `ACTIVATE` y luego `EXPIRE` por `scheduled_at`.
- Cada transición confirmada crea **una sola corrida**: `DELEGATION_CHANGE` (activación o
  revocación) usa como raíz el audit del comando que la causó; `DELEGATION_EXPIRY` usa su propia
  raíz `SYSTEM` causada por la delegación, su versión y `valid_to`. La unicidad es
  `(organization_id, delegation_id, delegation_version, transition, scheduled_at)`; un reclaim
  reutiliza job, corrida, raíz y cursor.
- La corrida de delegación solo reevalúa los requirements cubiertos por ese rol y scope.
- Cada cambio confirmado de perfil, rol, grant o scope crea **una sola corrida** por su
  `AdministrativeAuditRecord` (unicidad `organization_id + trigger_audit_id`). Una solicitud
  administrativa exige `reconciliation_key` y es idempotente por
  `organization_id + actor_user_id + reconciliation_key`.
- La corrida usa lease persistente de 30 s, renovación como máximo cada 10 s, fencing token y
  cursor UUID-D. Un crash o un lease expirado permiten reclamar la misma corrida, reutilizando su
  `root_audit_id`, su cursor y su causa; nunca se crea una raíz nueva.
- Configuración: `Approval:InstanceId` (opcional; por defecto máquina + proceso) identifica el
  holder del lease. `Approval:Worker:Enabled` (por defecto `true`) permite deshabilitar el worker.

## Superficies operativas

- `GET /api/v1/approval/operations/unassigned`: requirements sin candidato elegible.
- `GET /api/v1/approval/operations/reconciliation`: estado de la última reconciliación y si una
  corrida lleva más de 60 s sin completar.
- `POST /api/v1/approval/operations/reconciliation` con `{ "reconciliationKey": "..." }`: solo
  `ADMIN`; el rol restaura operación pero no elige assignee ni decide.
- `GET /api/v1/approval/operations/outbox` y `.../outbox/dead-letters`: backlog y dead letters.
- `POST /api/v1/approval/operations/outbox/{eventId}/replay`: solo `ADMIN`; reintenta un dead letter
  sin editar el payload.
- `GET /api/v1/approval/cases/{caseId}`: originador o workload propietario; `AUDITOR` organizacional
  obtiene la lectura de evidencia. `ADMIN` por sí solo no obtiene esas lecturas.
- `GET /api/v1/approval/cases/{caseId}/decisions`, `.../assignments` y `.../audit`: solo `AUDITOR`.
  La lectura de auditoría expone la unión de actor `USER|WORKLOAD|SYSTEM` y el enlace `caused_by`
  de cada efecto automático. La vista de decisiones expone `origin`, `actor_type`, `evidence_id`,
  `evidence_status` y la raíz humana de un carry-forward; la de assignments expone
  `delegation_id`/`delegation_version` aplicados.

## Delegación, supersesión y revocación

- `POST /api/v1/approval/delegations`: el delegante autenticado, o `ADMIN` organizacional con
  motivo, crea una delegación acotada por rol, scope e intervalo UTC. Falla cerrado ante
  auto-delegación, solape, cadena, intervalo inválido o falta de `RoleAssignment` del delegante
  durante todo el intervalo. El replay de `delegation_command_key` devuelve el mismo registro y
  otra carga devuelve `409`.
- `POST /api/v1/approval/delegations/{delegationId}/revoke`: el delegante o `ADMIN`; cancela los
  jobs pendientes y crea la corrida `DELEGATION_CHANGE/REVOKE`.
- `GET /api/v1/approval/delegations` (propias) y `.../delegations/organization` (solo `AUDITOR`).
- `POST /api/v1/approval/cases/{caseId}/supersessions`: solo el workload propietario. Exige
  `supersession_key`, `submission_key`, versión esperada y un mapping biyectivo y completo de
  targets. En una transacción, el caso anterior y sus nodos no terminales pasan a `SUPERSEDED`,
  se emite `approval-case-lifecycle/v1` por target anterior, y el caso nuevo nace con
  carry-forward estricto (misma identidad material y descriptor, evidencia `VALID`) o con tasks
  nuevas. El replay devuelve el mismo caso y otra carga `409`.
- `POST /api/v1/approval/evidence/{evidenceId}/revocations`: el workload propietario de un caso
  enlazado (`OWNER_INVALIDATION`) o `ADMIN` solo como `INCIDENT_CONTAINMENT`. Marca la evidencia
  `REVOKED`, emite `approval-evidence-revoked/v1`, corta carry-forward y verificación futuros y no
  modifica la decisión histórica. Es irreversible e idempotente por
  `(organization_id, evidence_id, revocation_key)`.
- `GET /api/v1/approval/cases/{caseId}/history`: cadena de versiones para el originador, el
  workload propietario o `AUDITOR`; fuera de scope responde `404`.

## Health

`/health/approval` degrada cuando:

- existe un dead letter;
- el evento pendiente más antiguo supera 5 minutos;
- existe una corrida de reconciliación sin completar dentro de su presupuesto de 60 segundos;
- existe un job de transición de delegación pendiente más de 60 segundos desde su instante
  programado.

Las etiquetas son `APPROVAL_DEAD_LETTER`, `APPROVAL_BACKLOG_OVERDUE`,
`APPROVAL_RECONCILIATION_OVERDUE` y `APPROVAL_DELEGATION_TRANSITION_OVERDUE`.

## Recuperación

- **Lease abandonado o crash del worker:** no se interviene: el siguiente barrido reclama la misma
  corrida cuando el lease expira y continúa desde el cursor. El fencing token impide que el holder
  anterior confirme cambios.
- **Dead letter:** `ADMIN` decide si reejecuta con replay; el payload permanece byte a byte.
- **Evento v1 detectado:** detener el despliegue, restaurar la base desde el backup pre-Approval y
  reintentar. No existe consumer v1 autorizado, recuperación automática ni reescritura de filas.
- **Migración aplicada por error:** no se revierte con `database update` (Down lanza); se corrige
  hacia adelante y se conserva el esquema.
- **Job de delegación perdido o activación tardía:** el worker procesa los jobs pendientes por
  `scheduled_at`; una activación tardía se confirma y la expiración se procesa inmediatamente
  después, reutilizando jobs, corridas y raíces existentes.
- **Evidencia revocada por error:** no se reactiva. La decisión histórica es inmutable; una
  aprobación nueva exige una task humana y una decisión nueva.
- **Supersesión de un caso ya supersedido:** `409`; la cadena se lee por
  `GET .../cases/{caseId}/history`.
- **Reversión de la aplicación:** antes del primer tráfico v2 se puede volver a la versión previa
  conservando el esquema vacío. Después de persistir un resultado de SPEC 03/04 se deshabilitan
  submissions y transiciones, se mantienen los dispatcher/consumers de los contratos ya emitidos y
  se corrige hacia adelante; nunca se vuelve a una versión incapaz de consumir esos eventos.
