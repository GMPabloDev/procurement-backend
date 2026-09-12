# Operación del módulo Approval

## Orden de despliegue

1. Verificar el preflight de contrato antes de aplicar la migración o de habilitar submissions y
   dispatcher v2: la base debe tener **cero eventos `approval-result/v1`** en cualquier estado. La
   API ejecuta este preflight al arrancar (`ApprovalContractPreflight`) y, si encuentra eventos
   heredados, falla cerrado en lugar de despacharlos o reescribirlos.
2. Aplicar la migración antes de publicar la API:
   `dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api`.
   El esquema `Approval` es aditivo. Las migraciones de Approval **no tienen descenso destructivo**:
   `Down` lanza `NotSupportedException` a propósito. Revertir la aplicación conserva el esquema y
   toda la historia (casos, tasks, decisiones, audit y outbox); no se autoriza una migración
   descendente que borre evidencia.
3. Publicar la API. El worker productivo (`ApprovalWorkflowWorker`) arranca con la aplicación en
   entornos distintos de `Testing`, ejecuta el preflight y, si este falla, detiene el host.
4. Habilitar submissions solo después de que los adapters y el resolver de SPEC 01 estén
   registrados: sin resolver o con registro ambiguo el módulo falla cerrado (`503`).

## Contratos y versiones

- `approval-canonical-json/v2` es la única canonicalización publicable. Un cambio de preimagen,
  orden o claves exige una nueva `canonicalization_version`; un cambio de schema del documento,
  subir `policy_schema_version`.
- `approval-result/v2` es la única versión de resultados. Cada evento publica `result_source`
  `{id,key,type}` (`APPROVAL_REQUIREMENT` o `EXTERNAL_PREREQUISITE`) y, cuando proviene de una
  decisión, `decision_digest` es exactamente el `workflow_decision_digest` persistido (nunca el
  `decision_fingerprint`). No hay dual-write ni reetiquetado de payloads v1: un evento v1 bloquea
  el rollout y solo se recupera desde una base limpia o restaurada al backup pre-Approval.
- Los consumidores deduplican por `event_id + contract_version`; el dispatcher entrega al menos
  una vez y nunca reescribe el payload.

## Worker y operación automática

- El worker recorre todas las organizaciones cada cinco segundos y:
  - despacha los eventos de outbox vencidos del dispatcher;
  - procesa las corridas de reconciliación pendientes bajo su lease.
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
  de cada efecto automático.

## Health

`/health/approval` degrada cuando:

- existe un dead letter;
- el evento pendiente más antiguo supera 5 minutos;
- existe una corrida de reconciliación sin completar dentro de su presupuesto de 60 segundos.

Las etiquetas son `APPROVAL_DEAD_LETTER`, `APPROVAL_BACKLOG_OVERDUE` y
`APPROVAL_RECONCILIATION_OVERDUE`.

## Recuperación

- **Lease abandonado o crash del worker:** no se interviene: el siguiente barrido reclama la misma
  corrida cuando el lease expira y continúa desde el cursor. El fencing token impide que el holder
  anterior confirme cambios.
- **Dead letter:** `ADMIN` decide si reejecuta con replay; el payload permanece byte a byte.
- **Evento v1 detectado:** detener el despliegue, restaurar la base desde el backup pre-Approval y
  reintentar. No existe consumer v1 autorizado, recuperación automática ni reescritura de filas.
- **Migración aplicada por error:** no se revierte con `database update` (Down lanza); se corrige
  hacia adelante y se conserva el esquema.
- **Reversión de la aplicación:** antes del primer tráfico v2 se puede volver a la versión previa
  conservando el esquema vacío. Después de persistir un resultado v2 se deshabilitan submissions,
  se mantiene el dispatcher/consumer v2 y se corrige hacia adelante; nunca se vuelve a una versión
  incapaz de consumir los eventos ya emitidos.
