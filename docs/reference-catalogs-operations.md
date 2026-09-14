# Operación de los catálogos de referencia (Cost Centers y Spend Categories)

## Orden de despliegue

1. Aplicar la migración `Spec07ReferenceCatalogs` antes de publicar la API:
   `dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api`.
   El esquema `ReferenceCatalog` es aditivo: raíces `CostCenters`/`SpendCategories` con puntero
   `CurrentVersion` y versiones append-only `CostCenterVersions`/`SpendCategoryVersions`. No
   modifica snapshots, attestations, scopes ni digests históricos.
2. Desplegar primero el schema y los readers, después la administración y la lectura de referencia,
   luego la validación de `COST_CENTER` en Organization/Policy/Approval (los scopes históricos
   siguen siendo legibles) y por último los seis registros de owner y los dos catálogos Policy
   exact-one. El preflight exige SPEC 01/02/03/05/06 integradas y un recorrido
   requester → attestation real → Policy → Approval con scope Cost Center.
3. Comprobar `/health/purchase-request`: informa `DEGRADED` con códigos exactos cuando un slot de
   owner, un catálogo Policy, el storage o el puntero `current` están ausentes, ambiguos o
   corruptos. Un catálogo **vacío es configuración empresarial válida** y no degrada por sí solo.
4. Crear los catálogos con `ADMIN` organizacional antes de presentar Purchase Requests reales. No
   se fabrican entradas baseline, no se promueven doubles de test y no se corrigen digests a mano.

## Contratos y determinismo

- Los Cost Centers son entidades versionadas: `department_id` es la identidad estable del Department
  propietario y la versión current del Department se resuelve al proyectar o atestiguar. Renombrar un
  Department **no** versiona Cost Centers en cascada; un draft que conserve la referencia antigua
  debe revisarse antes de presentarse.
- Las Spend Categories usan `catalog=SPEND_CATEGORY` con `code`, `version` y `digest`. El digest es
  SHA-256 de `policy-canonical-json/v1` sobre el preimage exacto `spend-category/v1`:
  `{canonicalization_version, code, contract_version, name, organization_id, version}` (sin estado,
  actor, motivo ni timestamps). Vector dorado y pruebas en
  `tests/ProcureToPay.UnitTests/ReferenceCatalogs/ReferenceCatalogCanonicalizationTests.cs`.
  Cambiar propiedades o interpretación del preimage exige nueva `canonicalization_version`; cambiar
  el schema documental sin tocar el preimage exige nueva `contract_version`.
- Cada mutación exige motivo y, cuando el agregado existe, `expected_version`; escribe su audit
  append-only en la misma transacción. Código, organización, identidad y versiones previas no se
  modifican ni eliminan: reasignar Department, renombrar, desactivar o reactivar crean una versión
  sucesora con `predecessor_version`.

## SCOPES

- `ORGANIZATION` cubre cualquier Cost Center; `COST_CENTER` cubre solo el mismo código sin distinguir
  mayúsculas/minúsculas; `DEPARTMENT` **no** cubre implícitamente sus Cost Centers ni al revés.
- El scope administrativo persiste el código estable; el `decision-scope/v1` de Approval congela el
  par UUID/versión y Approval lo resuelve contra el catálogo current antes de la elegibilidad. Un
  scope histórico conserva sus bytes y digest, pero no se usa para una nueva asignación o decisión si
  su referencia dejó de ser current y activa.
- Un Cost Center con assignments o grants no revocados —presentes o futuros— no puede desactivarse:
  revocar primero los scopes, lo que dispara la reconciliación de Approval existente. Un Department
  tampoco se desactiva mientras un Cost Center current activo lo referencie.

## Owners y catálogos Policy

| Slot (`assertion_type` / `reference_type`) | `owner_id` | `owner_contract_version` |
| --- | --- | --- |
| `ACTIVE_IN_ORGANIZATION` / `LEGAL_ENTITY` | `organization-domain` | `organization-db/v1` |
| `ACTIVE_IN_ORGANIZATION` / `USER` | `organization-domain` | `organization-db/v1` |
| `ACTIVE_IN_ORGANIZATION` / `DEPARTMENT` | `organization-domain` | `organization-db/v1` |
| `ACTIVE_IN_ORGANIZATION` / `COST_CENTER` | `cost-center-domain` | `cost-center-db/v1` |
| `COST_CENTER_OWNED_BY_DEPARTMENT` / `COST_CENTER` | `cost-center-domain` | `cost-center-db/v1` |
| `ACTIVE_IN_ORGANIZATION` / `SPEND_CATEGORY` | `spend-category-domain` | `spend-category-db/v1` |

- El registry resuelve la pareja exacta `(assertion_type, reference_type)`; `ReferenceType` nunca es
  `null` y una familia ausente **no** se sirve con un owner genérico. Cero o más de un registro falla
  `503` antes de escribir attestation o manifest.
- Los catálogos Policy exact-one son `COST_CENTER` (`cost-center-db/v1`) y `SPEND_CATEGORY`
  (`spend-category-db/v1`); su lookup está ligado a `organization_id`. Con
  `Policy:ReferenceCatalog:BaseUrl` configurado, el despliegue HTTP debe servir ambas familias en la
  misma forma exacta.
- Respuestas negativas: una versión no current o una raíz inactiva → `INACTIVE`; ausencia, otra
  organización, digest distinto o relación distinta → `NOT_FOUND`. Purchase Requests proyecta ambos
  como `422`; una respuesta con binding, `owner_id`, contrato o coherencia
  `active`/`status` incorrectos es dependencia inválida y produce `503`.
- `Cost Center Owner` (la responsabilidad funcional de una persona sobre un Cost Center) **no** se
  modela aquí ni se infiere de assignments, grants o catálogo.

## Health

Códigos de `/health/purchase-request` relacionados con los catálogos:

| Código | Significado |
| --- | --- |
| `PURCHASE_REQUEST_OWNER_UNAVAILABLE:<ASSERTION>:<REFERENCE>` | Slot de owner ausente o ambiguo; el código distingue los dos slots Cost Center. |
| `POLICY_REFERENCE_CATALOG_UNAVAILABLE:<CATALOG>` | Catálogo Policy `COST_CENTER` o `SPEND_CATEGORY` sin registro exact-one. |
| `REFERENCE_CATALOG_STORAGE_UNAVAILABLE` | El storage de catálogos no responde. |
| `REFERENCE_CATALOG_CORRUPTED:<CATALOG>` | Puntero `current` inconsistente o digest de Spend Category no reproducible. |

Ningún código incluye ids, códigos de negocio, nombres, digests ni PII.

## Recuperación

1. **Owner o catálogo ausente/ambiguo:** corregir el registro en el despliegue (nunca añadir un
   adapter permisivo) y volver a comprobar health. `submit` permanece `503` hasta entonces; no se
   escribe manifest parcial.
2. **Referencia stale o inactiva:** el `422` no se corrige editando la attestation. Revisar la
   Purchase Request para obtener una versión con referencias current, o reactivar/crear la versión
   correcta del catálogo y presentar de nuevo.
3. **Corrupción:** detener mutaciones, identificar la fila inconsistente y corregir **hacia
   adelante** con una versión nueva; las versiones históricas y sus digests no se editan. Si el
   puntero `current` apunta a una versión inexistente, restaurar desde backup y reconstruir el
   catálogo con el procedimiento de alta.
4. **Rollback:** deshabilitar mutaciones y nuevos submit conserva readers, versiones, audit y
   consumers. Tras publicar scopes `COST_CENTER`, volver a una versión que los rechace no es
   soportado: mantener compatibilidad de lectura y corregir hacia adelante. No hay migración
   descendente destructiva.
5. **Reconciliación:** revocar un scope `COST_CENTER` solicita la reconciliación de Approval ya
   existente (presupuesto de 60 segundos de SPEC 03); no se crea una corrida nueva por cambio de
   catálogo.
