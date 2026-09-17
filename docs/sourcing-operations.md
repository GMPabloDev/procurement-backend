# Operación del módulo Sourcing (SPEC 10)

Runbook de despliegue y recuperación del sourcing, las RFQ, las cotizaciones y el award. No describe
comportamiento contractual nuevo: cada paso referencia el contrato aprobado
(`specs/10-sourcing-rfq-cotizaciones-award.md`).

## 1. Orden de despliegue

1. **Migración.** Aplicar las migraciones `Sourcing` en orden antes de publicar la API:
   `Spec10SourcingCore`, `Spec10SourcingEvaluation`, `Spec10SourcingSelection`, `Spec10SourcingWaiver`,
   `Spec10SourcingProposal`, `Spec10SourcingAward`, `Spec10SourcingOwners`. La migración no rellena
   owners, conteos ni awards históricos (REQ-13, §Migración).
2. **Configuración de storage.** `Storage:S3:BucketName`, `MaxUploadSizeBytes` (≥ 20 MiB para
   evidencia de cotización) y `TemporaryUrlLifetimeMinutes` **entre 1 y 15**: un valor mayor hace que
   la descarga de evidencia falle cerrado con `503` (REQ-14).
3. **Workloads.** Registrar la identidad `issuer + client_id` del dominio de sourcing en
   `Approval:Workloads:*` y en `Policy:Workloads:*`, y el binding owner→workload de los dos owners en
   `Approval:OwnerWorkloads:*`: `quotation-status-owner/v1` y `procurement-stage-owner/v1` con
   `ClientId = sourcing-domain`. Rotar credenciales no cambia estos identificadores (REQ-13).
4. **Registros de processor.** Insertar exactamente una fila por owner en
   `Sourcing.SourcingPrerequisiteProcessorRegistrations` con
   `{adapter_id, adapter_version=v1, processor_id=sourcing-domain, workload_issuer, workload_client_id}`
   coincidentes con el binding de `Approval:OwnerWorkloads`. Cero, dos o un workload distinto degrada
   readiness y ningún worker reclama (REQ-13).
5. **Habilitar comandos** (procesos, RFQ, cotizaciones, evaluación, selección, waiver, propuesta,
   award) y **después** los claims/signals de los owners. Hasta ese momento los prerequisites
   permanecen `WAITING`, nunca `SATISFIED` por ausencia (REQ-13).

## 2. Preflight antes de habilitar

- SPEC 01–09 integradas y `SourcingPrerequisiteProcessorRegistrations` con un registro por owner.
- `ISourcingPolicyFactProviderRegistry` resuelve exactamente
  `{subject_type=SOURCING_PROPOSAL, operation=SOURCING_PO, provider_id=sourcing-domain,
  contract_version=sourcing-policy-facts/v1}` y el adapter de propuesta existe una sola vez.
- Storage alcanzable (`IFileStorage.IsAvailableAsync`) y bucket con la extensión del artefacto.
- Ningún prerequisite `WAITING` previo cuyo payload no cumpla `v1`.
- Readiness `sourcing` en `Healthy`: un hallazgo bloquea la habilitación, no se infiere evidencia.

## 3. Readiness y razones tipadas

`GET /health/ready` incluye el check `sourcing`. Razones (sin ids, nombres, precios ni digests):

| Código | Significado | Acción |
| --- | --- | --- |
| `SOURCING_OWNER_REGISTRATION_UNAVAILABLE` | 0/2 registros de owner o workload distinto | Corregir §1.4 y reiniciar el worker |
| `SOURCING_FACT_PROVIDER_UNAVAILABLE` | 0/2 providers `SOURCING_PO` o contrato distinto | Revisar el registro de `ISourcingPolicyFactProvider` |
| `SOURCING_APPROVAL_ADAPTER_UNAVAILABLE` | 0/2 adapters de propuesta | Revisar el registro del adapter `sourcing-policy-approval-adapter/v1` |
| `SOURCING_ATTACHMENT_STORAGE_UNAVAILABLE` | bucket inaccesible o credencial inválida | Revisar bucket/rol; las descargas fallan `503` |
| `SOURCING_OWNER_BACKLOG_EXCEEDED` | attempt due > 60 s sin procesar | Ver §5 (reclaim) y capacidad del worker |

## 4. Backlog y leases

- Los attempts usan lease de 30 s renovado cada 10 s con fencing token; un lease vencido es
  reclamable por otra instancia y el token obsoleto no escribe (REQ-13).
- Un error técnico devuelve el attempt a `PENDING` con `LastErrorCode` (`SOURCING_*_UNAVAILABLE`) y
  reintento a 5 s; la insuficiencia de cotizaciones se registra como `SOURCING_MINIMUM_NOT_MET` y el
  prerequisite sigue `WAITING` (REQ-05, REQ-13).
- Para reiniciar el procesamiento de una cola atascada: confirmar que no hay otro holder
  (`LeaseOwner`, `LeaseExpiresAt`), y dejar que el lease expire en lugar de editar filas. **No** se
  editan estados a mano: la evidencia y los signals son append-only.

## 5. Recuperación de un claim interrumpido

1. Identificar el attempt por `PrerequisiteId` (sin volcar payloads).
2. Si `LeaseExpiresAt <= SYSUTCDATETIME()` y el estado no es terminal, el próximo ciclo del worker lo
   reclama con un fencing token nuevo. No hace falta intervención.
3. Si el proceso murió después de señalar, el attempt quedó en `SIGNALLING`: el siguiente ciclo
   encuentra el signal registrado por su clave y completa el attempt sin señalar dos veces.
4. Si el proceso fue **cancelado** con el sourcing pre-award, sus attempts quedan `ABANDONED` sin
   signal y los prerequisites de la PR siguen `WAITING` para un proceso nuevo (REQ-13).

## 6. Evidencia corrupta

- Los documentos persistidos (RFQ, cotización, evaluación, proposal, award, manifest, facts de waiver,
  evidencia de owners) se rehashean al leer: una discrepancia devuelve `503`
  `/problems/sourcing-dependency-unavailable` y no se sirve nada parcial (REQ-14).
- Un attachment confirmado es inmutable: si el objeto fue adulterado, la revisión `VALID` y la
  publicación fallan; se exige una cotización sucesora con evidencia nueva (REQ-03, REQ-04).
- Nunca se "repara" una fila append-only: la recuperación es hacia adelante (nueva versión).

## 7. Rollback

1. Deshabilitar nuevos comandos y los claims/signals de los owners.
2. Mantener readers y consumers de los contratos persistidos (RFQ, cotizaciones, evaluaciones,
   selecciones, propuestas, awards, evidencia y audit se conservan).
3. Restaurar los owners a fail-closed (sin registros de processor) para que ningún worker reclame.
4. Tras emitir signals o awards **no** se ejecuta downgrade destructivo: se corrige hacia adelante con
   una propuesta y un award sucesores (REQ-12).

## 8. Rotación de credenciales

La identidad de workload es `issuer + client_id` y estable ante rotación: rotar el secreto no cambia
el binding owner→workload ni el registro del processor. Un `client_id` aislado no autoriza signals
(REQ-13, §Seguridad y privacidad).
