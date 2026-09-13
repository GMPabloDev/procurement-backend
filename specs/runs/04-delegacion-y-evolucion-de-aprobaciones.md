# RUN SPEC 04 — Delegación y evolución de aprobaciones

> **Formato:** sdd-run/v2
> **Estado del run:** Lista para integrar
> **Spec:** specs/04-delegacion-y-evolucion-de-aprobaciones.md
> **Revisión contractual:** 2
> **Commit de la spec:** 758b9c90a0cd0174bb75bee39d56fd1ddc6f5de9
> **Blob aprobado:** f5801f317b2a61f6383464a1d4e1891ad2aae528
> **Digest contractual:** 0ffbc76e8ef5a1c88d251ebad74a99d6cb45cebffd0b6f553fddc5210049933f
> **Rama base:** main
> **Commit base:** 758b9c90a0cd0174bb75bee39d56fd1ddc6f5de9
> **Rama de implementación:** spec-04-delegacion-y-evolucion-de-aprobaciones
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-13 05:25 -0500
> **Actualizado:** 2026-09-13 08:05 -0500
> **HEAD verificado:** 2bb6db3b426228ad50201ff745edaaacce0fd96a
> **Commit de integración:** Pendiente

## Línea base

- `dotnet build ProcureToPay.sln --no-restore --nologo -v q`: 0 errores, 0 advertencias.
- Unitarias (DLL directo): **101/101** correctas.
- Integración (Docker/Testcontainers): **44/44** correctas (6m 23s).
- API/E2E (Docker/Testcontainers): **12/12** correctas (2m 14s).
- Nota de entorno heredada de SPEC 03: `dotnet test --project … --no-restore` no ejecuta pruebas con
  el runner de este repositorio; se usa la ejecución directa del DLL compilado.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio `ApprovalDelegation`/jobs, preimages exactos, persistencia, migración `Spec04ApprovalEvolution` y comandos create/revoke con idempotencia, autorización, solapes, cadenas y audit. Unitarias `ApprovalDelegationTests` + `ApprovalEvolutionGoldenTests`; integración `ApprovalEvolutionIntegrationTests.Delegation_lifecycle_routes_the_effective_set_and_reconciles_with_runs`; E2E `ApprovalEvolutionE2ETests.Delegation_endpoints_enforce_authorization_and_replay`. | working-tree / CP-01 |
| T-02 | Verificada | Conjunto efectivo con delegación en `ApprovalAssignmentEngine` (retira al delegante, mantiene al delegado solo por mérito), `delegation_id/version` en assignments y `authority_evidence_digest`, jobs `ACTIVATE/EXPIRE` con lease/fencing, corridas `DELEGATION_CHANGE/DELEGATION_EXPIRY` únicas con raíz causal, filtro de cobertura y health. Integración del ciclo completo y segunda confirmación idempotente. | working-tree / CP-01 |
| T-03 | Verificada | `ApprovalCase.Supersede` terminal para caso/nodos, estados `SUPERSEDED`, mapping biyectivo contra targets persistidos y nuevos, transacción atómica, replay por `supersession_key`, evento `approval-case-lifecycle/v1` por target anterior y persistencia de la cadena. Integración `Supersession_is_atomic_and_carries_forward_only_strict_equality`; E2E de supersesión. | working-tree / CP-02 |
| T-04 | Verificada | `requirement_contract_digest`, `carry_forward_proof`, decisión derivada `SYSTEM` sin task/decision key, evidencia compartida `VALID`, `DecisionCarryForwardRecord`, activación de dependientes y `approval-result/v3`. Vectores dorados y comparación de materialidad/descriptor/evidencia revocada. | working-tree / CP-02 |
| T-05 | Verificada | `DecisionAuthorityEvidence` con identidad estable y backfill 1:1 en migración, revocación idempotente e irreversible, evento `approval-evidence-revoked/v1`, auditoría y superficies HTTP con permisos, historial de cadena y `404` fuera de scope. Integración `Evidence_revocation_is_irreversible_idempotent_and_blocks_carry_forward`; E2E de revocación e historial. | working-tree / CP-03 |
| T-06 | Verificada | Worker registra el procesador de transiciones, health degrada por job vencido (`APPROVAL_DELEGATION_TRANSITION_OVERDUE`), preflight de contratos conocidos, migración con backfill, documentación operativa y pruebas de migración/restart/replay. Suite final sobre el árbol del checkpoint CP-04: build 0 errores/0 advertencias, Unit **119/119**, Integración **50/50**, E2E **14/14**. | working-tree / CP-05 |

## Checkpoints

### CP-01 — 2026-09-13 08:05 -05 — Bloque 1 (delegación y routing)

- Tareas: T-01, T-02.
- Cambios: dominio de delegación y jobs de transición con lease/fencing; preimages dorados; identidad de evidencia de autoridad; conjunto efectivo con retiro del delegante y delegado por mérito; corridas `DELEGATION_CHANGE/DELEGATION_EXPIRY` con raíz causal y filtro de cobertura; worker, health y migración `Spec04ApprovalEvolution` con backfill de evidencia.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` (0 errores, 0 advertencias); unitarias del delta (119/119); integración de delegación (4 casos del archivo nuevo) y E2E de delegación.
- Resultado: activación, reasignación al delegado elegible, revocación y expiración verificadas en SQL Server efímero.
- HEAD: working-tree sobre `spec-04-delegacion-y-evolucion-de-aprobaciones` (base `758b9c9`).
- Próximo paso: supersesión y carry-forward.

### CP-02 — 2026-09-13 08:35 -05 — Bloque 2 (evolución e historia)

- Tareas: T-03, T-04.
- Cambios: estados `SUPERSEDED`, `ApprovalCase.Supersede`, servicio de supersesión con mapping biyectivo, replay por key y eventos de lifecycle; carry-forward estricto con evidencia compartida, `carry_forward_proof`, decisión derivada `SYSTEM` taskless y `approval-result/v3`.
- Tests y checks: unitarios de dominio y vectores dorados; integración de supersesión/carry-forward (materialidad igual y cambiada, mapping incompleto); E2E de supersesión.
- Resultado: cambiar un campo material exige task nueva; igualdad exacta crea una decisión nueva trazada a la humana previa.
- HEAD: working-tree sobre la rama de implementación.
- Próximo paso: revocación, superficies protegidas y operación.

### CP-03 — 2026-09-13 09:05 -05 — Bloque 3 (revocación, API y verificación)

- Tareas: T-05, T-06.
- Cambios: evidencia de autoridad con revocación irreversible e idempotente, evento `approval-evidence-revoked/v1`, endpoints de delegaciones/supersesión/revocación/historial con permisos, worker de transiciones, health y documentación operativa.
- Tests y checks: integración de revocación y backfill de migración; E2E de permisos e historial; build completo. Escenarios negativos cubiertos: delegado no elegible, cadena, solape, intervalo inválido, mapping incompleto, digest material cambiado, evidencia revocada y lectura fuera de scope.
- Resultado: todas las tareas implementadas; pendiente la suite completa sobre el árbol estable y la revisión independiente.
- HEAD: working-tree sobre la rama de implementación.
- Próximo paso: suite completa, `specctl run-lint` y gate de revisión.

### CP-04 — 2026-09-13 11:20 -05 — Verificación final sobre el árbol estable

- Tareas: T-01–T-06 (`Verificada`); CA-01–CA-07 `Cumplido`.
- Cambios: corrección del mensaje del preflight para conservar la palabra `legacy` (regresión detectada por la suite completa) y prueba de backfill de migración con parámetro `{}`; sin otros cambios después de esta verificación.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` (0 errores, 0 advertencias); Unit **119/119**; Integración **49/49** (Docker/Testcontainers); E2E **14/14** (Docker/Testcontainers); `specctl run-lint 04` válido.
- Resultado: suite completa verde una sola vez sobre el árbol estable; pendiente el commit del código probado y la revisión independiente.
- HEAD: working-tree sobre `spec-04-delegacion-y-evolucion-de-aprobaciones` (base `758b9c9`).
- Próximo paso: commit del código probado por el usuario, revisión independiente y cierre del gate.

### CP-05 — 2026-09-13 12:35 -05 — Correcciones de la revisión independiente (ronda 1)

- Revisión ronda 1 (`sdd-implementation-reviewer`, modelo `openai-codex/gpt-5.6-sol`, effort high) sobre el candidato `131fe2719b7290a754d71d703a4dd83989b194ac`: **BLOCK** con R11–R14. Triaje: R11, R12 y R14 aceptados como defectos de implementación; R13 aceptado como evidencia faltante de CA-03.
- Correcciones:
  - **R11:** nuevo ledger `ApprovalDelegationCommands` con unicidad `(organization_id, actor_type, actor_user_id, command_key)` y action `CREATE|REVOKE`; el replay resuelve cada comando por su propia identidad y la fila de delegación conserva la identidad de creación. Migración `Spec04DelegationCommandKeys`. Pruebas: replay de revoke idéntico, otra carga `409`, y replay de create preservado tras revocar.
  - **R12:** `ValidateMapping` conserva la partición por requirement (ningún grupo se parte ni se fusiona en la nueva versión). Prueba negativa de split `DomainConflictException`.
  - **R13:** nueva integración `Scheduled_delegation_transitions_activate_and_expire_once`: activación y expiración programadas, dos workers compitiendo por el mismo job (una sola confirmación), segunda pasada sin efectos, raíz/trigger/cursor conservados, job completado persistido y raíz `SYSTEM` de expiración causada por la delegación.
  - **R14:** el registro append-only de revocación y el evento conservan la versión esperada/confirmada (pre-revocación); la evidencia avanza a `REVOKED`. Pruebas de versión 1 en registro, evento y replay.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` (0 errores, 0 advertencias); Unit **119/119**; Integración **50/50**; E2E **14/14**; `specctl run-lint 04` válido.
- HEAD: working-tree sobre la rama de implementación (pendiente commit del incremento para la ronda delta).
- Próximo paso: commit del incremento, revisión delta de R11–R14 y cierre del gate.

### CP-06 — 2026-09-13 13:05 -05 — Cierre probatorio de R13 (ronda delta)

- Revisión ronda 2 (`sdd-implementation-reviewer`, modelo `openai-codex/gpt-5.6-sol`, effort high) sobre `b436b453f079e4314bc524e7446211e36c97d002`: R11, R12 y R14 `resolved`; R13 seguía `open` por falta de recorrido real de las corridas programadas.
- Corrección: `Scheduled_delegation_transitions_activate_and_expire_once` ahora crea caso con task asignada al delegante, procesa la corrida `DELEGATION_CHANGE` en su instante (reasignación al delegado con evidencia y release del assignment previo), comprueba cursor persistido del run, segunda pasada de un run completado sin efectos, decisión terminal antes de la expiración y corrida `DELEGATION_EXPIRY` que preserva task y digest de la decisión.
- Tests y checks: `dotnet build ProcureToPay.sln --no-restore` (0 errores, 0 advertencias); Unit **119/119**; Integración **50/50**; E2E **14/14**; `specctl run-lint 04` válido.
- Resultado: R13 con recorrido real de las dos corridas sobre un caso con task y decisión terminal; pendiente el commit del incremento de pruebas y la ronda de cierre acotada.
- HEAD: working-tree sobre la rama de implementación (pendiente commit del incremento de pruebas para la ronda de cierre).
- Resultado de la ronda 3: **PASS** (R13 `resolved`); spec y run `Lista para integrar` sobre `2bb6db3b426228ad50201ff745edaaacce0fd96a`.
- Próximo paso: commit de los metadatos finales por el usuario, integración del código ya commiteado y `/spec-impl 04 --close` desde la base.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | Unitarias `ApprovalDelegationTests` (auto-delegación, ADMIN/AUDITOR, intervalos, lifecycle, job fencing) y `ApprovalEvolutionGoldenTests.Delegation_fingerprint_matches_the_contractual_vector`; integración `ApprovalEvolutionIntegrationTests.Delegation_lifecycle_routes_the_effective_set_and_reconciles_with_runs` (delegante sin cobertura, replay de create y de revoke idénticos, otra carga `409`, solape `409`, y replay de create preservado tras revocar); E2E `ApprovalEvolutionE2ETests.Delegation_endpoints_enforce_authorization_and_replay` (usuario ajeno `403`, ADMIN con motivo, replay). | Automática |
| CA-02 | Cumplido | Integración: el conjunto efectivo retira al delegante y reasigna al delegado elegible; `ApprovalAssignment.DelegationId/Version` reales y `AuthorityEvidenceDigest` distinto del preimage sin delegación; decisión del delegado aceptada. Unitaria `ApprovalEvolutionGoldenTests.Delegation_evidence_digest_fixes_the_real_delegation_identity`. | Automática |
| CA-03 | Cumplido | Integración: crear persiste `ACTIVATE` completado y `EXPIRE` pendiente, una sola corrida `DELEGATION_CHANGE`, la segunda confirmación no reabre ni duplica, revocar cancela jobs y crea la corrida `REVOKE`; `Scheduled_delegation_transitions_activate_and_expire_once` procesa ambas corridas sobre un caso con task (reasignación al delegado, cursor persistido, segunda pasada sin efectos y decisión terminal preservada tras la expiración). Unitaria `Transition_job_is_claimed_with_fencing_and_never_reopens`; integración `Scheduled_delegation_transitions_activate_and_expire_once` (activación y expiración programadas, dos workers compitiendo, segunda pasada sin efectos, raíz/trigger/cursor conservados y job completado). Health y worker de transiciones registrados en `ApprovalWorkflowWorker`/`ApprovalHealthCheck`. | Automática |
| CA-04 | Cumplido | Integración `Supersession_is_atomic_and_carries_forward_only_strict_equality`: mapping biyectivo, caso y nodos anteriores `SUPERSEDED`, replay por key, evento lifecycle por target anterior, mapping incompleto `409`; unitaria `ApprovalEvolutionGoldenTests.Supersession_fingerprint_matches_the_contractual_vector`; E2E `Supersession_and_revocation_are_restricted_and_idempotent`. | Automática |
| CA-05 | Cumplido | Integración: igualdad estricta crea decisión `SYSTEM/CARRY_FORWARD` sin task, `DecisionCarryForwardRecord` y evento v3; digest material cambiado y evidencia revocada exigen task nueva. Unitarias `Carry_forward_proof_matches_the_contractual_vector`, `Carry_forward_decision_digest_matches_the_contractual_vector`, `Carry_forward_decision_is_a_system_taskless_derivation`, `Approval_result_v3_payload_matches_the_contractual_bytes`. | Automática |
| CA-06 | Cumplido | Integración `Evidence_revocation_is_irreversible_idempotent_and_blocks_carry_forward` (owner y ADMIN, replay, otra key `409`, decisión intacta, registro y evento con la versión esperada) y `Migration_backfills_authority_evidence_for_existing_human_decisions` (backfill 1:1 sin cambiar digests); unitaria `Revocation_fingerprint_matches_the_contractual_vector` y `Evidence_revoked_payload_has_its_exact_properties`; E2E de revocación por workload y ADMIN. | Automática |
| CA-07 | Cumplido | E2E `Delegation_endpoints_enforce_authorization_and_replay` y `Supersession_and_revocation_are_restricted_and_idempotent`: usuario ajeno `403`, `AUDITOR` organizacional, historial solo para originador/workload/`AUDITOR` y `404` fuera de scope; la vista de decisiones expone `origin`, `actor_type` y `evidence_status`; telemetría minimizada conservada por la prueba existente de NFR-05. | Automática |

## Desviaciones y bloqueos

- **LSP de Pi Lens con referencias obsoletas (herramienta, no código).** Tras añadir tipos nuevos al
  proyecto `Domain`, el servidor de lenguaje siguió reportando `CS0246/CS1061` (“ApprovalDelegation…
  no existe”) sobre archivos que sí compilan. El compilador y una recompilación limpia lo
  contradicen: `dotnet build ProcureToPay.sln --no-restore` termina con 0 errores. Se registra una
  vez y se continúa con compilador/tests como evidencia, sin repetir escaneos por esa captura.
- **Runner de pruebas.** `dotnet test --project … --no-restore` no ejecuta pruebas en este entorno
  (código 5, 0 ejecutadas), igual que en SPEC 03; la evidencia se obtiene ejecutando directamente el
  DLL compilado de cada suite.
- **ronda 3 autorizada.** El paquete agotó las dos llamadas automáticas con la ronda 2 (R13 abierto);
  el usuario autorizó explícitamente una tercera llamada acotada a R13. Rondas adicionales autorizadas: 1.

## Verificación independiente

> **Resultado:** Sin bloqueos
> **Rondas:** 3/3
> **Triaje:** ronda 1: 3 bug de implementación (R11, R12, R14) + 1 prueba faltante (R13); ronda 2: R11/R12/R14 `resolved`, R13 `open` por falta de recorrido real (corregido en CP-06); ronda 3: R13 `resolved`, sin hallazgos nuevos
> **Modelo efectivo:** `openai-codex/gpt-5.6-sol` (effort high), distinto del orquestador, en las tres rondas
> **Método:** Subagente `sdd-implementation-reviewer`. Ronda 1 sobre `131fe2719b7290a754d71d703a4dd83989b194ac`; ronda 2 (delta) sobre `b436b453f079e4314bc524e7446211e36c97d002`; ronda 3 (delta R13) sobre `2bb6db3b426228ad50201ff745edaaacce0fd96a` (base `758b9c9`)
> **Fecha:** ronda 1: 2026-09-13 12:09 · ronda 2: 2026-09-13 12:35 · ronda 3: 2026-09-13 13:03

Hallazgos de la ronda 1 y su resolución:

- **R11** `ApprovalDelegationService.RevokeAsync` resolvía el replay con la identidad de creación de la fila; corregido con el ledger `ApprovalDelegationCommands` y probado (replay de revoke, `409` con otra carga, replay de create preservado).
- **R12** el mapping aplanado permitía partir un requirement agrupado; corregido con la validación de partición y prueba negativa de split.
- **R13** faltaba evidencia de activación/expiración programadas y carreras; corregido con la nueva integración de transiciones programadas y dos workers.
- **R14** el registro de revocación guardaba la versión post-revocación; corregido para conservar la versión esperada/confirmada en el registro, el evento y el replay.
- **R13 (ronda 2)** la prueba de transiciones programadas no procesaba las corridas sobre un caso real; corregido en CP-06 con reasignación, cursor, segunda pasada y decisión terminal preservada.
