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
> **Actualizado:** 2026-09-16 11:43 -0500
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
| T-04 | Parcial | Motor reproducible de evaluación: `src/ProcureToPay.Domain/Modules/Sourcing/SourcingEvaluation.cs` (`sourcing-fx-snapshot/v1`, `manual-criterion-input/v1`, `criterion-score/v1`, `quotation-score/v1`, `evaluation-line-result/v1`, `quote-evaluation-version/v1`, orden UUID canónico) y `SourcingEvaluationEngine.cs` (REQ-07 fórmulas y acotado 0–100, redondeo 4 decimales `ToEven`, REQ-08 rate 1 o snapshot obligatorio y normalización a 12 decimales, criterio con peso positivo sin dato bloquea, REQ-09 empate conserva el conjunto recomendado). Pruebas: `SourcingEvaluationEngineTests` (13 goldens). Pendiente: persistencia de `QuoteEvaluationVersion`/FX/manuales, servicio de evaluación versionada y API. | working-tree sobre 6e26b72 · CP-02 |
| T-05 | Pendiente | — | — |
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

## Evidencia de aceptación

| Criterio | Estado | Evidencia | Verificador |
|---|---|---|---|
| CA-01 | Parcial | Integración SQL: takeover único y liberación, cancelación de PR bloqueada con takeover vivo, replay idéntico y conflicto de payload distinto, carrera `state=ACTIVE AND version=expected` (rama perdedora relee), draft sin takeover. Falta la evidencia API/E2E de autorización y de la carrera cancel vs publish. | SourcingProcessIntegrationTests |
| CA-02 | Parcial | Unitarias: reconciliación monetaria `decimal(38,12)`/ToEven, límites de peso/decimal, revisión VALID/INVALID/WITHDRAWN. Integración: extensión no reclasifica, respuesta un minuto después del deadline queda LATE, versiones y audit append-only, attachment sellado. Falta la matriz API/E2E. | SourcingCanonicalizerTests + SourcingProcessIntegrationTests |
| CA-03 | Parcial | Integración: conteo por línea con versiones current ON_TIME+VALID, excluye LATE/PENDING/retiradas, y el same-supplier no cuenta dos veces. Faltan API/E2E y captura negativa de logs/trazas. | SourcingProcessIntegrationTests |
| CA-04 | Pendiente | — | — |
| CA-05 | Parcial | Goldens unitarios: normalización FX a 12 decimales, `PRICE` como menor/actual, `DELIVERY_TIME` con `lowest_days=0`, `WARRANTY` con `highest_days=0`, acotado 0–100, redondeo a 4 decimales `ToEven`, empate conserva el conjunto recomendado y permutar entradas no cambia el digest. Faltan selección humana, versión esperada y auditoría de la justificación de desviación. | SourcingEvaluationEngineTests |
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
