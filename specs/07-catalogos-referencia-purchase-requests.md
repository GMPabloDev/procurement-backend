# SPEC 07 — Catálogos de referencia para Purchase Requests y scope Cost Center

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** En implementación
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** 64daf9c60d28f2d9a2dc6c286a8e2f12d83dd97a01a7651a3a72e73681f0eed1
> **Fecha:** 2026-09-14
> **Actualizada:** 2026-09-14
> **Aprobada el:** 2026-09-14
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Proveer Cost Centers y Spend Categories versionados y confiables para que Purchase Requests pueda atestiguar, evaluar y someter referencias reales, incluida la elegibilidad con scope `COST_CENTER`, sin adapters controlados ni fallos de configuración conocidos.
> **Depende de:** Ninguna
> **Modifica:** SPEC 01, SPEC 02, SPEC 03, SPEC 05, SPEC 06
> **Reemplaza:** Ninguna

## Contexto

SPEC 06 dejó deliberadamente la administración de Cost Centers y Spend Categories fuera de alcance y exige owners versionados exact-one para atestiguarlos. En producción solo se registra `OrganizationReferenceOwner`; Cost Center y Spend Category siguen sin `IPurchaseRequestReferenceOwner` ni `IPolicyReferenceCatalog`, mientras los recorridos de integración usan doubles que aceptan toda referencia y el E2E de health espera `PURCHASE_REQUEST_OWNER_UNAVAILABLE:COST_CENTER|SPEND_CATEGORY`. Además, SPEC 01, SPEC 03 y SPEC 05 conservan `COST_CENTER` como dimensión reservada pero rechazada. El resultado es que una Purchase Request puede guardar refs sintácticas, pero no completar con datos reales el recorrido attestation → Policy → Approval.

## Alcance

### Incluye

- Catálogo organizacional de Cost Centers imputables, versionados, con relación obligatoria y versionada Cost Center→Department.
- Catálogo organizacional versionado de Spend Categories con digest público reproducible para `VersionedCodeRef`.
- Administración, lectura de referencia, historia y audit de ambos catálogos con concurrencia y conservación histórica.
- Habilitación de `COST_CENTER` en role assignments, Approval Authority Grants, elegibilidad y `decision-scope/v1`.
- Adapters in-process exact-one de `IPurchaseRequestReferenceOwner` para los slots obligatorios de Purchase Requests y de `IPolicyReferenceCatalog` para `COST_CENTER` y `SPEND_CATEGORY`.
- Integración real con attestation, provider Policy, submit, Approval y health, sin owners ni catálogos permisivos de test en el recorrido certificado.
- Migración, operación, telemetría minimizada y regresión diferencial de SPEC 01, 02, 03, 05 y 06.

### No incluye

- Budgets, posiciones o movimientos presupuestarios; el prerequisite `budget-check-owner/v1` continúa fail-closed.
- La responsabilidad funcional `Cost Center Owner`, su visibilidad de saldos o analytics; no es un System Role y requiere una entrega posterior.
- Grupos o jerarquías de Cost Centers, Cost Centers no imputables, proyectos u otras dimensiones analíticas.
- Administración de Purchase Types, productos, Supplier Master, risk schemas o FX; sus owners siguen siendo independientes.
- Cambiar reglas, thresholds, requisitos o secuencia de Policy/Approval; esta spec habilita referencias y scopes, no crea políticas implícitas.
- Frontend, importaciones masivas, sincronización con ERP ni múltiples organizaciones o Legal Entities.

## Comportamiento esperado

- **REQ-01 — Catálogos versionados e históricos.** Cada Cost Center y Spend Category pertenece a la organización, tiene código estable único sin distinguir mayúsculas/minúsculas, nombre, estado `ACTIVE|INACTIVE` y versión positiva monotónica. Crear produce versión 1; renombrar, cambiar la relación permitida, desactivar o reactivar crea una versión sucesora append-only y mueve atómicamente el puntero current. Código, organización, identidad y versiones previas nunca se cambian ni eliminan. Todo comando exige `expected_version` cuando ya existe el agregado, motivo y control de concurrencia; una carrera confirma una sola sucesora y la perdedora obtiene `409`.

- **REQ-02 — Cost Center imputable y relación exacta.** Un Cost Center current `ACTIVE` referencia exactamente la identidad estable de un Department current `ACTIVE` de la misma organización; un Department puede poseer cero o más Cost Centers. La versión de Cost Center conserva `department_id`, mientras cada proyección/attestation resuelve y congela la versión current del Department: renombrar el Department no crea versiones Cost Center en cascada ni deja inválida su relación, aunque un draft que conserve la antigua `department_ref` debe revisarse antes de atestiguar. Reasignar a otro `department_id` es material, crea nueva versión del Cost Center y exige el Department destino current activo. No se admiten relaciones múltiples, nulas, a otra organización o a un Department inactivo. Un Department no puede desactivarse mientras algún Cost Center current activo lo referencie. Desactivar un Cost Center no borra Purchase Requests ni attestations históricas, pero impide usar esa versión en nuevas attestations y nuevos scopes.

- **REQ-03 — Spend Category reproducible.** Una Spend Category es distinta de Purchase Type y usa `catalog=SPEND_CATEGORY`. Su referencia pública exacta contiene `code`, `version` y `digest`; el digest debe coincidir con la versión current activa y con el preimage `spend-category/v1` definido en Datos y contratos. Código desconocido, versión no current, digest distinto o categoría inactiva se considera referencia no utilizable. No hay categorías baseline, aliases ni defaults embebidos: deben crearse administrativamente.

- **REQ-04 — Scope `COST_CENTER` en roles y authority.** Role assignments y Approval Authority Grants aceptan `COST_CENTER` con el código estable de un Cost Center current activo de la organización. `ORGANIZATION` cubre cualquier Cost Center; `COST_CENTER` cubre solo el mismo código sin distinguir mayúsculas/minúsculas; `DEPARTMENT` no cubre implícitamente sus Cost Centers ni viceversa. Si un requisito contiene varias dimensiones, el mismo assignment y, cuando aplique, un único grant deben cubrirlas todas conforme a SPEC 01. Scope vacío, código inexistente/inactivo, combinación de `ORGANIZATION` con scopes estrechos y duplicados se rechazan. Un Cost Center con assignments o grants activos o futuros no revocados no puede desactivarse; revocarlos conserva historia y dispara la reconciliación existente.

- **REQ-05 — Scope de decisión y catálogos Policy.** `decision-scope/v1` admite una entrada `COST_CENTER` con `reference_id` UUID no vacío y `reference_version` positiva, conservando sin cambios su schema y canonicalización. Policy solo publica/evalúa ese descriptor si un `IPolicyReferenceCatalog` exact-one confirma el Cost Center current activo de la organización; Approval resuelve la misma id/versión a su código estable antes de invocar elegibilidad. El catálogo Policy `SPEND_CATEGORY` confirma organización, código, versión y digest; el de `COST_CENTER`, organización, id y versión. Lookup ausente, ambiguo, timeout o fallo es `503`; referencia inexistente, inactiva, obsoleta o con digest distinto es `422`. Un scope histórico ya persistido conserva bytes y digest, pero no se usa para una nueva asignación o decisión si su referencia dejó de ser current y activa.

- **REQ-06 — Owners Purchase Request exact-one.** El registry resuelve por la pareja exacta `(assertion_type, reference_type)` y no interpreta `ReferenceType=null` como wildcard. En producción existen exactamente estos slots obligatorios: `ACTIVE_IN_ORGANIZATION` para `LEGAL_ENTITY`, `USER`, `DEPARTMENT`, `COST_CENTER` y `SPEND_CATEGORY`, y `COST_CENTER_OWNED_BY_DEPARTMENT` para `COST_CENTER`. Cada registro declara identidad y contrato estables; una implementación puede compartir código, pero cada slot queda declarado una sola vez. Cero o más de un registro, contrato vacío o familia distinta falla `503` antes de persistir attestation o manifest.

- **REQ-07 — Attestation y submit con datos reales.** Para cada línea, el owner Cost Center acepta `ACTIVE_IN_ORGANIZATION` solo si id/versión es current, activa y de la organización; acepta `COST_CENTER_OWNED_BY_DEPARTMENT` solo si esa misma versión enlaza el `department_id` estable y el target contiene la versión current del Department. El owner Spend Category acepta solo `{catalog=SPEND_CATEGORY,code,version,digest}` current y activo. Antes de persistir, Purchase Requests exige que la respuesta ecoe request, instante y source/target, que `owner_id` y `owner_contract_version` coincidan exactamente con el registro resuelto y que `active=true` equivalga a `status=ACTIVE`; `INACTIVE|NOT_FOUND` exige `active=false`, y cualquier otro estado o binding es respuesta inválida fail-closed. Ref ausente, inactiva, stale, digest incorrecto o relación falsa da `422`; owner/catálogo ausente, ambiguo, con respuesta contractual inválida o indisponible da `503 /problems/purchase-request-dependency-unavailable`. No se confirma attestation ni manifest parcial. Con referencias válidas, submit usa esos mismos snapshots para el provider real, los catálogos Policy y un caso Approval cuyo scope `COST_CENTER`, cuando lo exige la política, llega al resolver de elegibilidad sin reinterpretar la ref.

- **REQ-08 — API y autorización de catálogos.** Solo un usuario activo con `ADMIN` de scope `ORGANIZATION` crea, renombra, reasigna, desactiva o reactiva entradas; cada mutación exige motivo, versión esperada cuando aplica y audit append-only atómico. Cualquier usuario empresarial activo puede consultar las proyecciones current activas mínimas necesarias para construir una Purchase Request, siempre en su organización. `AUDITOR` puede leer historia y audit si un assignment `ORGANIZATION` o `COST_CENTER` cubre el objetivo; una Spend Category es configuración organizacional y exige scope `ORGANIZATION`. `ADMIN` no obtiene lectura de Purchase Requests por esta capacidad. Se usan Problem Details: `401`, `403`, `404`, `400`, `409` y `422`; las respuestas y errores no revelan entradas de otra organización.

- **REQ-09 — Health y operación fail-closed.** Health comprueba por separado los seis slots obligatorios de `IPurchaseRequestReferenceOwner`, los registros exact-one `IPolicyReferenceCatalog` de `COST_CENTER|SPEND_CATEGORY`, acceso al storage y consistencia del puntero current/version/digest. Usa la gramática exacta `PURCHASE_REQUEST_OWNER_UNAVAILABLE:<ASSERTION>:<REFERENCE>`, `POLICY_REFERENCE_CATALOG_UNAVAILABLE:<CATALOG>`, `REFERENCE_CATALOG_STORAGE_UNAVAILABLE` y `REFERENCE_CATALOG_CORRUPTED:<CATALOG>`; no colapsa los dos slots Cost Center ni incluye ids, nombres o digests. Un catálogo vacío es configuración empresarial válida y no degrada por sí solo; corrupción, owner/catalog ausente o ambiguo y storage inaccesible degradan readiness. Tras la migración y registros correctos desaparecen los códigos de owner ausente para Cost Center/Spend Category; los owners opcionales de producto, supplier, riesgo o FX solo se exigen cuando una request los usa.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `CostCenter` | id UUID, organización, código estable, current version | Raíz durable; código único case-insensitive y sin borrado físico. |
| `CostCenterVersion` | cost center id, version, nombre, Department id estable, estado, actor, UTC, motivo, predecessor | Append-only; una sola current por raíz. La versión current del Department se resuelve al proyectar/atestiguar, no se copia como relación mutable. |
| `SpendCategory` | organización, código estable, current version | Código es la identidad dentro de `SPEND_CATEGORY`; sin aliases. |
| `SpendCategoryVersion` | code, version, nombre, estado, digest, actor, UTC, motivo, predecessor | Append-only; digest reproducible y una sola current. |
| `AuthorizationScope` | dimension, reference | `COST_CENTER` persiste el código estable; no persiste nombre ni Department inferido. |
| `DecisionScopeEntry` | dimension, reference id, reference version | `COST_CENTER` conserva UUID+versión en `decision-scope/v1`. |
| `PolicyReferenceCatalogRegistration` | catalog id, resolver id, contract version | Exactamente uno para `COST_CENTER` y uno para `SPEND_CATEGORY`. |
| `PurchaseRequestReferenceOwnerRegistration` | assertion type, reference type, owner id, contract version | Exactamente uno por cada slot de REQ-06; no hay wildcard. |
| `ReferenceCatalogAuditRecord` | actor, UTC, acción, objetivo/versiones, scope, before/after, motivo, correlation | Append-only y atómico con la mutación; sin snapshots de Purchase Request. |

### Proyecciones públicas de referencia

- `cost-center-reference/v1` contiene exactamente `{code,department_ref,id,name,version}`; `department_ref` contiene exactamente `{id,version}`. La lista ordinaria solo emite entries current `ACTIVE` de la organización.
- `spend-category-reference/v1` contiene exactamente `{catalog,code,digest,name,version}`, con `catalog=SPEND_CATEGORY`. La lista ordinaria solo emite entries current `ACTIVE` de la organización.
- Las vistas administrativas agregan `status`, predecessor y metadatos de cambio, pero nunca reetiquetan una versión histórica como current.
- Códigos tienen 1–64 caracteres ASCII `[A-Z][A-Z0-9_.:-]*`; nombres, 1–200 Unicode scalars; motivos, 1–1.000. Strings se normalizan NFC y los códigos se publican en mayúsculas.

### Contratos de owners y Policy

- Owner Cost Center: `owner_id=cost-center-domain`, `owner_contract_version=cost-center-db/v1`; cubre los dos slots Cost Center de REQ-06.
- Owner Spend Category: `owner_id=spend-category-domain`, `owner_contract_version=spend-category-db/v1`; cubre su slot de actividad.
- Los slots organizacionales conservan `owner_id=organization-domain`, `owner_contract_version=organization-db/v1`, pero se registran explícitamente por `LEGAL_ENTITY`, `USER` y `DEPARTMENT`.
- `PolicyReferenceLookup` enlaza exactamente `{code,digest,id,organization_id,reference_type,value_kind,version}` con propiedades nullable presentes según kind. Para `COST_CENTER`, `id` y `version` son obligatorios y `code|digest|value_kind` son nulos; para `SPEND_CATEGORY`, `code|digest|version` son obligatorios e `id|value_kind` son nulos. Esta spec modifica el DTO/puerto in-process para transportar `organization_id` y nulos reales: `Guid.Empty` y strings vacíos no sustituyen propiedades nulas. El registro rechaza otra organización o forma.
- Los owners responden `ACTIVE` solo a la coincidencia exacta. Una versión existente pero no current o una raíz inactiva responde `INACTIVE`; ausencia, otra organización, digest distinto o relación distinta responde `NOT_FOUND`. Purchase Requests proyecta ambos resultados negativos como `422` sin revelar cuál existe.

### Canonicalización de Spend Category

`spend_category_digest` usa `policy-canonical-json/v1` y SHA-256 hexadecimal minúsculo. El preimage `spend-category/v1` contiene exactamente, en orden ordinal, `{canonicalization_version,code,contract_version,name,organization_id,version}`. Estado, actor, motivo, timestamps y digest no se incluyen. Cambiar propiedades o interpretación del preimage exige nueva `canonicalization_version`; cambiar el schema documental sin cambiar el preimage exige nueva `contract_version`.

Vector mínimo, bytes UTF-8 completos sin salto final:

```json
{"canonicalization_version":"policy-canonical-json/v1","code":"HARDWARE","contract_version":"spend-category/v1","name":"Hardware","organization_id":"11111111-1111-1111-1111-111111111111","version":1}
```

SHA-256 esperado: `4ed4dc1356c8d2b3dfeacdec46ee7743b9fcfb927d94c75dea8b15c950ed30a1`.

## Impacto sobre especificaciones anteriores

| Contrato anterior | Regla nueva y alcance |
| --- | --- |
| SPEC 01 REQ-03 | Un Department activo referenciado por un Cost Center current activo no puede desactivarse. Renombrarlo conserva su identidad y no versiona Cost Centers en cascada; las nuevas proyecciones usan la nueva versión Department. |
| SPEC 01 REQ-08, `AuthorizationScope` y CA-05 | `COST_CENTER` deja de rechazarse: se valida contra el catálogo, usa código estable y aplica la cobertura exacta de REQ-04. `ORGANIZATION|LEGAL_ENTITY|DEPARTMENT` conservan su semántica. |
| SPEC 02 REQ-05, registros y CA-12 | Existen catálogos productivos exact-one para `COST_CENTER` y `SPEND_CATEGORY`; el lookup queda ligado a organización y valida id/version o code/version/digest. Los catálogos de tipos futuros siguen fail-closed. |
| SPEC 03 REQ-02, resolver y CA-02 | `decision-scope/v1` admite `COST_CENTER` UUID/version y Approval lo resuelve contra el catálogo current antes de elegibilidad; no cambian propiedades, orden ni canonicalización. |
| SPEC 05 REQ-02 | Policy y el adapter dejan de rechazar un descriptor canónico `COST_CENTER` cuando su catálogo exact-one lo confirma; tokens legacy y JSON no canónico continúan rechazados. |
| SPEC 06 REQ-04, REQ-06, migración, health y CA-03/CA-08 | Cost Center y Spend Category dejan de ser owners futuros para los slots obligatorios. Attestation y submit usan implementaciones reales; health sano ya no espera sus códigos `OWNER_UNAVAILABLE`. Owners opcionales siguen por demanda. |

La vigencia de estas modificaciones empieza al integrar SPEC 07. Policies, scopes, attestations, manifests, casos y digests históricos no se reescriben ni se reetiquetan; los payloads `decision-scope/v1` previos permanecen verificables.

## Migración, despliegue y reversión

- La migración crea tablas raíz/versiones para ambos catálogos, constraints de unicidad case-insensitive, current único, predecessor/version monotónica, relación Cost Center→Department e índices de lookup; no fabrica entradas baseline ni modifica snapshots Purchase Request existentes.
- Orden de despliegue: schema y readers compatibles; administración y catálogos; validación de `COST_CENTER` en Organization/Policy/Approval; owners y registros exact-one; por último readiness y tráfico de submit real.
- El preflight exige SPEC 01, 02, 03, 05 y 06 integradas, seis slots PR exact-one, dos catálogos Policy exact-one y ausencia de registros wildcard o duplicados. Luego ejecuta un recorrido requester → attestation real → Policy → Approval con scope Cost Center.
- Requests draft con refs sintéticas, stale o inactivas no se migran ni autocorrigen: deben revisarse con refs current. Attestations y manifests ya confirmados permanecen inmutables; no se regeneran desde el catálogo actual.
- Rollback deshabilita mutaciones y nuevos submit, conserva readers, versiones, audit y consumers. Tras publicar scopes `COST_CENTER` no se vuelve a una versión que los rechace; se mantiene compatibilidad de lectura y se corrige hacia adelante. No hay down migration destructiva.
- `docs/reference-catalogs-operations.md` documenta creación inicial, rollout, diagnóstico de registros, reconciliación, recuperación y reversión desde el artefacto publicado; ningún procedimiento edita digests o current pointers a mano.

## Seguridad y privacidad

- Organización y actor se derivan del contexto autenticado; ningún comando acepta organización o identidad administrativa como fuente de autorización.
- Solo `ADMIN` organizacional muta catálogos o concede scopes. `COST_CENTER` no concede role ni authority por sí mismo; únicamente acota un assignment o grant ya autorizado.
- `Cost Center Owner` no se infiere desde assignments, grants, Department ni catálogo. Tampoco se concede visibilidad presupuestaria.
- Owners y catálogos son in-process. Ids de owner/resolver son identidades contractuales, no credenciales; workloads conservan `issuer + client_id` allowlisted conforme a las specs previas.
- Logs, métricas, health y Problem Details no contienen nombres, códigos empresariales concretos, user ids, motivos, refs completas, digests ni snapshots; audit administrativo conserva solo el before/after necesario y nunca tokens o Purchase Requests completas.

## Requisitos no funcionales

- **NFR-01 — Historia e integridad.** Raíces, versiones y audit permiten reconstruir cada transición; constraints impiden dos current, saltos de versión, relación incompleta y mutación/borrado de versiones históricas.
- **NFR-02 — Determinismo.** Mismos datos de Spend Category producen mismos bytes/digest; lookup y owner responden igual para misma organización, ref e instante confirmado.
- **NFR-03 — Exactly-one lógico.** Registro de owner/catálogo, actualización concurrente y reconciliación producen un solo efecto observable bajo retry, timeout y dos instancias.
- **NFR-04 — Aislamiento y fail-closed.** Ningún lookup, scope, attestation o submit acepta referencias de otra organización, stale, ambiguas o no atestiguadas ni degrada a `ALLOW`.
- **NFR-05 — Operación minimizada.** Health y telemetría distinguen catálogo, assertion, etapa, resultado y duración sin datos empresariales; cambios de scope solicitan reconciliación dentro del presupuesto de 60 segundos de SPEC 03.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | Invariantes/versiones, scope coverage, schemas, owner bindings, registry exact-one y golden independiente | `dotnet test --project tests/ProcureToPay.UnitTests/ProcureToPay.UnitTests.csproj --no-restore` |
| Integración | SQL Server, unicidad/concurrencia, relación Department, catálogos Policy, attestation transaccional y reconciliación | `dotnet test --project tests/ProcureToPay.IntegrationTests/ProcureToPay.IntegrationTests.csproj --no-restore` con Docker/Testcontainers |
| API/E2E | JWT, administración/lectura, errores, health y submit PR→Policy→Approval con scope Cost Center | `dotnet test --project tests/ProcureToPay.ApiE2ETests/ProcureToPay.ApiE2ETests.csproj --no-restore` con Docker/Testcontainers |
| Operación | Migración, startup con registros 0/1/2, catálogo vacío, corrupción, restart y rollback compatible | Pruebas automatizadas y `docs/reference-catalogs-operations.md` |

Todos los criterios son automáticos. Los tests negativos cubren código duplicado por case, expected version stale, Department inválido, relación falsa, digest alterado, versión no current, otra organización, scope mixto inválido, owner y catálogo con cardinalidad 0/2, wildcard, timeout y fallo después de comenzar attestation sin manifest parcial.

## Decisiones

- **DEC-01 — Cost Center es entidad versionada; Spend Category, código versionado.** Conserva las formas `VersionedEntityRef` y `VersionedCodeRef` ya publicadas por SPEC 06; se descarta cambiar el payload de línea.
- **DEC-02 — Relación Department dentro de la versión del Cost Center.** Reasignar es material y produce una versión nueva; se descarta consultar una relación mutable sin versión.
- **DEC-03 — Scope por código estable, decisión por UUID/version.** Assignments/grants conservan la semántica de SPEC 01 y los requirements congelan el snapshot que originó la decisión.
- **DEC-04 — Sin herencia Department↔Cost Center.** Solo `ORGANIZATION` cubre dimensiones inferiores. Se descarta inferir autoridad sobre todos los Cost Centers de un Department porque ampliaría permisos sin declaración explícita.
- **DEC-05 — Adapters reales separados del dominio Purchase Requests.** Los catálogos son owners de sus datos y PR solo atestigua respuestas; se descarta promover doubles o incorporar tablas de catálogo bajo Purchase Requests.
- **DEC-06 — Sin Cost Center Owner funcional en esta entrega.** El término owner de esta spec designa adapters de referencia. La responsabilidad de persona sobre un Cost Center no se infiere ni concede permisos.

## Plan de implementación

### Bloque 1 — Catálogos e historia

- **T-01 — Modelo, persistencia y migración.** Implementar raíces/versiones, relación exacta, digest, constraints, concurrencia y audit. Cubre: REQ-01, REQ-02, REQ-03, NFR-01, NFR-02, NFR-03, NFR-04, CA-01.
- **T-02 — API administrativa y lectura de referencia.** Implementar mutaciones ADMIN, proyecciones activas, historia AUDITOR, validaciones y Problem Details. Cubre: REQ-01, REQ-02, REQ-03, REQ-08, NFR-01, NFR-04, CA-01, CA-02.

**Resultado verificable:** catálogos reales producen refs current reproducibles y preservan cada versión bajo carreras, desactivación y reactivación.

### Bloque 2 — Scope y Policy

- **T-03 — Habilitar AuthorizationScope Cost Center.** Validar scopes de assignment/grant contra el catálogo, conservar cobertura exacta, bloqueos de desactivación y reconciliación. Cubre: REQ-04, REQ-08, NFR-03, NFR-04, NFR-05, CA-03.
- **T-04 — Habilitar decision scope y catálogos Policy.** Resolver UUID/version Cost Center, registrar `COST_CENTER|SPEND_CATEGORY` exact-one y ligar organización en lookups. Cubre: REQ-05, NFR-02, NFR-04, CA-04.

**Resultado verificable:** una política canónica con scope Cost Center se publica, evalúa y asigna solo a candidatos cuyo mismo assignment/grant cubre el código exacto.

### Bloque 3 — Owners y submit real

- **T-05 — Registrar owners exact-one.** Implementar owners Cost Center/Spend Category y registros explícitos de los seis slots, con cardinalidad, contratos y negativos. Cubre: REQ-06, REQ-07, NFR-02, NFR-03, NFR-04, CA-05.
- **T-06 — Integrar attestation y submit.** Sustituir doubles en el recorrido certificado, verificar relation/digest y probar atomicidad, Policy y Approval. Cubre: REQ-07, NFR-03, NFR-04, CA-05, CA-06.

**Resultado verificable:** una PR con refs obtenidas de los catálogos confirma manifest/evaluación/caso una sola vez; cualquier ref o registro incorrecto falla cerrado sin evidencia parcial.

### Bloque 4 — Health, operación y regresión

- **T-07 — Health, telemetría y runbook.** Diagnosticar slots/catálogos/storage, actualizar expectativa E2E, documentar rollout/recovery y ejecutar regresión diferencial. Cubre: REQ-08, REQ-09, NFR-05, CA-07, CA-08.

**Resultado verificable:** readiness es sano con registros correctos y catálogos aun vacíos, degrada con 0/2/corrupción sin filtrar datos y el E2E real completa submit con scope Cost Center.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-03, NFR-01, NFR-02, NFR-03 | Crear/update/deactivate/reactivate concurrente deja una sola current y toda versión/audit anterior intacta; código duplicado case-insensitive, salto, Department stale/inactivo o predecessor incorrecto falla. El golden de Spend Category coincide y cambia al alterar nombre, organización o versión. | Automática: unitarias con fixture independiente e integración SQL multi-DbContext/migración. |
| CA-02 | REQ-01, REQ-02, REQ-03, REQ-08 | ADMIN administra con motivo/version; usuario activo obtiene solo refs current mínimas de su organización; AUDITOR scoped ve historia permitida y no muta. `401/403/404/400/409/422` y límites exactos no filtran datos ni dejan audit de éxito parcial. | Automática: API/E2E JWT y persistencia transaccional. |
| CA-03 | REQ-04, REQ-08, NFR-03, NFR-04, NFR-05 | Assignment y grant aceptan un Cost Center activo y el resolver exige que un mismo assignment/grant cubra todos los scopes. Global cubre; otro Cost Center y Department no cubren. Inexistente/inactivo/duplicado se rechaza y desactivación con referencias no revocadas falla; la revocación dispara reconciliación dentro de 60 s. | Automática: matriz unitaria de coverage, integración Organization/Approval y API/E2E. |
| CA-04 | REQ-05, NFR-04 | `decision-scope/v1` con Cost Center exacto conserva round-trip/digest y llega a elegibilidad. Los catálogos Policy 1/0/2, otra organización, id/version/digest stale, timeout y descriptor no canónico producen aceptación o `422/503` según contrato, sin evaluación/caso parcial. | Automática: contrato, Policy integration y Approval assignment con catálogo real. |
| CA-05 | REQ-06, REQ-07, NFR-02, NFR-03, NFR-04 | Los seis slots resuelven exactamente uno; 0/2/wildcard fallan `503`. Owners reales ecoan source/target/instante e identidad/contrato exactos, y solo `active=true/status=ACTIVE` es positivo; mismatch o estado incoherente falla `503`. Cost Center válido y su relación current, más Spend Category code/version/digest, producen assertions reproducibles, mientras stale/inactivo/relación falsa/digest distinto da `422` sin manifest. | Automática: registry unitario, owner SQL y attestation transaccional. |
| CA-06 | REQ-07, NFR-03, NFR-04 | E2E sin owners ni catálogos permisivos crea PR con refs de lectura, atestigua, evalúa Policy, abre Approval con scope Cost Center y asigna solo al candidato cubierto. Replay recupera attempt/bundle/caso; fallo owner/catalog y carrera con cambio de catálogo no declaran submit exitoso ni duplican artefactos. | Automática: integración y API/E2E PR→Policy→Approval con fault injection y dos instancias. |
| CA-07 | REQ-08, REQ-09, NFR-05 | Health sano no contiene códigos de ausencia de Cost Center/Spend Category con registros correctos y acepta catálogo vacío; cada cardinalidad 0/2, storage/corrupción y contrato inválido degrada con la gramática exacta de REQ-09. Los dos slots Cost Center generan códigos distintos y ninguno expone códigos de negocio, ids, nombres, digests o PII. | Automática: health API/E2E y captura de telemetría. |
| CA-08 | REQ-09, NFR-05 | Migración y orden de rollout conservan scopes/digests históricos, rollback mantiene lectura, y las suites de SPEC 01/02/03/05/06 permanecen verdes tras sustituir expectativas de rechazo/degraded únicamente donde esta spec las modifica. | Automática: migración sobre baseline, suites completas, `git diff --check` y runbook ejercitado. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Owner o catálogo permisivo | Una ref aleatoria permite submit | Lookup exacto, E2E sin doubles y CA-04/CA-06. |
| Relación mutable rompe provenance | Department cambia sin versión Cost Center | Versión append-only y CA-01/CA-05. |
| Scope amplía permisos por jerarquía implícita | Department cubre Cost Centers automáticamente | DEC-04 y matriz CA-03. |
| Digest de categoría no reproducible | Owner y Policy aceptan bytes distintos | Preimage/golden independiente y CA-01/CA-05. |
| Registro genérico oculta cardinalidad | `ReferenceType=null` satisface varios slots | Parejas explícitas, rechazo de wildcard y health por slot. |
| Cambio entre attestation y Policy | Owner acepta y catálogo ve otra current | Fallo cerrado, transacciones/attempt recuperable y CA-06; no se reescribe attestation. |
| Rollback rechaza scopes ya publicados | Versión anterior no parsea `COST_CENTER` | Rollout por fases y corrección hacia adelante de Migración. |
| Se confunde owner técnico con responsabilidad humana | Registro adapter concede visibilidad/authority | Exclusión explícita y DEC-06. |
