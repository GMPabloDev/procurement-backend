# RUN SPEC 09 — Supplier Master y catálogo de proveedores homologados

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/09-supplier-master-catalogo-proveedores-homologados.md
> **Revisión contractual:** 1
> **Commit de la spec:** e52e9789f475dc53885da7212d6dec699166b165
> **Blob aprobado:** e2e5b509b1ff6e2c15ca2a8ecebfebed3a2a7fce
> **Digest contractual:** 338d7c40ad2d45979e5b053006f3a2f2283f0e9bfc0faa5bd1f0e144754f7faa
> **Rama base:** main
> **Commit base:** e52e9789f475dc53885da7212d6dec699166b165
> **Rama de implementación:** spec-09-supplier-master-catalogo-proveedores-homologados
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-15 15:47 -0500
> **Actualizado:** 2026-09-15 15:47 -0500
> **HEAD verificado:** Pendiente
> **Commit de integración:** Pendiente

## Línea base

Ejecutada sobre el commit base `e52e978` con el árbol limpio, antes de cualquier edición:

- `dotnet build ProcureToPay.sln --nologo -v q` → 0 errores, 17 advertencias preexistentes.
- `dotnet test --project … --no-restore` (documentado en `AGENTS.md`) no ejecuta pruebas en este
  entorno: se usa la ejecución directa de las asambleas compiladas, igual que en el run de SPEC 08.
- Unitarias: `200/200` correctas, 0 con errores.
- Integración (Testcontainers + Docker): `107/107` correctas, 0 con errores (17m 24s).
- API/E2E (Testcontainers + Docker): `27/27` correctas, 0 con errores (5m 16s).
- Fallos preexistentes: ninguno.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Dominio Supplier (`SupplierModels`, `SupplierCanonicalizer`, `SupplierChangeClassifier`, `ApprovedSupplierCatalog`, `SupplierPolicyFacts`, `SupplierApprovalContracts`): identidad fiscal, contenido versionado, digests publicados, clasificación server-side, matcher determinista de catálogo y snapshots. Persistencia `Supplier` con punteros, constraints y triggers append-only/predecessor/puntero. Evidencia: `SupplierCanonicalizationGoldenTests` (9, incluido el vector publicado `633c1bd7…5c91d`), `ApprovedSupplierCatalogMatcherTests` (9), `SupplierBankingKeyProviderTests` (4) e integración `A_new_supplier_is_drafted_and_only_an_approved_decision_materializes_it`, `The_fiscal_identity_is_unique_per_country_and_tax_id`, `The_append_only_triggers_reject_a_rewrite_of_an_approved_version`. | working-tree / CP-01 |
| T-02 | Verificada | Gobierno: `SupplierGovernanceService` (draft, clasificación desde el diff, aplicación no sensible, propuesta, submission durable con `SubmissionKey`, aplicación de resultados), `SupplierGovernanceApprovalAdapter` (requirement `PROCUREMENT_APPROVER` + SUPPLIER_MASTER, scope ORGANIZATION, exclusiones del editor), `SupplierApprovalResultConsumer` y `ApprovalResultRouter`. Evidencia: `A_cosmetic_change_is_applied_by_the_buyer_without_an_approval_case`, `A_sensitive_change_without_approval_keeps_the_approved_version_and_a_rejection_closes_it`, `A_blocked_supplier_recovers_only_through_a_new_approved_activation`. | working-tree / CP-01 |
| T-03 | Verificada | Banking cifrado: `ConfigurationSupplierBankingKeyProvider` (AES-256-GCM, AAD ligada a organización/supplier/detalle/versión) y `SupplierBankingService` (versiones append-only, default por moneda, máscara, reveal AP current y reveal del approver con task viva, audit con effect key). Evidencia: `SupplierBankingKeyProviderTests` y `Banking_details_are_encrypted_and_revealed_only_to_the_authorized_actors` (incluye denegación a buyer/approver terminal y rechazo del rewrite por el trigger). | working-tree / CP-01 |
| T-04 | Verificada | Catálogo homologado: `ApprovedSupplierCatalogGovernanceService` (raíz por selector, versiones append-only, attachment confirmado como evidencia de publicación, propuesta y submission), staging/confirmación/descarga auditada y matcher determinista con `EXPIRED` por reloj. Evidencia: `ApprovedSupplierCatalogIntegrationTests` (3: publicación con vigencia y expiración, precedencia producto-específica + attachment sin confirmar rechazado, descarga auditada con metadata inmutable) y `ApprovedSupplierCatalogMatcherTests` (9). | working-tree / CP-02 |
| T-05 | Verificada | Owners y facts v2: `SupplierReferenceOwner`, `SupplierPolicyReferenceCatalog`, `ApprovedSupplierFactOwner` y `IApprovedSupplierFactOwner`; `PurchaseRequestAttestationService` congela un `supplier-policy-fact-snapshot/v1` por línea y publica `purchase-request-completeness-manifest/v2`; `PurchaseRequestPolicyFactProvider` sirve `purchase-request-policy-facts/v2` con `PREFERRED_SUPPLIER`/`EXTERNAL_AGREEMENT_STATUS` reales y mantiene v1 para manifiestos históricos. Evidencia: `A_control_over_two_suppliers_partitions_into_two_prerequisites_and_signals_them` (un snapshots preferred/ACTIVE y otro false/NONE desde el catálogo real) y `PurchaseRequestPolicyIntegrationTests` (5/5 sobre SQL real). | working-tree / CP-02 |
| T-06 | Verificada | Adapter v4 (`PolicyApprovalAdapter.ContractVersionV4` + partición por supplier con `SupplierCanonicalizer.PartitionKey`, preimagen v1 de `source_control_digest` con requirement key original y targets de la partición, budget v3 intacto) y processor real con attempt durable, lease de 30 s renovado cada 10 s, fencing, keys deterministas, evidencia `active-supplier-evidence/v1` y señal idempotente. Evidencia: `A_control_over_two_suppliers_partitions_into_two_prerequisites_and_signals_them` (2 prerequisites v1, cada uno con su `supplier_ref` y 1 target, ambos `SATISFIED` con evidence digest) y `A_supplier_blocked_after_submission_fails_its_prerequisite_without_breaking_the_other_one` (uno `SATISFIED` y otro `FAILED` tras el bloqueo posterior al submit). | working-tree / CP-02 |
| T-07 | Verificada | API por rol (`SupplierController`, `ApprovedSupplierCatalogController`), reveal bancario auditado, health `supplier` con la gramática contractual y `ApiExceptionHandler` con `/problems/supplier-dependency-unavailable` (`503`). Evidencia: `SupplierE2ETests` (2: ciclo gobernado con lecturas mínimas, `403` del requester, `404` del task ajeno, `SUPPLIER_OK` y banking enmascarado con reveal solo AP). | working-tree / CP-02 |
| T-08 | Verificada | Migraciones `Spec09SupplierMaster` (tablas, constraints, triggers append-only/predecessor/puntero, registro único del processor) y `Spec09SupplierSubmissionKey`; `docs/supplier-operations.md` con rotación de clave, diagnóstico por código y recuperación; compatibilidad v1–v4 conservada (attempts y manifiestos históricos siguen resolviendo su versión). Evidencia: suite completa sobre el árbol estable — Unit `222/222`, Integración `119/119`, API/E2E `29/29`, `git diff --check` limpio. | working-tree / CP-02 |

## Checkpoints

### CP-01 — 2026-09-15 17:20 -0500 — Supplier Master, banking y gobierno verificados

- Tareas: T-01, T-02 y T-03 verificadas; T-04–T-08 pendientes.
- Cambios: dominio y persistencia Supplier con migraciones `Spec09SupplierMaster` (tablas, constraints, triggers append-only/predecessor/puntero, registro del processor) y `Spec09SupplierSubmissionKey`; servicios de gobierno, banking, catálogo, owners y processor; adapter v4 con partición Supplier; manifest/provider v2 de Purchase Requests; controllers, health y `ApiExceptionHandler` para `422/503` del dominio Supplier.
- Tests y checks: build 0 errores; Unit `222/222`; integración dirigida `SupplierGovernanceIntegrationTests` `7/7`; `PurchaseRequestPolicyIntegrationTests` `5/5` (attestation v2 + provider v2 reales); `specctl run-lint 09` válido; `git diff --check` limpio.
- Desviaciones registradas: (a) la clasificación del cambio se calcula con el estado que el candidato materializaría, no con el estado del payload, para que un cambio cosmético no se confunda con un cambio de estado; (b) la partición Supplier vive en `policy-approval-adapter/v4`, implementada como parámetro interno de `PolicyApprovalAdapter`; (c) el trigger que compara nulabilidad de `ProductId` usa una disyunción explícita porque `(IS NULL) <> (IS NULL)` no es sintaxis válida en T-SQL; (d) la submission es un attempt durable (la fila queda `PENDING` con `SubmissionKey` antes de crear el caso) porque el servicio de Approval abre su propia transacción y un reintento debe recuperar el mismo caso; (e) el contrato `approval-result/v2` —el que Approval publica para una decisión humana simple— también se enruta por subject, porque de lo contrario el resultado de un cambio de Supplier nunca llegaría a su consumer; la spec enumera v3 y lifecycle como los contratos que el consumer entiende y ambos siguen registrados.
- Límite de herramienta registrado una vez: el diagnóstico de pi-lens quedó obsoleto para los archivos nuevos de este run (reportaba tipos inexistentes que `dotnet build` resolvía); la verificación se hizo con `dotnet build`/`dotnet test`, que es la autoridad del compilador.
- Próximo paso: T-04 (catálogo homologado y attachments) y T-05/T-06 (facts v2 y partición del prerequisite).

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Cumplido | `SupplierCanonicalizationGoldenTests` fija el vector publicado `633c1bd7…5c91d` y los preimages exactos; `The_fiscal_identity_is_unique_per_country_and_tax_id` prueba la reserva fiscal y el conflicto; `The_append_only_triggers_reject_a_rewrite_of_an_approved_version` prueba que la historia no se puede reescribir. | Suite unitaria + integración SQL |
| CA-02 | Cumplido | `A_cosmetic_change_is_applied_by_the_buyer_without_an_approval_case`, `A_sensitive_change_without_approval_keeps_the_approved_version_and_a_rejection_closes_it`, `A_blocked_supplier_recovers_only_through_a_new_approved_activation` y `SupplierE2ETests` (requester `403`, task ajena `404`, editores sin capacidad de decidir su propio proposal). | Integración SQL + API/E2E con JWT real |
| CA-03 | Cumplido | La submission es un attempt durable con `SubmissionKey` reutilizada (`Replayed`), el caso se recupera por clave y el consumer es idempotente por `event_id+contract_version`; el router de resultados entrega v2/v3/lifecycle. Evidencia indirecta en las siete pruebas de gobierno y en `PurchaseRequestSubmissionIntegrationTests` (6/6). | Integración SQL |
| CA-04 | Cumplido | `SupplierBankingKeyProviderTests` (round-trip, AAD ligada, tag/nonce/ciphertext alterados, clave ausente o inválida) y `The_banking_projection_stays_masked_and_the_reveal_is_role_bound` (máscara en la API, reveal AP `200`, buyer y approver terminales `403`, audit sin valor). | Unitarias + API/E2E |
| CA-05 | Cumplido | `An_approved_entry_is_published_with_its_agreement_and_expires_by_clock` (publicación, expiración por reloj sin mutar la versión), `A_product_entry_prevails_and_an_unconfirmed_attachment_cannot_be_published` (precedencia y staging no publicable), `The_agreement_download_is_audited_and_the_attachment_metadata_is_immutable`. | Integración SQL + storage de prueba |
| CA-06 | Cumplido | `A_control_over_two_suppliers_partitions_into_two_prerequisites_and_signals_them` recorre PR → attestation v2 → policy real → provider v2 → bundle y comprueba los dos snapshots congelados (preferred/ACTIVE y false/NONE) y su digest reproducible. | Integración SQL cross-module |
| CA-07 | Cumplido | El mismo recorrido produce exactamente dos prerequisites `active-supplier-owner/v1` (uno por supplier, key `ACTIVE_SUPPLIER:<digest>`, un target cada uno) que el processor señala; con el supplier bloqueado después del submit el prerequisite queda `FAILED` y el otro `SATISFIED`, cada uno con su attempt `COMPLETED` y evidence digest. `PurchaseRequestSubmissionIntegrationTests` conserva v1–v3. | Integración SQL cross-module + worker |
| CA-08 | Cumplido | `SupplierE2ETests` prueba rol mínimo, lecturas mínimas, ausencia de PII/pan en respuestas y `SUPPLIER_OK`; el health degrada con códigos contractuales; las suites de SPEC 01–08 permanecen verdes tras sustituir únicamente lo que esta spec modifica (redirects de owners/catálogos, provider v2, adapter v4 y ruteo de resultados v2). | API/E2E + suite completa |

## Desviaciones y bloqueos

- Ninguno bloqueante. Véanse las desviaciones internas registradas en CP-01 y CP-02.

### CP-04 — 2026-09-16 09:05 -0500 — Correcciones de la ronda 2 (R10, R12, R15)

- Tareas: T-01–T-08 verificadas; CA-01–CA-08 cumplidos.
- Cambios: `IFileStorage.IsAvailableAsync` con sonda real de `S3FileStorage` y consumo en readiness (R15); exclusión del propio root en el default bancario (R10); solape contra todas las versiones ACTIVE del selector (R12); dos casos nuevos en `SupplierHardeningIntegrationTests` (8 en total) y `Readiness_degrades_when_the_agreement_storage_is_unreachable` en API/E2E (30).
- Tests y checks: build 0 errores; Unit `222/222`; Integración `127/127`; API/E2E `30/30`; `git diff --check` limpio; `specctl run-lint 09` válido.
- HEAD: working tree sin commit sobre `c979d8e`; requiere commit del usuario.
- Próximo paso: commit, y decidir con el usuario si autoriza una ronda delta acotada a R10/R12/R15 o si se acepta la evidencia propia.

### CP-03 — 2026-09-16 07:25 -0500 — Correcciones de la revisión independiente (ronda 1)

- Tareas: T-01–T-08 verificadas tras las correcciones; CA-01–CA-08 cumplidos.
- Cambios: provenance contractual en el provider v2 (R9); idempotencia durable y default diferido del banking con `SupplierBankingCommands` e `IsDefault` (R10); eliminación de `AccountHolder` en claro (R11); índice único del selector sin el filtro implícito y validación de selector/solape (R12); materialización atómica y `event_id` único (R13); autorización organizacional del catálogo, lecturas gobernadas y roles de lectura del master (R14); sondas de readiness (R15); migración `Spec09SupplierReviewFixes` y suite de endurecimiento (R16).
- Tests y checks: build 0 errores; Unit `222/222`; Integración `125/125`; API/E2E `29/29`; `git diff --check` limpio; `specctl run-lint 09` válido.
- HEAD: working tree sin commit sobre `2b6954e`; requiere nuevo commit del usuario antes de la ronda 2.
- Próximo paso: commit de las correcciones y ronda diferencial de la revisión.

### CP-02 — 2026-09-15 19:05 -0500 — Catálogo, facts v2, adapter v4 y recorrido real certificados

- Tareas: T-01–T-08 verificadas; CA-01–CA-08 cumplidos.
- Cambios: `ApprovedSupplierCatalogGovernanceService` (selector único, attachments, submission durable), `ApprovedSupplierFactOwner` + `SupplierReferenceOwner` + `SupplierPolicyReferenceCatalog`, manifest/provider v2 en Purchase Requests, `policy-approval-adapter/v4` con partición por Supplier, `SupplierPrerequisiteProcessor` con registro exact-one, controllers, health y runbook. Correcciones internas detectadas por las suites: clasificación con el estado efectivo del candidato, `SubmissionKey` durable, orden de guardado del puntero frente al trigger, `THROW` del trigger de nulabilidad, almacenamiento de acuerdos resuelto bajo demanda (para que un despliegue sin S3 no rompa requests ajenos), provenance de los facts de Supplier anclada al digest del catálogo y no al instante de attestation (conserva la materialidad y por tanto el carry-forward de SPEC 04), y `PurchaseRequestSubmissionService.AdapterContractVersion = v4`.
- Tests y checks: build 0 errores; Unit `222/222`; Integración `119/119`; API/E2E `29/29`; `git diff --check` limpio; `specctl run-lint 09` válido. Suite completa ejecutada una vez sobre el árbol estable de este checkpoint.
- Desviaciones ampliadas: además de las de CP-01, la provenance de `PREFERRED_SUPPLIER`/`EXTERNAL_AGREEMENT_STATUS` usa `supplier-facts/<catalog_content_digest|NONE>#<line-id>` en lugar de la forma con el digest del set por versión, porque esa forma invalidaba la materialidad de líneas retenidas en cada re-attestación y rompía el carry-forward de SPEC 04; el digest del set sigue entrando en el manifiesto v2 y el contrato de facts no cambia.
- HEAD: working tree sin commit sobre `e52e978`.
- Próximo paso: commit del árbol probado por el usuario y verificación independiente.

## Verificación independiente

> **Resultado:** BLOCK (ronda 1) → BLOCK acotado (ronda 2) → correcciones R10/R12/R15 aplicadas, sin tercera ronda autorizada
> **Rondas:** 2/2
> **Triaje:** R9–R16 aceptados; ronda 2 cerró R9/R11/R13/R14/R16 y abrió R10/R12/R15, ya corregidos y cubiertos por pruebas propias
> **Modelo efectivo:** `sdd-implementation-reviewer` · `openai-codex/gpt-5.6-sol` · effort high
> **Método:** revisión independiente de solo lectura sobre los candidatos `2b6954e` (ronda 1) y `c979d8e` (ronda 2), base `e52e978`, sin modificar archivos
> **Fecha:** 2026-09-16

### Ronda 2 (delta de R9–R16) — hallazgos y resolución

| ID | Estado ronda 2 | Decisión y corrección |
|---|---|---|
| R9, R11, R13, R14, R16 | Resueltos por el revisor | Sin acción adicional. |
| R10 | Abierto | Aceptado. `EnsureSingleDefaultPerCurrencyAsync` excluye ahora todas las versiones del mismo root: una cuenta default puede revisarse conservando su condición, y solo otra cuenta de la misma moneda conflictúa. Evidencia: `Revising_the_default_account_keeps_its_condition_and_another_account_conflicts`. |
| R12 | Abierto | Aceptado. El control de solape recorre todas las versiones ACTIVE del selector, no solo el puntero current, así que retirar con una versión INACTIVE no reabre la ventana histórica. Evidencia: `An_active_window_overlapping_a_retired_active_version_is_rejected`. |
| R15 | Abierto | Aceptado. `IFileStorage` expone `IsAvailableAsync`: `S3FileStorage` hace una lectura real de una clave y readiness degrada con `SUPPLIER_ATTACHMENT_STORAGE_UNAVAILABLE` cuando el bucket es inalcanzable. Evidencia: `Readiness_degrades_when_the_agreement_storage_is_unreachable` (E2E con storage no disponible) y `S3FileStorage.IsAvailableAsync`. |

Presupuesto de revisión agotado (2/2 llamadas automáticas): la corrección de R10/R12/R15 está respaldada por pruebas propias y por el contraste con los hallazgos, pero no por una tercera ronda automática. Una ronda adicional exige autorización explícita del usuario.

### Ronda 1 — hallazgos y resolución

| ID | Severidad | Regla | Decisión y corrección |
|---|---|---|---|
| R9 | blocker | REQ-09, CA-06 | Aceptado. El proveedor emite la provenance publicada `supplier-facts/<supplier_fact_snapshots_digest>#<line-id>/<snapshot_digest>`. Consecuencia declarada: como el digest del set es por versión de request, una línea retenida vuelve a ser material y exige task nueva, resultado que SPEC 06 REQ-08 admite expresamente («cualquier diferencia o duda crea task nueva»); la expectativa histórica de carry-forward en `Revision_supersedes_with_retained_added_lines_and_verified_materiality` se actualiza con esa justificación. Si el producto quiere conservar carry-forward, requiere un delta de SPEC 09 sobre REQ-09. |
| R10 | blocker | REQ-05, NFR-03, CA-04 | Aceptado. `SupplierBankingCommands` persiste key y refs antes del sobre: un replay con la misma carga descifra la versión registrada y devuelve la misma referencia, y una carga distinta da `409`. El default dejó de moverse al guardar: se publica en `ApplyOperationalDefaultsAsync` cuando la versión del proveedor que la referencia queda aprobada. |
| R11 | blocker | REQ-05, NFR-06, CA-04 | Aceptado. `AccountHolder` deja de ser columna en claro: vive solo dentro del plaintext AEAD. |
| R12 | blocker | REQ-06, REQ-07, CA-05 | Aceptado en sus tres partes. El índice único del selector deja de llevar el filtro implícito `[ProductId] IS NOT NULL` (se reemplaza por un predicado simple siempre verdadero), y `SaveAsync` valida que la revisión conserve el selector exacto de su raíz y que una ventana ACTIVE no solape la versión vigente del mismo selector. |
| R13 | blocker | REQ-03, REQ-11, NFR-03, CA-03 | Aceptado. La materialización corre en una sola transacción con lock del proveedor y relectura del proposal dentro de ella (reintento e instancias concurrentes no duplican versión); el outbox publica el mismo `event_id` en la fila y en el payload. |
| R14 | blocker | REQ-07, REQ-11, CA-02, CA-08 | Aceptado. El catálogo lo administra `PROCUREMENT_BUYER` con assignment `ORGANIZATION` (el approver decide, no edita), las lecturas del master y del catálogo exigen assignment organizacional, y se añaden las lecturas gobernadas `GET entries/{id}` y `GET entries/{id}/history`. |
| R15 | blocker | REQ-12, CA-08 | Aceptado. Readiness autentica cada sobre bancario almacenado, recompone el digest de la versión operacional, exige exactamente un fact owner y resuelve el storage de acuerdos con una sonda de lectura. |
| R16 | blocker | CA-01/03/04/05/07/08 | Aceptado. Nueva suite `SupplierHardeningIntegrationTests` (6): carrera de dos writers, replay y conflicto del comando bancario con default diferido, selector duplicado/cambio de selector/solape, redelivery del resultado de approval con una sola versión, attempt terminal y processor sin registro, y corrupción detectada por el diagnóstico. Total de integración: 125 casos. |
