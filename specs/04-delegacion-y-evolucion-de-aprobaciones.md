# SPEC 04 — Delegación y evolución de aprobaciones

> **Formato:** sdd/v3
> **Estado:** Borrador
> **Ejecución:** No iniciada
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** Pendiente
> **Fecha:** 2026-09-11
> **Actualizada:** 2026-09-11
> **Aprobada el:** Pendiente
> **Aprobada por:** Pendiente
> **Objetivo:** Permitir ausencias y cambios materiales mediante delegaciones acotadas, supersesión, carry-forward estricto y revocación de evidencia sin ampliar autoridad ni reescribir decisiones históricas.
> **Depende de:** SPEC 01, SPEC 03
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

SPEC 03 entrega casos, assignments y decisiones humanas inmutables, pero un proceso real también debe sobrevivir vacaciones, revocaciones de autoridad y nuevas versiones del documento. La fuente funcional exige que toda delegación tenga vigencia y audit, que el delegado nunca reciba autoridad superior a la propia y que un cambio material no conserve una aprobación de forma heurística.

Esta spec extiende el núcleo sin acoplarlo a Policy ni a dominios transaccionales concretos. La integración de quotation waiver y sus wire contracts queda en SPEC 05.

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

- **REQ-01 — Delegación programada, autorizada e idempotente.** El delegante debe estar activo y poseer al crear un `RoleAssignment` vigente que cubra por completo el rol y `DecisionScopeDescriptor` delegados. Puede crear o revocar su delegación; `ADMIN` puede hacerlo por indisponibilidad operativa con motivo obligatorio. Cada comando exige `delegation_command_key`, única en `(organization_id, actor_type, actor_id, key)`, y fingerprint canónico de acción `CREATE|REVOKE`, delegante, delegado, role, scope, intervalo, motivo y versión esperada; excluye UTC/correlation del servidor. Replay idéntico devuelve el mismo registro y otra carga da `409`. La delegación identifica delegante, delegado, role, scope, `valid_from` inclusivo, `valid_to` exclusivo, estado y versión. Se rechazan auto-delegación, intervalos inválidos, cadenas y periodos activos solapados para el mismo delegante/rol/scope.

- **REQ-02 — Autoridad no transferible y routing efectivo.** Para un requirement cubierto por una delegación vigente se elimina al delegante del conjunto directo y se agrega al delegado solo si este también aparece por mérito propio en el resultado original de `IOrganizationEligibilityService`; los usuarios efectivos se deduplican. La regla de menor carga y desempate de SPEC 03 opera sobre ese conjunto, sin preferencia automática por el delegado. El delegado debe cubrir por sí mismo role, authority, nivel, límite, scope y vigencia, y nunca puede superar ninguno mediante la delegación.

- **REQ-03 — Reconciliación de delegaciones.** Activación, revocación o expiración dispara una reconciliación idempotente de tasks `PENDING` cubiertas. Una assignment inválida se libera y se vuelve a resolver con el conjunto efectivo; puede quedar `UNASSIGNED`. El worker usa reloj de servidor, leases y checkpoint persistente, procesa en máximo 60 segundos y conserva cada assignment anterior con causa y delegación aplicada. Las decisiones terminales no se reasignan ni reinterpretan.

- **REQ-04 — Nueva versión y supersesión determinista.** Un workload propietario puede abrir una nueva versión que referencia el caso previo y sus targets reemplazados, con `submission_key`, fingerprint y versión esperada. La operación atómica crea el caso nuevo, cambia el anterior a `SUPERSEDED` y lleva todos sus requirements, prerequisites y tasks no terminales a `SUPERSEDED`; estos estados son terminales, no cuentan para completion ni carga y nunca reabren. Las decisiones terminales previas permanecen inmutables. El caso nuevo recrea requirements para todos sus targets: solo decisiones aprobadas que cumplan REQ-05 nacen como carry-forward; el resto crea tasks nuevas conforme a SPEC 03. Tras `REQUEST_CHANGES`, únicamente este caso enlazado puede continuar. Un prerequisite `FAILED` se corrige mediante el nuevo caso, no reabriendo el nodo anterior. La transacción emite `CASE_SUPERSEDED` y resultados por cada target afectado; un fallo revierte caso nuevo, estados y outbox juntos.

- **REQ-05 — Carry-forward por igualdad canónica.** Una aprobación previa se conserva únicamente cuando coinciden organization, subject y target estable, versión/digest material, requirement descriptor, role, authority, scope, dependencias, exclusiones y acciones permitidas. Se crea una nueva `ApprovalDecision` terminal con `action=APPROVE`, `origin=CARRY_FORWARD`, id/digest nuevos y referencia a la decisión humana anterior; el requirement nuevo nace `APPROVED` sin task y puede habilitar dependientes. `DecisionCarryForwardRecord` conserva decisiones previa/nueva, caso/requirement nuevos, targets y digests comparados. Cualquier diferencia o incertidumbre exige una task y decisión humanas nuevas. Los adapters pueden declarar tipos no transferibles; SPEC 05 lo exige para `POLICY_EXCEPTION`.

- **REQ-06 — Revocación de evidencia y operación sin bypass.** Solo el workload propietario puede revocar evidencia porque el sujeto o binding dejó de ser válido; `ADMIN` puede hacerlo únicamente como contención de incidente. Ambos requieren motivo, versión esperada y `revocation_key`, única por `(organization_id, evidence_id, key)`. La transacción crea un `DecisionEvidenceRevocation` append-only, invalida verificaciones futuras y emite evento, pero no modifica la decisión ni revierte efectos consumidos. Replay idéntico devuelve la revocación; otro fingerprint da `409`. Nadie puede reactivar evidencia, cambiar una acción terminal, crear carry-forward manual ni escoger assignee.

- **REQ-07 — Historia, visibilidad y auditoría.** El aprobador consulta delegaciones propias y assignments históricos; el originador o workload owner ve la cadena de casos y resultados; `AUDITOR` organizacional lee delegaciones, supersesiones, carry-forward y revocaciones; `ADMIN` opera reconciliación y contención sin acceder automáticamente al documento origen. Fuera del scope visible se responde `404`. Crear/revocar delegación, reasignar, superseder, carry-forward y revocar evidencia genera audit append-only con actor, UTC, motivo, versiones, scope, before/after o subcambios y correlation.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `ApprovalDelegation` | delegante, delegado, role, scope, vigencia, estado, versión, command key/fingerprint, actor, motivo | No concede authority; replay exacto; sin cadenas, solapes ni auto-delegación. |
| `CaseSupersession` | caso anterior/nuevo, subject versions, targets reemplazados, key/fingerprint, actor, UTC | Append-only; caso/nodos anteriores quedan terminales `SUPERSEDED`. |
| `DecisionCarryForwardRecord` | decisión previa/nueva, caso/requirement nuevo, targets, digests comparados, workload, UTC | Siempre acompaña una nueva decisión `APPROVE/CARRY_FORWARD`. |
| `DecisionEvidenceRevocation` | evidence/decision, revocation key/fingerprint, actor type/id, UTC, reason, expected version | Append-only, irreversible e idempotente. |
| `ApprovalAssignment` extendido | delegation id/version opcional, causa de asignación/liberación | Conserva la prueba de conjunto efectivo y carga. |
| `ApprovalAuditRecord` extendido | acción, actor, scope, versiones, before/after o subcambios, motivo, correlation | No contiene snapshots completos ni PII innecesaria. |

Reglas adicionales:

- `valid_from` es inclusivo, `valid_to` exclusivo y `valid_to > valid_from`; ambos son instantes UTC.
- Dos intervalos se solapan si comparten cualquier instante efectivo; extremos adyacentes `[a,b)` y `[b,c)` son válidos.
- Una delegación se aplica solo cuando role y todos los scopes del requirement están cubiertos por el mismo registro.
- El fingerprint de delegación cubre versión canónica, acción, actor, delegante/delegado, role, scope, intervalo, motivo NFC y expected version.
- El fingerprint de supersesión cubre caso anterior, nueva referencia de sujeto/snapshot, targets reemplazados, materialidad declarada y nueva submission.
- El fingerprint de revocación cubre versión canónica, evidence id/version, actor type/id, motivo NFC y `revocation_key`; todos excluyen UTC/correlation del servidor.
- La decisión `CARRY_FORWARD` usa `approval-canonical-json/v1` de SPEC 03 y hashea su referencia histórica y prueba de igualdad.

## Migración, despliegue y reversión

- La migración es aditiva sobre el schema Approval de SPEC 03: añade delegaciones, supersesiones, carry-forward, revocaciones y checkpoints sin reescribir decisiones existentes.
- Se despliega primero la migración y después workers/API. Hasta habilitar workers, no se crean delegaciones; una configuración parcial no debe dejar una delegación activa sin reconciliación.
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
| Unitaria | Intervalos, scopes, conjunto efectivo, materialidad, fingerprints, carry-forward y revocación | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.UnitTests/` |
| Integración | SQL Server, solapes, carreras, workers, checkpoints, outbox y append-only | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | Delegante, `ADMIN`, `AUDITOR`, workload owner, supersesión, revocación y visibilidad | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato | Adapters controlados declaran materialidad y tipos no transferibles | Pruebas de contrato dentro de la solución |
| Operación | Fake clock, dos workers, restart, SLA de 60 s, health y reversión conservadora | SQL Server efímero y procedimiento automatizado |

Todos los criterios se verifican automáticamente. Casos negativos: delegado no elegible, cadena, solape, expiración durante carrera, cambio de digest, target parcial, carry-forward de tipo prohibido, revocación duplicada distinta y lectura fuera de scope.

## Decisiones

- **DEC-01 — Delegación transforma routing, no autoridad.** El delegado debe ser candidato directo por mérito propio; se descartan transferencia de grants y preferencia automática.
- **DEC-02 — Sin cadenas ni solapes.** Release 1 admite un solo salto y periodos inequívocos para mantener determinismo y auditabilidad.
- **DEC-03 — Nueva decisión para carry-forward.** El caso nuevo conserva resultado mediante una decisión derivada con digest propio, no reutilizando el id anterior ni ocultando historia.
- **DEC-04 — Igualdad declarada y canónica.** El adapter propietario aporta materialidad versionada, pero el workflow compara todos los campos contractuales; ante duda exige aprobación nueva.
- **DEC-05 — Revocación no reescribe hechos.** La decisión ocurrió y permanece; solo se invalida su uso futuro mediante un registro irreversible.

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
- **T-06 — Pruebas y operación.** Cubrir toda la estrategia, concurrencia multiinstancia, restart, health, despliegue y reversión. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, NFR-01, NFR-02, NFR-03, NFR-04, CA-01, CA-02, CA-03, CA-04, CA-05, CA-06, CA-07.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra delegación, evolución y revocación sin depender de Policy.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01 | Solo delegante con role/scope vigente o `ADMIN` con motivo crea/revoca; auto-delegación, cadena, solape e intervalo inválido fallan sin cambios parciales. Replay de `delegation_command_key` idéntico devuelve el mismo registro y otro fingerprint da `409`. | Automática: matriz de intervalos/scopes, fingerprints e integración SQL concurrente. |
| CA-02 | REQ-01, REQ-02 | Durante vigencia se retira al delegante y solo se añade al delegado elegible; menor carga sigue aplicando. Un delegado sin authority, límite o scope suficiente nunca recibe ni decide. | Automática: resolver de SPEC 01, fake clock y pruebas negativas. |
| CA-03 | REQ-03, NFR-01, NFR-03 | Activar, revocar o expirar reasigna o deja `UNASSIGNED` en máximo 60 segundos; dos workers no duplican assignment, cada liberación conserva assignment/audit/evento y una decisión terminal permanece intacta. | Automática: integración multiinstancia con checkpoint, outbox y reloj controlado. |
| CA-04 | REQ-04, NFR-02 | Nueva versión crea atómicamente el caso nuevo, deja caso/nodos anteriores terminales `SUPERSEDED`, no los cuenta para carga/completion y emite outbox por target; no reabre `FAILED` ni continúa el mismo caso tras `REQUEST_CHANGES`. Replay devuelve el mismo caso y otra carga da `409`. | Automática: state machine, fallo forzado y matriz SQL de caso/targets/outbox. |
| CA-05 | REQ-05, NFR-02 | Igualdad exacta crea decisión nueva `APPROVE/CARRY_FORWARD`, record, requirement aprobado sin task y evento; cambiar cualquier digest/descriptor/exclusión exige task nueva y un tipo prohibido no se conserva. | Automática: tabla de materialidad, golden y constraints. |
| CA-06 | REQ-06, NFR-02, NFR-03 | Revocación owner o `ADMIN` por incidente invalida verificaciones futuras sin alterar decisión; revocación, audit y evento confirman atómicamente; replay idéntico no duplica y otro fingerprint da `409`; reactivación no existe. | Automática: dominio, fallo transaccional, outbox y API/E2E. |
| CA-07 | REQ-07, NFR-04 | Cada mutación produce audit minimizado; aprobador, originador, `AUDITOR` y `ADMIN` ven únicamente su scope y ninguna superficie permite seleccionar assignee o cambiar una terminal. | Automática: API/E2E de permisos y captura de telemetría. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Delegación amplía autoridad | Delegado sin grant recibe o decide | Elegibilidad independiente y CA-02. |
| Expiración tardía | Task sigue asignada pasados 60 segundos | Worker con checkpoint, health y CA-03. |
| Cambio material reutiliza aprobación | Digest distinto genera carry-forward | Comparación completa y CA-05. |
| Supersesión cancela targets ajenos | Nueva versión afecta decisiones independientes | Mapping explícito por target y CA-04. |
| Revocación borra historia | Decisión muta o desaparece | Registro append-only y CA-06. |
| Bypass administrativo | `ADMIN` crea carry-forward o decide | Comandos separados y CA-07. |
