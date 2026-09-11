# SPEC 03 — Núcleo de casos y decisiones de aprobación

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** Bloqueada
> **Vigencia:** Pendiente
> **Revisión:** 2
> **Digest contractual:** 82ed4ebe75e522106d9b9b281feb3b9dd2a5702efece9b4d21c13ce3960e7299
> **Fecha:** 2026-09-10
> **Actualizada:** 2026-09-11
> **Aprobada el:** 2026-09-11
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Convertir requisitos tipados de dominios confiables en casos y tareas de aprobación asignados de forma determinista, con decisiones autorizadas, auditables y entregadas mediante eventos durables.
> **Depende de:** SPEC 01
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

La SPEC 01 implementó identidades, roles, Approval Authority, scopes y `IOrganizationEligibilityService`, que devuelve candidatos con `EligibilityEvidence` pero no selecciona personas ni crea tareas. Los dominios transaccionales y la SPEC 02 necesitan una frontera que conserve requirements por target, aplique dependencias, impida autoaprobación y produzca resultados durables sin mutar documentos ajenos.

El borrador anterior de la SPEC 03 mezclaba ese núcleo con delegaciones, evolución de versiones, operación avanzada y el adapter completo de Policy. Esta revisión conserva el número 03 para el primer entregable verificable. La delegación y evolución quedan en SPEC 04; la integración con Policy y quotation waiver, en SPEC 05.

## Alcance

### Incluye

- Ingreso idempotente de casos y requirements desde adapters internos registrados y workloads allowlisted.
- Scopes tipados, targets versionados, dependencias entre approvals y prerrequisitos externos.
- Asignación automática al candidato elegible con menor carga, estado `UNASSIGNED` y reconciliación ante pérdida de elegibilidad.
- Exclusiones inmutables de Segregation of Duties, sin bypass de `ADMIN`.
- Acciones `APPROVE`, `REJECT` y `REQUEST_CHANGES` con revalidación de autoridad, concurrencia e idempotencia.
- Bandeja mínima del assignee, estado del caso para su originador o workload propietario y lectura organizacional de auditoría.
- Persistencia SQL Server, evidencia canónica, audit append-only y outbox de resultados por target.
- Cancelación de casos abiertos por el workload propietario, errores, límites, health y pruebas del núcleo.

### No incluye

- Delegaciones, vacaciones, activación programada o reasignación causada por una delegación; pertenecen a SPEC 04.
- Nueva versión de un caso, supersesión o carry-forward de decisiones; pertenecen a SPEC 04.
- Revocación de evidencia ya emitida y vistas operativas avanzadas; pertenecen a SPEC 04.
- Adapter de controles de SPEC 02, secuencia específica Department/Finance/Procurement o quotation waiver; pertenecen a SPEC 05.
- Purchase Requests, Cost Centers, budgets, proveedores, sourcing, POs, invoices, matching o payments.
- Quórums, votación, grupos, selección manual de assignee, escalamiento por SLA o notificaciones externas.
- Diseñador visual, scripts, expresiones arbitrarias, interfaz frontend o mutaciones sobre documentos origen.

## Comportamiento esperado

- **REQ-01 — Ingreso confiable e idempotente.** Un adapter in-process registrado exactamente una vez por `subject_type + operation + contract_version`, invocado por un workload autenticado y allowlisted, puede abrir un `ApprovalCase` para una organización y versión inmutable de sujeto. El comando incluye `submission_key`, referencia y digest del snapshot origen, `requester_id` cuando exista, `originator_id`, exclusiones SoD, requirements y prerrequisitos completos. La key es única en `(organization_id, workload_issuer, workload_client_id, subject_type, operation)`; repetir el mismo fingerprint devuelve el mismo caso sin resolver ni crear de nuevo y reutilizar la key con otro contenido devuelve `409`. Un caller HTTP de usuario no puede autocertificar requirements, scopes, exclusiones ni digests.

- **REQ-02 — Requirements, scopes y targets conservados.** Cada `ApprovalRequirement` contiene key estable, tipo, `stage_code`, rol, authority, `DecisionScopeDescriptor`, acciones, dependencias, exclusiones y targets. El descriptor usa JSON `decision-scope/v1` con propiedades exactas `{schema_version, organization_id, scopes}`; cada scope contiene `{dimension, reference_id, reference_version}`. `ORGANIZATION` exige referencias nulas y no se combina; `LEGAL_ENTITY` y `DEPARTMENT` exigen UUID y versión positiva activos. `COST_CENTER` permanece rechazado hasta que su catálogo extienda SPEC 01. Cada target usa `{type,id,version,material_snapshot_digest}`. Un requirement agrupa targets solo si descriptor, dependencias, acciones, decisión y exclusiones son idénticos; conserva `source_requirement_key` y una `workflow_requirement_key` estable.

- **REQ-03 — Grafo y prerrequisitos externos.** Las dependencias forman un DAG dentro del mismo caso. Cada edge declara predecessor `APPROVAL|EXTERNAL`, key, modo `ALL` y un conjunto no vacío de targets completos que pertenece a ambos nodos. Solo se satisface cuando todos esos targets están `APPROVED` o `SATISFIED`; coincidencias parciales o de otra versión/digest no habilitan tareas. Un `ExternalPrerequisite` conserva owner adapter/version, source control/digest, parámetros tipados, targets y estado `WAITING|SATISFIED|FAILED|CANCELLED`. Solo su workload propietario cambia `WAITING` a `SATISFIED|FAILED` mediante `signal_key`, fingerprint y versión esperada. `FAILED` es inmutable y bloquea los descendientes afectados; corregirlo exige un caso nuevo de SPEC 04.

- **REQ-04 — Asignación determinista y ausencia de candidato.** Al habilitar un requirement, el workflow convierte `DecisionScopeDescriptor` a `AuthorizationScopeSet` e invoca `IOrganizationEligibilityService` con rol, authority, scope, instante UTC y exclusiones exactas. Entre candidatos directos elige menor número de tareas actuales `PENDING` en la organización y desempata por UUID canónico ascendente. Selección, reserva de carga y assignment se serializan. Si no hay candidato, la única tarea actual queda `UNASSIGNED` y el caso `BLOCKED`, sin fallback. Cambios confirmados de perfiles, roles, grants o scopes disparan una reconciliación idempotente que reasigna una tarea `PENDING` inválida o la devuelve a `UNASSIGNED`; una decisión terminal no cambia.

- **REQ-05 — Segregation of Duties fail-closed.** El contrato versionado del adapter declara actores obligatorios por operación y construye exclusiones inmutables. Toda aprobación empresarial exige `originator_id`; si existe un requester distinto, también se excluye. Un actor excluido no puede ser candidato, assignee ni decisor. Campo obligatorio ausente, identidad vacía o exclusiones incompatibles con el adapter version se rechazan antes de persistir. `ADMIN`, replay o reconciliación no pueden reducir el conjunto.

- **REQ-06 — Decisión autorizada e inmutable.** Solo el assignee actual, activo y autenticado puede ejecutar `APPROVE`, `REJECT` o `REQUEST_CHANGES` sobre una tarea `PENDING`. Exige motivo no vacío, versión esperada y `decision_key`, única en `(organization_id, actor_user_id, decision_key)`. Antes de confirmar se vuelve a resolver elegibilidad con los mismos inputs y reloj del servidor. Replay con fingerprint idéntico devuelve la decisión original aunque la tarea ya sea terminal; otra carga o una carrera incompatible devuelve `409`. Acción, targets, actor, UTC, motivo y `EligibilityEvidence` fresca son append-only.

- **REQ-07 — Estados, propagación y cancelación.** `ApprovalCase` transita `OPEN → BLOCKED|COMPLETED|CANCELLED`; un bloqueo por `UNASSIGNED` puede volver a `OPEN`, mientras un prerequisite `FAILED` solo permite cancelación hasta SPEC 04. Un requirement transita `WAITING → UNASSIGNED → PENDING → APPROVED|REJECTED|CHANGES_REQUESTED`, y cualquier estado no terminal puede terminar `CANCELLED`; la task refleja desde `UNASSIGNED` y no reabre. `APPROVE` habilita edges de sus targets; `REJECT` y `REQUEST_CHANGES` cancelan solo descendientes enlazados. El caso completa cuando todos los requirements vigentes son terminales y todo prerequisite está `SATISFIED`. Solo el workload propietario puede cancelar un caso abierto con motivo y versión esperada; la cancelación no borra decisiones ni compensa efectos consumidos.

- **REQ-08 — Evidencia canónica y durable.** `approval-canonical-json/v1` serializa UTF-8 sin BOM ni whitespace, propiedades conocidas presentes y ordenadas ordinalmente, strings NFC, UUID `D` minúsculo, timestamps UTC con siete decimales, enums en mayúsculas, decimales invariantes y sets ordenados por bytes canónicos sin duplicados. Conserva preimages de `submission_fingerprint`, `decision_fingerprint`, `signal_fingerprint`, `authority_evidence_digest` y `workflow_decision_digest`. El digest de decisión liga caso/sujeto/snapshot, requirement/descriptor, targets, acción, actor, motivo, instante, exclusiones y evidencia de elegibilidad. Cada transición observable crea audit y un `ApprovalOutboxEvent` por target en la misma transacción.

- **REQ-09 — API, visibilidad, errores y límites.** Un usuario activo consulta sus tareas `PENDING` y su historia de decisiones; el originador o workload propietario consulta su caso; `AUDITOR` organizacional lee casos, assignments, decisiones y evidencia; `ADMIN` ve `UNASSIGNED` y dispara reconciliación, pero no decide ni elige assignee. Fuera del alcance visible se responde `404`. Se usan Problem Details: `401`, `403`, `404`, `400`, `409`, `413`, `422` y `503 /problems/approval-dependency-unavailable`. Un caso admite hasta 2.000 requirements, 10.000 asociaciones a targets y 5 MiB canónicos; keys y códigos usan los límites y alfabetos declarados en Datos y contratos.

- **REQ-10 — Exactly-one bajo concurrencia y operación segura.** Existe como máximo una task actual por requirement y exactamente una decisión terminal por `(requirement_id, target completo)` al finalizar. Ingreso, assignment, decisión, señal, cancelación y reconciliación usan índices, leases y transacciones multiinstancia. Outbox entrega al menos una vez con `event_id + contract_version`; reintenta a 1 s, 5 s, 30 s, 2 min y 10 min hasta 10 intentos, conserva `DEAD_LETTER` y permite replay administrativo sin editar payload. Health degrada si el evento pendiente más antiguo supera 5 minutos, una reconciliación debida supera 60 segundos o existe dead letter. Tras timeout el caller debe reintentar con su key.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `ApprovalCase` | organización, subject type/id/version, operation, snapshot digest, workload, key/fingerprint, estado, versión | No contiene el documento origen; estados de REQ-07. |
| `DecisionScopeDescriptor` | schema version, organización, scopes tipados/versionados | Contrato exacto `decision-scope/v1`; referencias validadas contra SPEC 01. |
| `ApprovalRequirement` | source/workflow key, tipo, stage, role, authority, descriptor, exclusiones, dependencias, targets, estado | Targets indivisibles y máximo una task actual. |
| `ExternalPrerequisite` | owner/version, control/digest, parámetros, targets, estado, signal key/fingerprint, versión | Solo el owner señala; `FAILED` no se reabre. |
| `ApprovalTarget` | type, id, version, material snapshot digest | Identidad completa usada por edges, decisiones y eventos. |
| `ApprovalTask` | requirement, targets, estado, assignment actual, versión | `UNASSIGNED`, `PENDING` y terminales de REQ-07. |
| `ApprovalAssignment` | task, assignee, UTC de alta/baja, carga, eligibility evidence, causa | Append-only; uno actual por task. |
| `ApprovalDecision` | task/requirement, targets, action, actor, reason, UTC, key/fingerprint, evidence, digests | Append-only; origen `HUMAN` en esta spec. |
| `ApprovalOutboxEvent` | id, contract version, caso/sujeto, requirement, target, resultado, decisión/digest, attempts/state | Consumer deduplica por `event_id + contract_version`. |
| `ApprovalAuditRecord` | actor, UTC, acción, objetivo/versiones, scope, motivo, before/after, correlation | Append-only y minimizado. |

Reglas adicionales:

- `submission_key` y `decision_key` miden 1–128 caracteres `[A-Za-z0-9._:-]`; códigos miden 1–128 ASCII `[A-Z][A-Z0-9_.:-]*`; motivos, 1–1.000 Unicode scalars.
- `signal_key` es única en `(organization_id, external_prerequisite_id, key)` y cada reuse exige fingerprint idéntico.
- La carga cuenta solo tasks `PENDING`; una task agrupada cuenta como una unidad y no se rebalancea por cambios posteriores de carga.
- La decisión agrupada no se divide; respuestas distintas requieren requirements distintos antes de decidir.
- `valid_from` es inclusivo y `valid_to` exclusivo cuando aparezcan en evidencia de authority.
- Adapters y eventos se versionan; una versión desconocida se rechaza fail-closed.

## Migración, despliegue y reversión

- La persistencia se añade bajo un schema explícito de Approval y no modifica tablas de Organization o Policy. Las referencias a dominios futuros son identidades/versiones tipadas, no FKs a tablas inexistentes.
- Se aplica primero la migración, luego la aplicación con resolver y adapters controlados y finalmente se habilitan submissions. Sin resolver o con registro de adapters ambiguo, el módulo falla cerrado.
- Dispatcher y reconciliador usan leases persistentes; tras restart recuperan trabajo pendiente. Solo `ADMIN` puede reintentar dead letters o reconciliaciones sin editar datos contractuales.
- Revertir la aplicación conserva casos, tasks, decisiones, audit y outbox. No se autoriza migración descendente destructiva; la recuperación vuelve a una versión compatible y corrige hacia adelante.
- Antes de tráfico se prueban caso simple, DAG, `UNASSIGNED`, decisión concurrente, cancelación, restart y replay.

## Seguridad y privacidad

- Usuarios usan JWT y autorización local de SPEC 01; claims empresariales del IdP no conceden tasks ni decisiones. Submissions, señales y cancelación exigen workload allowlisted.
- El decisor debe ser assignee actual y seguir siendo elegible en la transacción. Assignment previo, `ADMIN`, Job Title o posesión de un enlace no bastan.
- Exclusiones SoD forman parte del requirement y del digest, y no se reducen durante assignment, reconciliación o replay.
- Bandeja, respuestas y telemetría omiten tokens, subjects de IdP, snapshots completos, saldos, documentos y PII innecesaria.
- `ADMIN` restaura operación, pero no crea evidencia de authority, selecciona assignee ni cambia decisiones terminales. `AUDITOR` nunca muta.

## Requisitos no funcionales

- **NFR-01 — Determinismo reproducible.** El mismo comando canónico, snapshot de elegibilidad/carga y reloj produce el mismo assignment, decisión, bytes y digests; un replay devuelve los artefactos originales.
- **NFR-02 — Atomicidad e inmutabilidad.** Assignment, decisión, audit y outbox correspondientes se confirman o revierten juntos; requirements, decisiones y assignments liberados no se sobrescriben.
- **NFR-03 — Consistencia de autoridad.** Una decisión revalida estado confirmado y una revocación de rol/grant no se prolonga mediante caché; una reconciliación debida ocurre en máximo 60 segundos.
- **NFR-04 — Disponibilidad segura.** Fallos de resolver, adapter, persistencia o ambigüedad nunca degradan a aprobación ni conservan silenciosamente un assignee inválido.
- **NFR-05 — Observabilidad minimizada.** Submissions, assignments, `UNASSIGNED`, decisiones, conflictos, reconciliación y dispatch emiten métricas, logs y trazas OpenTelemetry sin motivos completos, snapshots ni PII.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | DAG, state machines, scopes, agrupación, menor carga, SoD, fingerprints, canonicalización y límites | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.UnitTests/` |
| Integración | SQL Server, índices exactly-one, carreras, reconciliación, atomicidad, outbox y migración | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | JWT/workload, bandeja, decisiones, señales, cancelación, visibilidad y Problem Details | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato | Adapters controlados, resolver de SPEC 01 y eventos duplicados | Pruebas de contrato dentro de la solución |
| Operación | Restart, leases, health, dead letter, replay y reversión conservando historia | SQL Server efímero y procedimiento operativo automatizado |

Todos los criterios se verifican automáticamente. Los casos negativos incluyen actor excluido, authority revocada entre assignment y decisión, cero candidatos, dependencia cíclica, target de otra versión, task stale, doble decisión, replay distinto, evento duplicado y fallo entre decisión, audit y outbox.

## Decisiones

- **DEC-01 — Núcleo genérico con adapters tipados.** El workflow no conoce estados internos de PR, Supplier, Invoice o Payment; cada dominio aporta subjects, requirements, exclusiones y proyección mediante contratos versionados.
- **DEC-02 — Menor carga con desempate estable.** Se elige entre candidatos directos válidos por tareas `PENDING` y UUID; se descartan bandeja compartida y selección manual.
- **DEC-03 — Elegibilidad en assignment y decisión.** Se conserva evidencia de ambos instantes; no se confía indefinidamente en el assignment.
- **DEC-04 — Dependencias explícitas, no diseñador.** Un DAG acotado enlaza approvals y controles externos; `stage_code` solo etiqueta.
- **DEC-05 — El dominio propietario proyecta estado.** El workflow emite resultados durables y no modifica documentos ajenos.
- **DEC-06 — División por entregables.** Esta spec entrega el núcleo; SPEC 04 añade delegación/evolución y SPEC 05 integra Policy/waiver para mantener contratos verificables y revisables por separado.

## Plan de implementación

### Bloque 1 — Casos y grafo

- **T-01 — Ingreso, contrato y evidencia de frontera.** Implementar adapters exact-one, idempotencia, `decision-scope/v1`, targets, agrupación, límites y validaciones. Añadir unitarias de schema/fingerprint/límites, integración SQL de unicidad/replay, contrato del adapter controlado y API/E2E de workload no allowlisted (`403`) y límites canónicos (`413`). Cubre: REQ-01, REQ-02, REQ-09, REQ-10, CA-01, CA-02.
- **T-02 — DAG, lifecycle y evidencia de transición.** Implementar dependencias humanas/externas, señales, propagación, estados, cancelación y sus superficies de workload. Añadir tabla unitaria de DAG/state machine, integración de propagación/outbox y API/E2E de signals, cancelación y conflictos. Cubre: REQ-03, REQ-07, REQ-10, NFR-02, NFR-04, CA-03, CA-06.

**Resultado verificable:** las suites unitarias, de integración y contrato demuestran que un adapter crea una sola vez un caso válido; API/E2E demuestra que señales y cancelación habilitan o bloquean exclusivamente los targets correctos.

### Bloque 2 — Routing y decisiones

- **T-03 — Assignment, reconciliación y evidencia de autoridad.** Integrar `IOrganizationEligibilityService`, menor carga, exclusiones, `UNASSIGNED`, reconciliación y su operación administrativa. Añadir unitarias deterministas con fake clock, pruebas de contrato contra el resolver de SPEC 01, integración SQL multi-DbContext y API/E2E de actor excluido, role revocado, `ADMIN` y cero candidatos. Cubre: REQ-04, REQ-05, REQ-09, REQ-10, NFR-01, NFR-03, NFR-04, CA-04, CA-05, CA-08.
- **T-04 — Decisión, evidencia y pruebas de atomicidad.** Implementar acciones y superficie del assignee, revalidación, idempotencia, canonicalización, digests y atomicidad. Añadir golden vectors unitarios, integración con carreras/fallo forzado y API/E2E de replay, versión obsoleta, decisión concurrente y `403` administrativo. Cubre: REQ-06, REQ-08, REQ-09, REQ-10, NFR-01, NFR-02, CA-05, CA-06, CA-07, CA-08.

**Resultado verificable:** las pruebas de routing demuestran el assignee determinista y la reconciliación en 60 segundos; las de decisión prueban que solo el assignee aún elegible decide una vez y que decisión, audit y outbox son atómicos y reproducibles.

### Bloque 3 — Persistencia y entrega

- **T-05 — Schema, workers y evidencia operativa.** Mapear agregados append-only, índices, rowversion, leases, dispatcher, retries, health y migración aditiva. Añadir integración Testcontainers de exactly-one/recovery, contrato de consumer ante eventos duplicados, pruebas operativas de dos instancias, restart, backlog, dead letter, replay y reversión, y captura de métricas/logs/trazas de workers sin PII. Cubre: REQ-04, REQ-07, REQ-08, REQ-10, NFR-02, NFR-03, NFR-04, NFR-05, CA-04, CA-06, CA-07, CA-08.

**Resultado verificable:** SQL Server y los escenarios operativos prueban que dos instancias no duplican assignments, decisiones o eventos y recuperan outbox/reconciliación tras restart.

### Bloque 4 — Lectura y observabilidad

- **T-06 — Bandeja, visibilidad y evidencia HTTP.** Exponer bandeja mínima, estado de caso, historia y auditoría con autorización, Problem Details y telemetría minimizada. Añadir API/E2E de assignee, originador, workload, `AUDITOR`, `ADMIN`, ocultación `404`, categorías de error y captura de métricas/logs/trazas HTTP sin PII. Cubre: REQ-09, REQ-10, NFR-04, NFR-05, CA-08.

**Resultado verificable:** API/E2E demuestra visibilidad mínima, permisos y errores; `dotnet test ProcureToPay.sln --no-restore` agrega la evidencia distribuida de los cuatro bloques sin depender de SPEC 04 ni SPEC 05.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-09, REQ-10 | Un workload y adapter exact-one crean una vez; replay idéntico devuelve ids, payload distinto da `409`, caller no allowlisted `403` y límites ±1 producen aceptación/`413`. | Automática: unitarias de fingerprint/límites, integración SQL y API/E2E. |
| CA-02 | REQ-02 | `decision-scope/v1` exacto mapea Organization/Legal Entity/Department y preserva versiones; texto, campos extra, `COST_CENTER` y targets incompletos fallan. Solo se agrupan targets con contratos idénticos. | Automática: schema, catálogo, grouping y serialización. |
| CA-03 | REQ-03, REQ-07 | Un DAG válido habilita por target completo; ciclos o referencias ajenas fallan. Señal owner idempotente satisface o bloquea; `FAILED` no reabre y el caso no completa mientras exista prerequisite pendiente. | Automática: tabla de DAG/signals y API/E2E con adapter controlado. |
| CA-04 | REQ-04, REQ-10, NFR-01, NFR-03 | Cargas `0,1,1` eligen `0`; empate elige menor UUID. Cero candidatos crea `UNASSIGNED`; cambios de autoridad reasignan o bloquean en 60 segundos sin doble current assignment. | Automática: fake clock, resolver de SPEC 01 y carrera multi-DbContext. |
| CA-05 | REQ-05, REQ-06 | Originator/requester excluidos nunca son candidatos ni decisores. Solo el assignee todavía elegible decide con motivo/key/version; role revocado falla y `ADMIN` recibe `403`. | Automática: unitarias SoD y API/E2E de revocación/concurrencia. |
| CA-06 | REQ-06, REQ-07, REQ-10, NFR-02 | State machines rechazan reapertura; APPROVE habilita edges, REJECT/REQUEST_CHANGES cancelan solo descendientes enlazados y cancelación owner conserva historia. Decisión, audit y outbox son atómicos. | Automática: tabla exhaustiva, fallo forzado y consumer idempotente. |
| CA-07 | REQ-08, NFR-01, NFR-02 | Golden vectors fijan bytes y SHA-256 de submission, signal, authority y decisión; permutar sets no cambia digest, cambiar un campo sí, NFC se normaliza y duplicados se rechazan. | Automática: goldens independientes y replay de preimages. |
| CA-08 | REQ-09, REQ-10, NFR-04, NFR-05 | Assignee ve inbox mínima, originador su caso, `AUDITOR` evidencia y `ADMIN` operación sin decidir. Problem Details y telemetría no filtran PII; health refleja backlog, reconciliación y dead letter. | Automática: API/E2E con JWTs, captura de trazas y SQL Server efímero. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Assignment injusto o doble | Empates divergen o una carrera crea dos assignments | Reserva transaccional, UUID estable y CA-04. |
| Decisión con autoridad revocada | Un assignee decide tras perder role/grant/scope | Revalidación en transacción, reconciliación y CA-05. |
| Dependencia habilitada por target incorrecto | Coincide id pero no versión/digest | Referencia completa y CA-03. |
| Doble decisión o evento | Dos instancias confirman resultados incompatibles | Índices, rowversion, keys y outbox deduplicable. |
| `ADMIN` sustituye al negocio | Operación administrativa decide o elige assignee | API separada y CA-05/CA-08. |
| Exposición de datos sensibles | Bandeja o trazas incluyen documento o identidad innecesaria | Proyección mínima, allowlist y CA-08. |
