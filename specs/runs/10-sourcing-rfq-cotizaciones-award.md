# RUN SPEC 10 — Sourcing, RFQ, cotizaciones y award

> **Formato:** sdd-run/v2
> **Estado del run:** En implementación
> **Spec:** specs/10-sourcing-rfq-cotizaciones-award.md
> **Revisión contractual:** 1
> **Commit de la spec:** 6e26b72e7c4fe573e0a8603f9c559b4932acbe34
> **Blob aprobado:** 3e3951ec5a6ac922c18d7f52dfbb8da4c5405e52
> **Digest contractual:** 6172c3d26718271b05ad262636f6410fc0d8a5d70a2a4e868ba136a111a070f3
> **Rama base:** main
> **Commit base:** 6e26b72e7c4fe573e0a8603f9c559b4932acbe34
> **Rama de implementación:** spec-10-sourcing-rfq-cotizaciones-award
> **Aislamiento Git:** Rama dedicada
> **Modo de revisión:** final
> **Iniciado:** 2026-09-16 10:31 -0500
> **Actualizado:** 2026-09-16 12:32 -0500
> **HEAD verificado:** Pendiente
> **Commit de integración:** Pendiente

## Línea base

- `dotnet build ProcureToPay.sln --no-restore --nologo -v q` → 0 errores, 31 avisos preexistentes (xUnit2031/xUnit2013/EF1002/EF1003 en suites de Suppliers y ReferenceCatalogs).
- `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` → 222/222 correctos antes de tocar código.
- `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` (Docker) → línea base no re-ejecutada completa; la clase `Sourcing` parte de cero tests y no altera las suites existentes.
- Nota: `--nologo` no es compatible con el runner Microsoft.Testing.Platform en `dotnet test` (sale con código 5 sin ejecutar); se usa el comando documentado en AGENTS.md.

## Progreso por tareas

| Tarea | Estado | Evidencia | HEAD/checkpoint |
|---|---|---|---|
| T-01 | Verificada | Sourcing, takeover y RFQ: `src/ProcureToPay.Domain/Modules/Sourcing/{SourcingCodes,SourcingModels,SourcingCanonicalizer,SourcingCommandFingerprints,SourcingExceptions}.cs`, `src/ProcureToPay.Infrastructure/Persistence/Sourcing/{SourcingPersistenceModels,SourcingModelConfiguration,SourcingSerialization,SourcingProcessService}.cs`, migración `20260916154457_Spec10SourcingCore`, guardas de takeover en `PurchaseRequestPersistenceService`, `SourcingController` (procesos/RFQ). Pruebas: `SourcingCanonicalizerTests` (unitarias) y `SourcingProcessIntegrationTests` (SQL: takeover, carrera de versión, append-only). | working-tree sobre 6e26b72 · CP-01 |
| T-02 | Verificada | Quotations y attachments: `SourcingQuotationService` (staging/confirmación/descarga, versiones append-only, puntualidad contra el deadline vigente, review server-side, conteo por línea/Supplier), `QuotationLineScopeRecord`, endpoints de quotation/attachments. Pruebas: unitarias de reloj/dinero/reconciliación y `SourcingProcessIntegrationTests` (conteo, extensión no retroactiva, attachment sellado). | working-tree sobre 6e26b72 · CP-01 |
| T-03 | Pendiente | — | — |
| T-04 | Verificada | Motor reproducible (`SourcingEvaluation.cs`, `SourcingEvaluationEngine.cs`) y persistencia versionada: `SourcingEvaluationService` (FX con evidencia confirmada, score manual con quotation válida, una versión por comando, reemplazo rechazado si la current fue consumida, digest recomputado al leer), `SourcingQuotationQueries` compartida con el conteo, migración `20260916165427_Spec10SourcingEvaluation` (append-only + CHECK de rate/score), endpoints de FX/manual/evaluación. Pruebas: `SourcingEvaluationEngineTests` (13 unitarias) y `SourcingEvaluationIntegrationTests` (5 sobre SQL Server, 12 en la clase). | working-tree sobre 6e26b72 · CP-03 |
| T-05 | Verificada | Catálogo y selección: `SourcingSelection.cs` (recomendado decidido por el servidor, desviación con justificación 1–1.000, partición por Supplier/moneda/términos y orden UUID canónico), `SourcingEvaluationDocument` (reconstruye el resultado por línea desde el documento persistido y verifica el digest), `SourcingSelectionService` (una selección current por línea, versión esperada, elegibilidad por quotation `ON_TIME+VALID`, restricción `supplier_ref` de la PR, partition) y migración `20260916173711_Spec10SourcingSelection`. Pruebas: `SourcingSelectionTests` (8 unitarias) y `SourcingSelectionIntegrationTests` (4 SQL). Ruta de catálogo sin RFQ (REQ-06) queda para el bloque 3 | working-tree sobre 6e26b72 · CP-04 |
| T-06 | Pendiente | — | — |
| T-07 | Pendiente | — | — |
| T-08 | Pendiente | — | — |
| T-09 | Pendiente | — | — |

## Checkpoints

- **CP-01 (2026-09-16 11:06 -0500) · Bloque 1 (T-01, T-02)** — árbol probado: `working-tree sobre 6e26b72` (sin commit).
  - Tests ejecutados: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` → 239/239 correctos (17 nuevas de Sourcing); `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` → 134/134 correctos sobre SQL Server 2022 con Testcontainers y migración real (7 nuevas de Sourcing); `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` → 30/30 correctos. `dotnet build ProcureToPay.sln --no-restore` → 0 errores, avisos preexistentes. `git diff --check` limpio.
  - Decisión interna reversible: los predecesores exigidos por REQ-01 son todos los prerequisites del caso current cuyo `OwnerAdapterId` no es `quotation-status-owner` ni `procurement-stage-owner` (los dos nodos de etapa PROCUREMENT que este módulo satisface); se documenta en `SourcingProcessService`.
  - Defectos corregidos durante el bloque: `OccurredAt` de una transición RFQ usaba el reloj real en lugar del reloj del comando (rompía la reproducibilidad del deadline vigente); `GetProcessAsync` servía una entidad rastreada obsoleta tras un update condicional.
  - Pendiente inmediato: Bloque 2 (T-03 waiver, T-04 evaluación/FX, T-05 catálogo/selección).
- **CP-02 (2026-09-16 11:43 -0500) · T-04 parcial (motor de evaluación)** — árbol probado: `working-tree sobre 6e26b72` (sin commit).
  - Tests ejecutados: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` → 252/252 correctos (13 nuevos del motor); `dotnet build ProcureToPay.sln --no-restore` → 0 errores (27 avisos preexistentes); `git diff --check` limpio.
  - Delta aditivo respecto de CP-01: solo tipos nuevos de dominio y pruebas unitarias nuevas; no hay cableado nuevo, por lo que la certificación de integración/E2E de CP-01 sigue siendo válida para el código existente. La próxima suite completa se ejecutará sobre el árbol estable del gate.
  - Pendiente de T-04: persistencia (`QuoteEvaluationVersion`, `SourcingFxSnapshot`, `manual-criterion-input`), servicio de evaluación versionada que invalide/versione en cada cambio material y endpoints de lectura; después T-03, T-05 y Bloques 3–4.
- **CP-03 (2026-09-16 12:12 -0500) · T-04 verificado (evaluación versionada)** — árbol probado: `working-tree sobre 6e26b72` (sin commit).
  - Tests ejecutados: `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` → 252/252; `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore --filter-class "*Sourcing*"` → 12/12 sobre SQL Server 2022 con la migración nueva; `dotnet build ProcureToPay.sln --no-restore` → 0 errores; `git diff --check` limpio.
  - Correcciones derivadas de las pruebas: el motor deduplica participants por identidad de quotation (una oferta sirve varias líneas sin repetir el mismo score), y el trigger de `QuoteEvaluationVersions` pasó de bloquear todo update a admitir solo la transición de consumo en un sentido.
  - Pendiente inmediato: suite completa sobre el árbol estable antes del gate, y los bloques 2 restantes (T-03 waiver, T-05 catálogo/selección) y 3–4.
- **CP-04 (2026-09-16 12:32 -0500) · T-05 verificado (selección humana)** — árbol probado: `working-tree sobre 6e26b72` (sin commit).
  - Tests ejecutados sobre el árbol estable: unitarias 260/260; integración completa 143/143 sobre SQL Server 2022 (16 de Sourcing); API/E2E 30/30; `dotnet build ProcureToPay.sln --no-restore` 0 errores; `git diff --check` limpio.
  - Alcance de T-05 en este checkpoint: selección por línea, desviación auditada, partición y elegibilidad; la omisión gobernada de RFQ por catálogo (REQ-06) se implementará junto al provider `SOURCING_PO` (bloque 3) porque comparte el hecho de catálogo congelado y la evaluación Policy.

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Parcial | Integración SQL: takeover único y liberación, cancelación de PR bloqueada con takeover vivo, replay idéntico y conflicto de payload distinto, carrera `state=ACTIVE AND version=expected` (rama perdedora relee), draft sin takeover. Falta la evidencia API/E2E de autorización y de la carrera cancel vs publish. | SourcingProcessIntegrationTests |
| CA-02 | Parcial | Unitarias: reconciliación monetaria `decimal(38,12)`/ToEven, límites de peso/decimal, revisión VALID/INVALID/WITHDRAWN. Integración: extensión no reclasifica, respuesta un minuto después del deadline queda LATE, versiones y audit append-only, attachment sellado. Falta la matriz API/E2E. | SourcingCanonicalizerTests + SourcingProcessIntegrationTests |
| CA-03 | Parcial | Integración: conteo por línea con versiones current ON_TIME+VALID, excluye LATE/PENDING/retiradas, y el same-supplier no cuenta dos veces. Faltan API/E2E y captura negativa de logs/trazas. | SourcingProcessIntegrationTests |
| CA-04 | Pendiente | — | — |
| CA-05 | Parcial | Goldens unitarios (FX 12 decimales, `PRICE`, `DELIVERY_TIME` con cero, `WARRANTY` con cero, acotado 0–100, redondeo `ToEven`, empate conserva el conjunto, digest invariante a permutación) y unitarias de selección (recomendado sin justificación, desviación con justificación obligatoria, partición por Supplier/moneda/términos, línea repetida). Integración: documento persistido == preimagen del digest, versión consumida irreemplazable, FX y score manual con evidencia/quotation válida, selección recomendada y partición, versión esperada, sucesor append-only, restricción `supplier_ref` de la PR. Falta la evidencia API/E2E de la desviación justificada. | SourcingEvaluation*Tests + SourcingSelectionTests + SourcingSelectionIntegrationTests |
| CA-06 | Pendiente | — | — |
| CA-07 | Pendiente | — | — |
| CA-08 | Pendiente | — | — |
| CA-09 | Pendiente | — | — |
| CA-10 | Pendiente | — | — |

## Desviaciones y bloqueos

- Ninguno.

## Verificación independiente

> **Resultado:** Pendiente
> **Rondas:** 0/2
> **Triaje:** Pendiente
> **Modelo efectivo:** Pendiente
> **Método:** Pendiente
> **Fecha:** Pendiente
