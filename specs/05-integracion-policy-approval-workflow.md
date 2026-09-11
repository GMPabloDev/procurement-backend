# SPEC 05 — Integración de Policy con Approval Workflow

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
> **Objetivo:** Convertir controles persistidos de SPEC 02 en approvals y prerrequisitos de SPEC 03 y permitir que Policy verifique un quotation waiver únicamente contra una decisión autorizada, vigente y ligada a la evaluación exacta.
> **Depende de:** SPEC 02, SPEC 03, SPEC 04
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

SPEC 02 ya está implementada y produce `GeneratedControl`; mantiene un verifier default-deny hasta que exista Approval Workflow. SPEC 03 define casos, DAG, asignación, decisiones y evidencia; SPEC 04 agrega evolución y revocación. Falta el adapter que traduzca controles sin inventar semántica y el verifier HTTP que cierre el quotation waiver fail-closed.

La investigación detectó drift entre el contrato aprobado de SPEC 02 y su código actual: `PolicyApprovalDescriptor.DecisionScope` acepta tokens como `ORGANIZATION`/`COST_CENTER` en vez de `decision-scope/v1`, y el request HTTP implementado de waiver omite bindings que REQ-14 de SPEC 02 exige y representa cobertura solo con UUIDs sin versión/digest material. Esta spec no cambia aquella decisión: incluye cerrar esa implementación incompleta y probar el contrato bidireccional antes de habilitar el adapter real.

## Alcance

### Incluye

- Adapter in-process exact-one para transformar un `PolicyEvaluationBundle` vigente en una submission idempotente de Approval Workflow.
- Validación y producción de `DecisionScopeDescriptor` canónico, cerrando el drift existente en Policy.
- Mapping explícito de cada control de SPEC 02 a requirement humano, prerrequisito externo o ausencia de nodo.
- Secuencia Department → Finance/IT/Legal paralelos → Procurement/PRE_PO mediante dependencias por target.
- Submission y decisión `POLICY_EXCEPTION/REDUCE_MIN_VALID_QUOTATIONS` sin carry-forward.
- `POST /v1/policy-exceptions/verify` autenticado, con lookup server-side, bindings completos, nonce, vigencia, revocación, SoD y evidencia canónica.
- Ajuste del cliente/verifier actual de Policy para cumplir su contrato aprobado, pruebas bidireccionales, observabilidad y rollout fail-closed.

### No incluye

- Reglas, evaluación o administración de políticas; pertenecen a SPEC 02.
- Núcleo de casos, routing y decisiones; pertenecen a SPEC 03.
- Delegación, supersesión, carry-forward genérico o revocación base; pertenecen a SPEC 04.
- Ejecución material de budget checks, documentos, supplier, Procurement o sourcing; sus dominios propietarios señalan prerrequisitos futuros.
- Purchase Requests, Cost Centers, Supplier Master, RFQ, quotations, awards, POs, invoices o payments.
- Excepciones distintas de `REDUCE_MIN_VALID_QUOTATIONS`.
- Convertir un control no humano en aprobación manual, ignorarlo porque su owner no existe o permitir bypass de `ADMIN`.
- Interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Consumo exacto e idempotente del bundle.** Un adapter `policy-approval-adapter/v1`, registrado exactamente una vez para su subject/operation, recibe por referencia el `PolicyEvaluationBundle` persistido de mayor `evaluation_sequence` para la misma organización, subject/version y manifest. Recalcula/verifica policy, input, result y manifest digests antes de construir la submission de SPEC 03. La `submission_key` y fingerprint ligan bundle id/result digest, policy id/content digest, subject, manifest, controls, requester/originator y targets. Replay idéntico devuelve el mismo caso; otra carga da `409`. Bundle `BLOCKED`, corrupto, no actual o de otra versión se rechaza sin crear caso.

- **REQ-02 — Scope tipado y cierre de drift.** Antes de habilitar el adapter, `PolicyApprovalDescriptor.DecisionScope` debe producir y validar exclusivamente JSON canónico `decision-scope/v1` compatible con SPEC 03. `ORGANIZATION` usa refs nulas; `LEGAL_ENTITY` y `DEPARTMENT` usan UUID/version del catálogo atestiguado. Tokens legacy, JSON no canónico, campos extra, referencia inactiva y `COST_CENTER` se rechazan al publicar/evaluar Policy y al consumir el bundle. El cambio corrige la implementación de un requisito ya aprobado de SPEC 02; no se interpreta silenciosamente un token ni se migra un bundle histórico como si hubiera contenido datos ausentes.

- **REQ-03 — Mapping cerrado de controles.** `REQUIRE_APPROVAL` crea uno o más `ApprovalRequirement` y particiona targets de forma estable cuando descriptor, dependencias, exclusiones o routing difieren. `REQUIRE_BUDGET_CHECK`, `REQUIRE_SUPPORTING_DOCUMENT`, `REQUIRE_ACTIVE_SUPPLIER`, `REQUIRE_QUOTATIONS` y `REQUIRE_PROCUREMENT` crean `ExternalPrerequisite` conservando todos los parámetros y digests. `ALLOW`, `REQUIRE_PO` y `ALLOW_DIRECT_PURCHASE` no crean nodos y permanecen para el dominio owner. `BLOCK` o resultado `BLOCKED` impiden submission. Tipo desconocido, parámetro omitido, target incompleto u owner ambiguo falla cerrado. Un prerequisite sin owner disponible queda `WAITING` y bloquea completion; nunca se omite ni se convierte en task humana.

- **REQ-04 — Secuencia por targets y fases.** El adapter genera dependencias explícitas donde Department Approval ocurre primero; Finance, IT y Legal pueden quedar en paralelo si sus controles no declaran otra dependencia; Procurement espera todas las approvals y controles previos requeridos; Procurement Approval/PRE_PO precede el resultado que habilita emitir PO. Cada edge usa target type/id/version/material digest completos. `stage_code` conserva `DEPARTMENT`, `PRE_PROCUREMENT`, `PROCUREMENT` o `PRE_PO`, pero no sustituye el DAG. Un control externo sin dependent humano sigue siendo condición obligatoria de completion.

- **REQ-05 — Solicitud de quotation waiver.** Solo un workload confiable abre un requirement `POLICY_EXCEPTION/REDUCE_MIN_VALID_QUOTATIONS` ligado a organization, subject/version, `base_bundle_id`, `base_result_digest`, policy version/content digest, manifest digest, target requirement key y `covered_lines` completas `{type,id,version,material_snapshot_digest}`, además de `from`, `to`, `floor`, requester, originator, binding, nonce y vigencia. Exige role `PROCUREMENT_APPROVER`, authority `PROCUREMENT`, scope unión exacta y exclusiones de requester, originator y `workload_subject_id` cuando sea persona. Debe cumplirse `1 <= floor <= to < from`. Este tipo no admite carry-forward; otra evaluación o cambio material exige caso y decisión nuevos.

- **REQ-06 — Verificación server-side autenticada.** `POST /v1/policy-exceptions/verify` exige service JWT Bearer con audience `approval-workflow` e issuer/client allowlisted antes del lookup. El servicio localiza una única decisión por `evidence_digest` y `workflow_decision_id/version`, confirma `APPROVE`, tipo exception, authority, scope, SoD, vigencia y ausencia de revocación, y compara todos los campos del request con evidencia persistida. No confía en `approver_id`, role, authority o `segregation_satisfied` enviados por Policy. Request malformado da `400`; identidad `401/403`; evidencia no visible `404`; replay conflictivo `409`; binding expirado/revocado/insuficiente `422` o `verified=false`; dependencia técnica `503`.

- **REQ-07 — Wire contract completo y compatible con SPEC 02.** Request y response usan todas las propiedades de `workflow-verification-request/v1` y `workflow-verification-response/v1` declaradas en Datos y contratos. El cliente `HttpQuotationWaiverVerifier` de Policy debe enviar los bindings aprobados en SPEC 02 que hoy faltan, sustituir la cobertura UUID-only por targets versionados completos y usar únicamente approver/evidence/scope/SoD devueltos por Workflow. Respuesta `verified=true` exige contrato completo; JSON malformado, campo requerido ausente o versión incompatible se trata como `PolicyDependencyUnavailableException`. `404/409/422` o `verified=false` no reducen el control. Timeout máximo 5 segundos, cancelación y rotación de credenciales no alteran identidad del workload.

- **REQ-08 — Replay, canonicalización y revocación.** `(organization_id, verifier_type, nonce)` es único. El mismo payload, decisión y binding devuelve idempotentemente la misma verificación; reutilizar nonce/decisión con otro bundle, target, líneas o payload devuelve conflicto/no verificada sin crear evidencia. `authority_evidence_digest`, `workflow_decision_digest` y `evidence_digest` usan `approval-canonical-json/v1`; el último liga verifier, decisión, organization/subject, bundle/result, policy/content, manifest, target, reducción, requester/originator, approver, binding, nonce, scope, líneas, validity y SoD. Policy calcula después su propio `exception_verification_digest` de SPEC 02. Expiración o `DecisionEvidenceRevocation` de SPEC 04 invalida verificaciones futuras sin reescribir la decisión ni evaluaciones pasadas.

## Datos y contratos

Mapping obligatorio:

| Control combinado de SPEC 02 | Proyección en Approval Workflow | Owner/señal |
| --- | --- | --- |
| `REQUIRE_APPROVAL` | Requirement(s) por targets, descriptor, dependencias y exclusiones idénticos | Workflow decide. |
| `REQUIRE_BUDGET_CHECK` | Prerequisite con Cost Center refs/versiones, amount base y currency | Budget futuro; evidence ref/digest. |
| `REQUIRE_SUPPORTING_DOCUMENT` | Prerequisite con document types y minimum count | Dominio owner; conteo/tipos/digest, sin adjuntos. |
| `REQUIRE_ACTIVE_SUPPLIER` | Prerequisite con supplier id/version | Supplier futuro; estado de esa versión. |
| `REQUIRE_QUOTATIONS` | Prerequisite con minimum valid y allowance/floor | Sourcing futuro; waiver requiere nueva submission enlazada. |
| `REQUIRE_PROCUREMENT` | Prerequisite con stage y targets | Procurement futuro; completion versionado. |
| `ALLOW`, `REQUIRE_PO`, `ALLOW_DIRECT_PURCHASE` | Sin task ni prerequisite | El dominio owner aplica el control. |
| `BLOCK` o bundle `BLOCKED` | Submission rechazada | Nunca crea approvals para sortear bloqueo. |

`workflow-verification-request/v1`, con todas las propiedades presentes:

| Campo | Regla |
| --- | --- |
| `contract_version`, `evidence_digest`, `workflow_decision_id`, `workflow_decision_version` | Versión exacta, SHA-256 e identidad esperada. |
| `organization_id`, `subject_type`, `subject_id`, `subject_version` | Coinciden con caso y evaluación. |
| `base_bundle_id`, `base_result_digest`, `policy_version_id`, `policy_content_digest`, `manifest_digest` | Binding completo de SPEC 02. |
| `target_requirement_key`, `covered_lines[{type,id,version,material_snapshot_digest}]`, `from`, `to`, `floor` | Reducción y cobertura exactas por identidad/version/digest; `floor <= to < from`. |
| `requester_id`, `originator_id`, `workload_subject_id`, `binding`, `nonce` | SoD y replay; nonce único. |

`workflow-verification-response/v1`, con todas las propiedades presentes:

| Campo | Regla |
| --- | --- |
| `contract_version`, `verified`, `verifier_id`, `verifier_contract_version`, `verifier_reference` | `verified=false` no aporta autoridad. |
| `evidence_digest`, `binding`, `nonce`, `workflow_decision_id`, `workflow_decision_version`, `workflow_decision_digest` | Coincidencia exacta con request y persistencia. |
| `approver_id`, `approver_role`, `authority_type`, `eligibility_evidence`, `eligibility_evidence_digest`, `authority_evidence_digest` | Evidencia server-side; Policy usa estos valores. |
| `decision_scope`, `covered_lines[{type,id,version,material_snapshot_digest}]`, `segregation_satisfied` | Cobertura completa por identidad/version/digest y SoD verdadera. |
| `valid_from`, `valid_to`, `revoked_at` | `verified=true` exige instante vigente y `revoked_at=null`. |

Reglas adicionales:

- Los ids usan UUID `D` minúsculo y digests SHA-256 hex minúsculo; cada línea conserva type/id/version/material digest y los sets de líneas/scopes se ordenan por bytes canónicos y rechazan duplicados.
- `binding` es un digest reproducible de todos los campos de evaluación y reducción, no un sustituto que permita omitirlos del request.
- La respuesta conserva `EligibilityEvidence` completa de SPEC 01 y su digest; Policy puede persistir la forma minimizada ya definida por SPEC 02.
- El adapter usa los targets del manifest confirmado, no ids aportados de nuevo por un usuario.
- Eventos y wire contracts rechazan versiones desconocidas; no hay fallback a payload legacy.

## Migración, despliegue y reversión

- La persistencia agrega registros del adapter y evidencia de verificación al schema Approval; las correcciones de Policy son aditivas o crean una nueva representación sin reinterpretar bundles históricos.
- Primero se despliega soporte de `decision-scope/v1` y wire v1 completo en Policy con verifier default-deny; después Workflow/verifier; finalmente se registra el adapter real y se habilitan submissions. Nunca existe una ventana fail-open.
- Bundles históricos con token legacy no se someten al workflow. Deben reevaluarse con nueva key y descriptor canónico; no se mutan ni reciben una migración inferida.
- Health distingue adapter ausente/ambiguo, contrato incompatible, credencial inválida, timeout y verifier no disponible sin exponer bindings o PII.
- Rollback deshabilita adapter/verifier real y restaura default-deny; conserva casos, decisiones, revocaciones y evaluaciones. No reduce controles ya requeridos ni borra evidencia.
- Antes de habilitar tráfico se ejecuta contrato bidireccional real Policy↔Workflow sobre SQL Server efímero, además de casos de token legacy, binding adulterado, nonce repetido, expiración y revocación.

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
| Unitaria | Mapping completo, scopes JSON, DAG de fases, binding, canonicalización, digests y nonce | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.UnitTests/` |
| Integración | Bundles persistidos, reevaluación, adapter exact-one, casos/decisiones SQL y revocación | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | Service JWT, verifier, Problem Details, timeout, payload malformado, vigencia y replay | `dotnet test ProcureToPay.sln` en `tests/ProcureToPay.ApiE2ETests/` |
| Contrato | Fixtures compartidas Policy↔Workflow para request/response, scopes, success y rechazos | Pruebas bidireccionales dentro de la solución |
| Migración/operación | Bundles legacy, rollout default-deny→real, health y rollback a default-deny | SQL Server efímero y procedimiento automatizado |

Todos los criterios se verifican automáticamente. Casos negativos: bundle stale/BLOCKED, token scope legacy, control desconocido, owner ausente, edge de otra versión, autoaprobación, binding/digest adulterado, nonce reutilizado, evidencia expirada/revocada, response incompleto y timeout.

## Decisiones

- **DEC-01 — Adapter cerrado, no reinterpretación.** Cada efecto de SPEC 02 tiene una proyección explícita; un control desconocido o incompleto bloquea.
- **DEC-02 — Corregir drift antes de integrar.** `decision-scope/v1` y el wire completo ya son obligaciones de SPEC 02; esta entrega cierra su implementación y evita compatibilidad silenciosa con tokens/payloads insuficientes.
- **DEC-03 — Dependencias, no fases implícitas.** La secuencia funcional se materializa como edges por target; `stage_code` solo etiqueta.
- **DEC-04 — Waiver ligado a una evaluación exacta.** La excepción cubre un bundle, control, líneas, reducción y vigencia; no es reutilizable ni admite carry-forward.
- **DEC-05 — Verificación desde persistencia.** Workflow obtiene approver, authority, SoD y validez de su decisión; se descartan hints autocertificados y confianza en un binding opaco.
- **DEC-06 — Rollback a default-deny.** Una incompatibilidad deshabilita integración y conserva controles, nunca activa un bypass temporal.

## Plan de implementación

### Bloque 1 — Frontera Policy→Workflow

- **T-01 — Cerrar contratos productores.** Implementar/validar `decision-scope/v1`, wire v1 completo y fixtures compartidas, corrigiendo el drift de SPEC 02. Cubre: REQ-02, REQ-07, NFR-01, NFR-03, CA-01, CA-06.
- **T-02 — Adapter y mapping.** Implementar lookup de bundle, exact-one, idempotencia, tabla completa de efectos y errores fail-closed. Cubre: REQ-01, REQ-03, NFR-01, NFR-03, NFR-04, CA-01, CA-02, CA-03.

**Resultado verificable:** un bundle canónico crea una sola submission y cualquier bundle/control legacy, corrupto o desconocido queda bloqueado.

### Bloque 2 — Secuencia y waiver

- **T-03 — DAG de controles.** Construir requirements/prerequisites, partición de targets y secuencia funcional por edges completos. Cubre: REQ-03, REQ-04, CA-03, CA-04.
- **T-04 — Caso de quotation waiver.** Crear requirement exception, exclusiones, authority, cobertura, nonce y prohibición de carry-forward. Cubre: REQ-05, REQ-08, NFR-02, NFR-03, CA-05, CA-08.

**Resultado verificable:** approvals y controles automáticos quedan ordenados por target, y una reducción solo puede solicitarse con bindings completos.

### Bloque 3 — Verifier y operación

- **T-05 — Endpoint y evidencia.** Implementar service auth, lookup/comparación server-side, response completo, digests, replay, expiración y revocación. Cubre: REQ-06, REQ-07, REQ-08, NFR-01, NFR-02, NFR-03, CA-05, CA-06, CA-07, CA-08.
- **T-06 — Verificación y rollout.** Cubrir pruebas bidireccionales, SQL/API/E2E, telemetría, health, despliegue y rollback default-deny. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, NFR-01, NFR-02, NFR-03, NFR-04, CA-01, CA-02, CA-03, CA-04, CA-05, CA-06, CA-07, CA-08.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra el recorrido Policy↔Workflow y ningún fallo reduce el control.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-07, NFR-01 | Policy publica/evalúa `decision-scope/v1` exacto y wire completo; tokens actuales, JSON extra o payload omitido fallan. Bundle vigente crea una vez; replay devuelve caso y otra carga da `409`. | Automática: fixtures compartidas, regresión de drift, SQL y API/E2E. |
| CA-02 | REQ-01, REQ-03 | Bundle `BLOCKED`, stale, corrupto o de otra versión no crea caso. Cada efecto conocido sigue la tabla; desconocido/parámetro omitido falla y owner ausente deja prerequisite `WAITING`. | Automática: matriz efecto/parámetro y adapter controlado. |
| CA-03 | REQ-03 | Cada prerequisite conserva parámetros y targets completos; incluso sin approval dependiente impide completion hasta señal owner válida. Ningún control automático se convierte en task humana. | Automática: contrato de mapping y signals. |
| CA-04 | REQ-04 | Department precede; Finance/IT/Legal paralelos; Procurement/PRE_PO esperan controles; edges comparan type/id/version/digest y no se habilitan por coincidencia parcial. | Automática: DAG por target y escenarios multi-línea. |
| CA-05 | REQ-05, REQ-06 | Waiver exige `PROCUREMENT_APPROVER` + `PROCUREMENT`, cobertura exacta por type/id/version/material digest y exclusiones; cambiar solo versión/digest de línea falla. Service JWT inválido falla y lookup server-side rechaza autoaprobación, otra evaluación o decisión no vigente. | Automática: dominio, auth y contrato HTTP con targets versionados. |
| CA-06 | REQ-06, REQ-07, NFR-01, NFR-03 | Request/response v1 completos interoperan; Policy usa approver/evidence devueltos. Response incompleto/malformado o 5xx es dependencia indisponible; `404/409/422`/false no reduce. | Automática: pruebas bidireccionales y HTTP fault injection. |
| CA-07 | REQ-08, NFR-02 | Golden vectors fijan authority, workflow decision, evidence y exception verification digests; replay idéntico devuelve evidencia y nonce/binding reutilizado con otro payload da conflicto sin duplicar. | Automática: goldens independientes, constraints SQL y replay. |
| CA-08 | REQ-05, REQ-08, NFR-03, NFR-04 | `POLICY_EXCEPTION` nunca hace carry-forward; expiración/revocación invalida verificaciones futuras, rollout/rollback mantiene default-deny y telemetría no filtra bindings, nonce, tokens o PII. | Automática: integración con SPEC 04, health y captura de trazas. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Drift de scope persiste | Policy emite `ORGANIZATION`/`COST_CENTER` en texto | Regresión explícita y CA-01. |
| Wire omite bindings | Verifier decide solo por digest opaco o hints | Contrato completo y CA-06. |
| Control se pierde en mapping | Caso completa con prerequisite no señalado | Tabla cerrada y CA-02/CA-03. |
| Secuencia incorrecta | Procurement inicia antes de controles previos | Edges completos y CA-04. |
| Evidencia autocertificada | Request modifica approver o SoD | Lookup server-side y CA-05. |
| Waiver reutilizable | Misma decisión reduce otro bundle/target | Nonce, binding completo y CA-07. |
| Rollout crea ventana fail-open | Verifier parcial reduce control | Orden default-deny y CA-08. |
