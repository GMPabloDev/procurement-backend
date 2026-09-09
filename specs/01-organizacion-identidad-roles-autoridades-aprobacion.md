# SPEC 01 — Organización, identidad, roles y autoridades de aprobación

> **Formato:** sdd/v3
> **Estado:** Aprobada
> **Ejecución:** Lista para integrar
> **Vigencia:** Pendiente
> **Revisión:** 1
> **Digest contractual:** c5f194a4dca2597aff6cbbace581b1f9212197416c2918559e1a124484de458e
> **Fecha:** 2026-09-08
> **Actualizada:** 2026-09-08
> **Aprobada el:** 2026-09-08
> **Aprobada por:** osiosad mediante specctl approve
> **Objetivo:** Proveer una base organizacional y de autorización en la que cada identidad autenticada tenga un perfil empresarial controlado y solo resulte elegible para una decisión cuando posea rol, autoridad, alcance y vigencia suficientes.
> **Depende de:** Ninguna
> **Modifica:** Ninguna
> **Reemplaza:** Ninguna

## Contexto

La base funcional `procure-to-pay-management-platform-v2.md` separa cinco conceptos que no deben confundirse: Department, Job Title, System Role, Process Responsibility y Approval Authority. También establece que Release 1 opera con una organización y una Legal Entity, y que una persona solo puede recibir una aprobación si combina un System Role compatible, autoridad suficiente, scope aplicable, vigencia y ausencia de conflictos de Segregation of Duties.

El backend actual es un scaffold .NET 10: valida JWT Bearer contra Keycloak, dispone de SQL Server mediante EF Core y todavía no contiene usuarios de negocio, estructura organizativa, roles, grants ni políticas de autorización. El realm de Keycloak tampoco define roles empresariales. Esta SPEC convierte la primera capacidad recomendada por la base funcional en un contrato implementable sin adelantar el workflow de aprobaciones ni el Policy Engine.

## Alcance

### Incluye

- Configuración de la única organización y Legal Entity de Release 1.
- Departments configurables y pertenencia de cada usuario activo a un único Department.
- Perfil empresarial local asociado de forma estable a la identidad autenticada por Keycloak.
- Alta Just-In-Time restringida de identidades desconocidas.
- Activación y desactivación de usuarios sin eliminar su historia.
- Catálogo cerrado de System Roles de Release 1 y sus asignaciones múltiples y con scope explícito por usuario.
- Catálogos configurables y versionados de niveles de autoridad y Approval Authority Grants con tipo, nivel, límite, scope y vigencia.
- Resolución determinista de candidatos elegibles y producción de evidencia reproducible, sin crear ni asignar Approval Tasks.
- Administración por usuarios con System Role `ADMIN`, con motivo y registro auditable de cada mutación.
- Bootstrap y recuperación administrativa seguros, no expuestos por HTTP, para la organización, su Legal Entity y el primer administrador.
- Contratos de autorización y errores para las operaciones de este alcance.

### No incluye

- Creación, secuencia, agrupación, reasignación o estado de Approval Requirements y Approval Tasks.
- Policy Engine, thresholds de compra, controles por línea o evaluación de políticas.
- Delegaciones, vacaciones, escalamiento o selección automática de un aprobador entre varios candidatos.
- Cost Centers, Cost Center Owners, budgets o movimientos presupuestarios.
- Purchase Requests, proveedores, sourcing, POs, fulfillment, invoices o payments.
- Process Responsibilities como Acceptance Owner o IT Provisioner asignado a una compra concreta.
- Gestión de credenciales, MFA, recuperación de contraseña o ciclo de vida interno de Keycloak.
- Sincronización SCIM, múltiples Identity Providers, múltiples organizaciones o múltiples Legal Entities.
- Roles personalizados, permisos editables o un diseñador genérico de RBAC.
- Audit Trail transversal de todos los dominios; esta SPEC solo exige evidencia administrativa compatible con su futura incorporación.
- Interfaz frontend.

## Comportamiento esperado

- **REQ-01 — Bootstrap y recuperación explícitos y seguros.** Un inicializador operativo, no expuesto por HTTP y ejecutado con acceso de despliegue antes de habilitar tráfico, debe crear atómicamente la única organización, la única Legal Entity, un Department inicial y el primer perfil activo con System Role `ADMIN` a partir de una identidad Keycloak identificada por `issuer + subject` y un motivo operativo explícito. Usa una transacción serializable y un marcador persistente de inicialización; es idempotente para la misma identidad y configuración y rechaza cualquier divergencia una vez completado. Nunca convierte al primer login en administrador. El sistema impide desactivar al último `ADMIN` activo o revocar su último assignment efectivo. Un procedimiento operativo break-glass, tampoco expuesto por HTTP, puede designar un reemplazo solo con acceso de despliegue, motivo e identidad explícitos; conserva al administrador anterior, registra el hecho con actor `SYSTEM` y no concede ninguna Approval Authority.

- **REQ-02 — Organización única.** Release 1 admite exactamente una organización y una Legal Entity. Sus códigos, moneda base y mes inicial fiscal quedan inmutables después del bootstrap; sus nombres y zona horaria pueden actualizarse por `ADMIN` con motivo. La moneda usa un código ISO 4217, la zona horaria un identificador IANA válido y el mes inicial del año fiscal un valor entre 1 y 12. Crear una segunda organización o Legal Entity debe rechazarse como conflicto. El cálculo y etiquetado de periodos fiscales se cerrará en la SPEC de budgets.

- **REQ-03 — Departments.** `ADMIN` puede crear, consultar, renombrar, activar y desactivar Departments. Cada Department tiene un código estable, único sin distinguir mayúsculas/minúsculas, y un nombre. El código no cambia después de su creación. Un Department no puede desactivarse mientras tenga usuarios activos ni role assignments o grants no revocados —incluidos los futuros— que lo referencien; el sistema debe indicar el conflicto sin desactivar parcialmente datos relacionados. Un Department inactivo puede reactivarse conservando su identificador e historia.

- **REQ-04 — Identidad local JIT restringida.** Cuando una petición protegida presenta por primera vez un JWT válido del issuer y audience configurados, el sistema crea como máximo un perfil local `PENDING_SETUP`, identificado por la pareja inmutable `issuer + subject`. Puede conservar email y nombre como atributos informativos procedentes del IdP, pero nunca usa email, nombre o Job Title como clave de identidad o fuente de autorización. El usuario pendiente solo puede consultar su estado de incorporación; toda capacidad empresarial devuelve `403`.

- **REQ-05 — Preparación, activación, desactivación y retorno.** `ADMIN` puede completar el Department, Job Title y atributos empresariales de un perfil pendiente, preparar roles o grants y activarlo. La activación exige un Department activo y un Job Title no vacío; no asigna `REQUESTER` ni ningún otro rol por defecto. Al desactivar un usuario, el sistema revoca atómicamente todos sus role assignments y grants que todavía no estén revocados, incluidos los de vigencia futura, y conserva sus referencias históricas. El retorno de la misma pareja `issuer + subject` reutiliza el perfil mediante `INACTIVE → PENDING_SETUP → ACTIVE`; roles y grants revocados no se restauran automáticamente y deben asignarse de nuevo. No se puede desactivar al último `ADMIN` efectivo. La posible reasignación de tareas pendientes pertenece a la SPEC de Approval Workflow.

- **REQ-06 — Catálogo cerrado y assignments con scope.** El catálogo de Release 1 contiene exactamente `REQUESTER`, `DEPARTMENT_APPROVER`, `FINANCE_APPROVER`, `PROCUREMENT_BUYER`, `PROCUREMENT_APPROVER`, `AP_SPECIALIST`, `PAYMENT_APPROVER`, `IT_REVIEWER`, `IT_PROVISIONER`, `LEGAL_REVIEWER`, `ADMIN` y `AUDITOR`. Un usuario puede tener varios roles y más de un assignment no solapado para el mismo rol cuando sus scopes difieren. Todo assignment tiene scope explícito. `ADMIN` solo admite scope `ORGANIZATION` y permite administrar toda la única organización. `IT_REVIEWER`, `LEGAL_REVIEWER` y `AUDITOR` se limitan por su assignment sin requerir un Authority Grant. En esta entrega, `AUDITOR` puede consultar en modo read-only configuración organizacional y evidencia administrativa cuyos scopes estén cubiertos por su assignment, con identidad y datos sensibles minimizados; los documentos, decisiones y movimientos de otros dominios se incorporarán en sus respectivas SPECs. `ADMIN` puede asignar o revocar roles con motivo y no puede dejar al sistema sin un `ADMIN` activo. `CFO`, `Procurement Manager`, `EMPLOYEE`, `FINANCE` y `RECEIVER` no son System Roles. Tener `ADMIN` no concede por sí mismo ninguna autoridad de aprobación, y `AUDITOR` nunca concede mutación.

- **REQ-07 — Niveles y grants de autoridad.** Los tipos iniciales de Approval Authority son `BUSINESS_NEED`, `FINANCIAL`, `PROCUREMENT`, `SUPPLIER_MASTER`, `MATCH_EXCEPTION` y `PAYMENT`. `ADMIN` puede configurar para cada tipo niveles ordenados y puede crear, revocar o consultar grants. Cada grant referencia un usuario, un tipo, una versión inmutable de nivel activa al otorgarlo, un límite monetario en moneda base cuando corresponda, un scope explícito, `valid_from` y un `valid_to` opcional. Cambiar orden o significado crea una nueva versión de nivel; no reinterpreta grants previos. Nivel y límite deben cumplirse simultáneamente; dos grants nunca se combinan para completar una sola exigencia. Un grant revocado, futuro, vencido o perteneciente a un usuario no activo no otorga autoridad.

- **REQ-08 — Scope explícito y extensible.** Un role assignment o grant declara expresamente si cubre toda la organización o un conjunto de referencias permitidas. En esta SPEC se resuelven scopes `ORGANIZATION`, `LEGAL_ENTITY` y `DEPARTMENT`; un scope vacío no equivale a alcance global. La dimensión `COST_CENTER` queda formalmente delegada a la SPEC de Cost Centers y no puede asignarse hasta que exista su catálogo; esa extensión debe conservar la semántica definida aquí antes de implementar Purchase Requests. Cuando una decisión requiera varias dimensiones, el mismo assignment y, si se exige authority, un único grant deben cubrirlas todas. Las referencias inexistentes o inactivas se rechazan al crear o modificar el assignment o grant.

- **REQ-09 — Resolución de elegibilidad y evidencia.** La capa de aplicación debe ofrecer una operación determinista que reciba System Role requerido, scope de decisión, instante de evaluación, usuarios excluidos por conflicto y el requisito de authority definido por la matriz de esta SPEC. Solo `IT_REVIEWER` y `LEGAL_REVIEWER` admiten `authority_requirement = NONE`; `DEPARTMENT_APPROVER`, `FINANCE_APPROVER`, `PROCUREMENT_APPROVER` y `PAYMENT_APPROVER` exigen uno de los tipos compatibles de la matriz, y cualquier otra combinación se rechaza como input inválido. Cuando existe authority se incluyen nivel mínimo e importe evaluado en moneda base si la decisión es monetaria. El resolver devuelve usuarios activos cuyo assignment vigente cubre el scope y, cuando corresponde, poseen un único grant vigente que satisface todo el requisito. Cada candidato incluye un `EligibilityEvidence` inmutable y serializable con identificadores y versiones del perfil, assignment, nivel y grant usados, scope, instante e inputs evaluados; esta SPEC lo devuelve pero no lo persiste ni crea una aprobación. Excluye siempre los identificadores indicados, no selecciona a una persona, no crea una Approval Task y no interpreta Job Title. Cero candidatos es un resultado válido que la SPEC de Approval Workflow convertirá en un requirement sin asignar.

- **REQ-10 — Administración auditable.** Solo un usuario activo con `ADMIN` de scope `ORGANIZATION` puede mutar organización, Departments, perfiles, role assignments, niveles o grants. Un `AUDITOR` activo puede leer la evidencia y configuración cubiertas por su scope, pero no mutarlas. Cada comando administrativo exige un motivo no vacío y escribe, en la misma operación atómica, actor, timestamp UTC, acción, tipo e identificador del objetivo, versión anterior y nueva, before/after pertinentes, scope afectado y correlation reference. Los comandos ordinarios realizan una mutación de agregado y generan un registro; bootstrap, recuperación y desactivación generan un único registro padre con una colección ordenada de subcambios, cada uno con objetivo y versiones, sin registros hijo separados ni evidencias contradictorias. No se permite editar ni borrar esos registros desde las operaciones de esta SPEC. Los valores sensibles o tokens no se guardan en la evidencia.

- **REQ-11 — Resultado de autenticación y autorización.** Una petición sin credenciales o con JWT inválido, issuer incorrecto o audience incorrecto recibe `401`. Un JWT válido cuyo perfil esté pendiente, desactivado o carezca del rol requerido recibe `403` en una operación empresarial. Un recurso inexistente recibe `404`; unicidad, concurrencia o transición incompatible reciben `409`; validación sintáctica o de campos recibe `400`; y otras invariantes de dominio reciben `422`. Los errores usan el contrato Problem Details ya habilitado por la API, que deberá distinguir esas categorías en vez de mapear todo `DomainException` a `422`, y no revelan si otra identidad posee roles o grants.

- **REQ-12 — Conservación histórica y reproducibilidad.** Perfiles, role assignments, grants, versiones de nivel y Departments que hayan sido referenciados no se eliminan físicamente mediante esta capacidad. Las correcciones se representan mediante una nueva versión, actualización auditable, revocación o desactivación. El resolver de REQ-09 devuelve un snapshot de evidencia autosuficiente; una futura decisión que lo persista podrá reproducir qué identidad, assignment, authority, nivel, límite, scope, vigencia, exclusiones e instante justificaron la elegibilidad aunque la configuración cambie después.

- **REQ-13 — Separación entre autenticación y autorización.** Keycloak es la autoridad para autenticar la identidad y emitir el JWT; el backend y SQL Server son la fuente de verdad para Department, Job Title, estado empresarial, System Roles y Approval Authority. Los claims de roles o authorities que pudiera contener un token no conceden capacidades empresariales. Las credenciales y sesiones permanecen fuera del backend.

## Datos y contratos

| Elemento | Datos mínimos | Regla o compatibilidad |
| --- | --- | --- |
| `Organization` | código, nombre, moneda base, zona horaria IANA, mes inicial fiscal | Singleton de Release 1; código, moneda y mes inicial fiscal inmutables tras bootstrap. |
| `LegalEntity` | identificador, código, nombre, organización | Exactamente una; su código es estable y único. |
| `Department` | identificador, código, nombre, estado | Código único sin distinguir mayúsculas/minúsculas; no se elimina físicamente. |
| `UserProfile` | identificador, issuer, subject, email informativo, nombre informativo, Department, Job Title, estado, versión | `issuer + subject` es único e inmutable; estados `PENDING_SETUP`, `ACTIVE`, `INACTIVE`; cada cambio incrementa la versión; email no enlaza cuentas. |
| `SystemRole` | código | Catálogo cerrado definido por REQ-06. |
| `RoleAssignment` | usuario, rol, scope, estado, actor, motivo, fechas, versión | No se permiten assignments activos solapados para `usuario + rol`; la revocación conserva historia. |
| `AuthorityLevel` | tipo, código, orden, estado, versión | Orden total dentro de cada tipo; una versión referenciada es inmutable y no se borra. |
| `ApprovalAuthorityGrant` | usuario, tipo, versión de nivel, límite base, moneda base, scope, vigencia, estado, versión | Un grant satisface de forma individual nivel, importe, scope y vigencia; revocación lógica. |
| `AuthorizationScope` | assignment o grant, dimensión, referencia o indicador global | Scope global siempre explícito; `COST_CENTER` no se admite hasta su SPEC. |
| `EligibilityEvidence` | inputs, usuario, versiones de perfil/assignment/grant/nivel, scope e instante | Snapshot inmutable devuelto por candidato; no equivale a una aprobación. |
| `AdministrativeAuditRecord` | actor type/id, instante UTC, acción, objetivo, versiones, scope, before/after o subcambios, motivo, correlation reference | Append-only desde esta capacidad y atómico con la mutación. |

Matriz admitida por el resolver de elegibilidad:

| System Role requerido | Authority Requirement | Regla |
| --- | --- | --- |
| `DEPARTMENT_APPROVER` | `BUSINESS_NEED` | Obligatorio. |
| `FINANCE_APPROVER` | `FINANCIAL` o `MATCH_EXCEPTION` | Obligatorio; el tipo depende de la decisión solicitada. |
| `PROCUREMENT_APPROVER` | `PROCUREMENT`, `SUPPLIER_MASTER` o `MATCH_EXCEPTION` | Obligatorio; el tipo depende de la decisión solicitada. |
| `PAYMENT_APPROVER` | `PAYMENT` | Obligatorio. |
| `IT_REVIEWER` | `NONE` | Elegibilidad por role assignment y scope. |
| `LEGAL_REVIEWER` | `NONE` | Elegibilidad por role assignment y scope. |

Cualquier rol o combinación no incluida se rechaza; en particular, `AP_SPECIALIST` no puede resolver como aprobador de `MATCH_EXCEPTION`.

Taxonomía observable de errores:

| Problem type | HTTP | Uso |
| --- | ---: | --- |
| `/problems/authentication-required` | 401 | Token ausente o no válido; lo emite el middleware JWT. |
| `/problems/forbidden` | 403 | Identidad válida sin estado, rol o scope suficiente. |
| `/problems/not-found` | 404 | Recurso no encontrado dentro del scope visible. |
| `/problems/validation` | 400 | Forma o campos de entrada inválidos, incluida una combinación no admitida del resolver. |
| `/problems/conflict` | 409 | Unicidad, versión obsoleta, último admin o transición incompatible. |
| `/problems/domain-rule-violation` | 422 | Otra invariante empresarial bien formada que no sea conflicto. |

Reglas adicionales del contrato:

- `valid_from` es inclusivo y `valid_to`, cuando existe, es exclusivo; ambos representan instantes UTC y `valid_to` debe ser posterior a `valid_from`.
- Los importes se expresan en minor units o un tipo decimal sin pérdida y siempre conservan la moneda base asociada; no se aceptan límites negativos.
- Un requisito sin importe no exige límite monetario. Cuando sí existe importe, el grant debe declarar un límite que lo cubra; un valor nulo no significa ilimitado.
- El backend puede actualizar la copia informativa de nombre o email al observar claims válidos del mismo `issuer + subject`, pero ese cambio no modifica permisos ni sustituye datos empresariales administrados.
- Las operaciones de escritura deben aplicar control de concurrencia. Una versión obsoleta no puede sobrescribir una modificación administrativa posterior y produce `409`.
- Desactivar un usuario revoca dentro de la misma transacción todos sus assignments y grants no revocados, incluso los de inicio futuro. Volverlo a `PENDING_SETUP` no crea ni reactiva privilegios.
- Los permisos efectivos de un role assignment se obtienen por intersección con el scope de un grant cuando una decisión exige ambos; un grant más amplio nunca amplía el assignment.
- Un intento de asignar scope `COST_CENTER` antes de que su SPEC habilite y valide ese catálogo produce `/problems/validation` y no persiste cambios.
- La API no queda obligada por esta SPEC a nombres concretos de rutas, pero debe exponer operaciones versionadas para: consultar el estado propio; administrar organización, Departments, usuarios, roles, niveles y grants; y consultar configuración/evidencia como `ADMIN` o como `AUDITOR` limitado por scope.

## Migración, despliegue y reversión

- El repositorio no contiene datos de dominio ni migraciones previas; la implementación agrega de forma aditiva la persistencia de esta SPEC bajo un schema de módulo explícito, respetando la convención declarada en `ProcureToPayDbContext`.
- El despliegue aplica primero la migración compatible, ejecuta el inicializador con acceso operativo restringido y solo después publica la API. El bootstrap no tiene endpoint HTTP y debe poder repetirse sin duplicar organización, Legal Entity, Department, usuario o rol.
- La aplicación no queda operativa para capacidades empresariales si el bootstrap está ausente o es inconsistente; health/diagnóstico debe distinguir esa condición sin exponer issuer o subject configurados.
- Se documenta y prueba un comando break-glass separado. Requiere acceso de despliegue, identidad de reemplazo y motivo; no funciona como endpoint, no elimina al administrador anterior, no concede grants y siempre crea evidencia. Su ejecución fuera de una recuperación autorizada se considera incidente operativo.
- Una reversión de aplicación conserva tablas y datos. No se autoriza una migración descendente que elimine perfiles, grants o evidencia administrativa en un entorno con datos; la recuperación consiste en volver a la versión anterior compatible y corregir la configuración o migración hacia adelante.
- Los permisos concedidos por esta SPEC no se activan desde claims de Keycloak, por lo que el despliegue no requiere introducir realm roles empresariales.

## Seguridad y privacidad

- Los contratos de mutación administrativa requieren autenticación JWT válida y un assignment local `ADMIN` de scope `ORGANIZATION`. La lectura de configuración y evidencia admite también `AUDITOR` activo y queda filtrada por su scope. Solo la consulta del estado propio está disponible para una identidad pendiente.
- Se valida issuer, audience, firma y vigencia del token mediante la configuración JWT existente. Nunca se persiste el token, credenciales, refresh tokens ni secretos de Keycloak.
- El subject no se expone en listados generales salvo necesidad administrativa; logs y telemetría usan identificadores internos o valores seudonimizados.
- Un email modificado o reutilizado no enlaza identidades ni transfiere permisos.
- `ADMIN` administra roles y grants, pero no se convierte por ello en aprobador y sigue sujeto a las exclusiones de Segregation of Duties que reciba el resolver.
- El sistema impide eliminar el último acceso administrativo efectivo; el mecanismo break-glass se mantiene fuera del plano HTTP normal y no bypassa aprobaciones transaccionales.
- Una desactivación o revocación debe impedir nuevas autorizaciones y nuevas selecciones de elegibilidad después de que la operación administrativa confirme éxito.
- La evidencia before/after debe omitir tokens y secretos y limitar los datos personales a los necesarios para explicar el cambio.
- El bootstrap solo acepta la identidad configurada explícitamente; se prohíbe la política “primer login es administrador”.

## Requisitos no funcionales

- **NFR-01 — Idempotencia y concurrencia de identidad.** Peticiones concurrentes con el mismo `issuer + subject` producen un único `UserProfile`; ninguna respuesta puede observar dos identidades locales para el mismo sujeto.
- **NFR-02 — Atomicidad administrativa.** Una mutación de configuración y su `AdministrativeAuditRecord` se confirman o revierten juntas; no existe un cambio exitoso sin evidencia ni evidencia de un cambio fallido.
- **NFR-03 — Consistencia de autorización.** Tras confirmar una activación, desactivación, asignación, revocación o cambio de grant, la siguiente decisión de autorización o elegibilidad usa el estado confirmado. Cualquier caché debe invalidarse como parte de la operación y no puede prolongar privilegios revocados.
- **NFR-04 — Tiempo y moneda deterministas.** Persistencia y comparaciones de vigencia usan UTC; presentación usa la zona organizacional. Comparaciones monetarias no usan coma flotante binaria y respetan minor units de la moneda base.
- **NFR-05 — Observabilidad sin credenciales.** Bootstrap, altas JIT, denegaciones administrativas y conflictos de concurrencia emiten logs estructurados y trazas correlacionables mediante OpenTelemetry, sin tokens, subjects completos ni datos personales innecesarios.

## Estrategia de pruebas

| Nivel | Cobertura prevista | Evidencia o comando conocido |
| --- | --- | --- |
| Unitaria | Estados de usuario, scope de roles, niveles/versiones, vigencia, límites, scopes, no combinación de grants, elegibilidad IT/Legal y exclusiones SoD | `dotnet test ProcureToPay.sln` con casos en `tests/ProcureToPay.UnitTests/` |
| Integración | Unicidad e idempotencia sobre SQL Server, migraciones, concurrencia, atomicidad cambio-auditoría, snapshots y resolver de elegibilidad | `dotnet test ProcureToPay.sln` con Testcontainers en `tests/ProcureToPay.IntegrationTests/` |
| API/E2E | JWT válido/inválido, JIT pendiente, autorización `ADMIN`, último administrador, Problem Details, activación y revocación efectiva | `dotnet test ProcureToPay.sln` con escenarios en `tests/ProcureToPay.ApiE2ETests/`; el preflight de implementación debe incorporar un emisor de pruebas o Keycloak aislado sin credenciales reales |
| Migración/operación | Aplicación limpia, bootstrap no HTTP, recuperación break-glass y reversión de aplicación conservando datos | Prueba automatizada de migración sobre SQL Server efímero y procedimiento operativo documentado |

Todos los criterios de aceptación se verifican automáticamente. Las pruebas negativas deben cubrir ausencia de rol, grants vencidos o fuera de scope, identidad pendiente/inactiva, issuer o audience incorrectos, doble creación concurrente y actualización con versión obsoleta.

## Decisiones

- **DEC-01 — Primera SPEC unificada pero acotada.** Organización, identidad, roles y Approval Authority forman una única base verificable. Approval Workflow, Policy Engine y dominios transaccionales quedan en SPECs posteriores para evitar mezclar elegibilidad con orquestación.
- **DEC-02 — Autenticación en Keycloak y autorización en el backend.** El JWT identifica; SQL Server decide permisos empresariales. Se descarta modelar grants con scope y vigencia en claims porque quedarían acoplados al IdP y podrían permanecer obsoletos durante la vida del token.
- **DEC-03 — Alta JIT restringida.** Una identidad válida desconocida se registra `PENDING_SETUP`, sin roles ni acceso empresarial. Se descarta activarla automáticamente y también se evita exigir que un operador copie manualmente el subject antes del primer login.
- **DEC-04 — Administración directa con auditoría.** `ADMIN` asigna o revoca roles y grants con motivo y evidencia atómica. Se descarta exigir un meta-workflow de doble aprobación en Release 1 porque introduciría Approval Workflow antes de contar con su SPEC; esto no convierte a `ADMIN` en autoridad empresarial.
- **DEC-05 — Bootstrap determinista.** La primera organización y el primer administrador nacen de configuración explícita e idempotente. Se descarta elevar al primer login por ser inseguro y no determinista.
- **DEC-06 — Catálogo de roles fijo y scoped assignments.** Los roles funcionales cerrados en la base son datos de referencia del sistema; Release 1 no permite crear roles o permisos arbitrarios. Job Title permanece informativo y nunca participa en autorización. El scope del assignment permite modelar IT, Legal y Auditor sin inventar authorities para ellos.
- **DEC-07 — Grants no aditivos.** Un único grant debe cubrir todos los requisitos de una decisión. Sumar límites, niveles o scopes de grants diferentes haría ambigua la responsabilidad y la evidencia de quién tenía autoridad.
- **DEC-08 — Scope global explícito.** La ausencia de scopes no otorga acceso global. Se usa una declaración explícita para evitar ampliaciones accidentales de autoridad; `COST_CENTER` se habilitará únicamente cuando exista el catálogo de la SPEC correspondiente.
- **DEC-09 — Sin borrado físico y retorno sin privilegios.** Desactivación y revocación preservan la historia. Un usuario que retorna pasa de nuevo por setup y no recupera roles o grants automáticamente. La futura SPEC de Audit Trail podrá incorporar estos registros sin reconstruir hechos perdidos.
- **DEC-10 — Matriz cerrada de elegibilidad.** Toda elegibilidad exige rol y scope. Solo IT y Legal omiten Approval Authority; Department, Finance, Procurement y Payment exigen un tipo compatible según la matriz contractual. Esto evita que un caller omita accidentalmente un grant obligatorio.
- **DEC-11 — Snapshot de elegibilidad no persistido aquí.** El resolver devuelve evidencia versionada y serializable, pero esta SPEC no la persiste ni crea una aprobación. El workflow posterior conservará el snapshot al tomar una decisión, evitando reinterpretarla después de cambios administrativos.
- **DEC-12 — Último administrador protegido.** El plano normal no puede dejar al sistema sin administración. Bootstrap y recuperación se ejecutan fuera de HTTP con acceso operativo explícito y evidencia, sin otorgar autoridad empresarial.

## Plan de implementación

### Bloque 1 — Modelo organizacional y autorización

- **T-01 — Invariantes de dominio.** Implementar agregados, estados y reglas para organización singleton, Legal Entity, Departments, perfiles, catálogo de roles, niveles, grants, scopes, vigencia y revocación. Cubre: REQ-02, REQ-03, REQ-05, REQ-06, REQ-07, REQ-08, REQ-12, REQ-13, NFR-04, CA-03, CA-04, CA-05, CA-06, CA-07, CA-10.
- **T-02 — Resolver de elegibilidad.** Implementar la matriz cerrada, role scope, authority obligatoria u omitida solo para IT/Legal, snapshots serializables, vigencia y exclusiones sin seleccionar ni crear tareas. Cubre: REQ-07, REQ-08, REQ-09, REQ-12, REQ-13, NFR-03, NFR-04, CA-05, CA-08.

**Resultado verificable:** las reglas puras distinguen identidad, Job Title, rol y authority y resuelven correctamente límites, scopes, fechas y exclusiones.

### Bloque 2 — Persistencia, migración y bootstrap

- **T-03 — Persistencia y concurrencia.** Mapear el módulo a SQL Server con schema explícito, índices únicos, control de concurrencia, revocación lógica y migración aditiva. Cubre: REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, REQ-10, REQ-12, NFR-01, NFR-02, NFR-03, NFR-04, CA-01, CA-02, CA-05, CA-06, CA-07, CA-09, CA-10.
- **T-04 — Bootstrap y recuperación seguros.** Implementar inicialización atómica, marcador persistente, protección del último admin, comando break-glass y diagnóstico operativo, sin endpoints HTTP ni elevación del primer login. Cubre: REQ-01, REQ-02, REQ-10, REQ-13, NFR-02, NFR-05, CA-01, CA-07, CA-09.

**Resultado verificable:** una base vacía se inicializa una vez, una reejecución idéntica no duplica datos y una configuración divergente falla sin cambios parciales.

### Bloque 3 — Identidad autenticada y contratos administrativos

- **T-05 — Enlace JWT y alta JIT.** Integrar la identidad autenticada con `UserProfile`, consulta de estado propio y denegación de perfiles pendientes o inactivos. Cubre: REQ-04, REQ-05, REQ-11, REQ-13, NFR-01, NFR-03, NFR-05, CA-02, CA-03, CA-10, CA-11.
- **T-06 — Administración y lectura protegidas.** Exponer mutaciones versionadas para `ADMIN`, lecturas filtradas para `AUDITOR`, taxonomía Problem Details, instrumentación estructurada y auditoría atómica con versiones y subcambios. Cubre: REQ-02, REQ-03, REQ-05, REQ-06, REQ-07, REQ-08, REQ-10, REQ-11, REQ-12, REQ-13, NFR-02, NFR-03, NFR-04, NFR-05, CA-03, CA-04, CA-05, CA-06, CA-07, CA-09, CA-10, CA-11.

**Resultado verificable:** solo un `ADMIN` activo puede administrar la base empresarial y cada cambio observable conserva evidencia sin conceder autoridad automática.

### Bloque 4 — Verificación y operación

- **T-07 — Pruebas de contrato y regresión.** Implementar pruebas unitarias, integración, API/E2E, migración, concurrencia y seguridad descritas en la estrategia, incluyendo todos los casos negativos. Cubre: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, REQ-13, NFR-01, NFR-02, NFR-03, NFR-04, NFR-05, CA-01, CA-02, CA-03, CA-04, CA-05, CA-06, CA-07, CA-08, CA-09, CA-10, CA-11.
- **T-08 — Documentación operativa.** Documentar datos de bootstrap sin secretos, orden de despliegue, diagnóstico, recuperación, límites frente a Keycloak y frontera con la siguiente SPEC. Cubre: REQ-01, REQ-02, REQ-11, REQ-13, NFR-05, CA-01, CA-07, CA-11.

**Resultado verificable:** `dotnet test ProcureToPay.sln` demuestra el contrato y un operador puede inicializar o recuperar el módulo sin elevar identidades arbitrarias ni perder datos.

## Criterios de aceptación

| ID | Cubre requisitos | Criterio | Verificación prevista |
| --- | --- | --- | --- |
| CA-01 | REQ-01, REQ-02, REQ-10, NFR-02 | Sin endpoint HTTP de bootstrap, una base vacía crea exactamente una organización, una Legal Entity, un Department y el `ADMIN` configurado; repetirlo es inocuo, una configuración divergente falla sin datos parciales, el último admin no puede desactivarse y break-glass crea solo un reemplazo `ADMIN` auditado sin grants. | Automática: integración sobre SQL Server efímero, inspección de rutas y pruebas negativas de divergencia, último admin y recuperación. |
| CA-02 | REQ-04, REQ-11, REQ-13, NFR-01 | Dos peticiones concurrentes con el mismo JWT válido desconocido producen un solo perfil `PENDING_SETUP`; puede consultar su estado, recibe `403` en operaciones empresariales y nunca obtiene roles por claims o defaults. | Automática: integración concurrente y API/E2E con JWT controlado. |
| CA-03 | REQ-05, REQ-10, REQ-11 | Un `ADMIN` solo puede activar un perfil con Department activo y Job Title; la activación sin roles es válida, no agrega `REQUESTER` y una entrada incompleta devuelve `400` sin cambio ni audit de éxito. | Automática: pruebas de dominio, integración y contrato HTTP. |
| CA-04 | REQ-06, REQ-10, REQ-13, NFR-03 | `ADMIN` solo funciona con scope organizacional; un `AUDITOR` lee configuración y evidencia dentro de su scope con campos sensibles minimizados y toda mutación devuelve `403`. Un rol desconocido o assignment solapado se rechaza, Job Title no concede permisos y una revocación deja de autorizar la siguiente petición. | Automática: pruebas unitarias de catálogo/scope y API/E2E positivas/negativas para ADMIN, AUDITOR y revocación. |
| CA-05 | REQ-07, REQ-08, REQ-09, REQ-12, NFR-04 | La matriz rechaza omitir authority para Department, Finance, Procurement o Payment y rechaza exigirla para IT/Legal; un candidato válido debe cubrir assignment y, cuando aplica, un mismo grant vigente con tipo, nivel, límite y scope. Grants parciales, vencidos, futuros, revocados o sumados no califican; `COST_CENTER` se rechaza; y la evidencia devuelta conserva las versiones evaluadas sin persistirse. | Automática: matriz unitaria de roles, tipos, límites, fechas y scopes, más integración que serializa el snapshot devuelto. |
| CA-06 | REQ-03, REQ-05, REQ-08 | Los códigos de Department no se duplican por mayúsculas; un Department con usuarios, assignments o grants vigentes no se desactiva; una referencia inexistente o inactiva se rechaza y una reactivación conserva identificador e historia. | Automática: integración de índices, FKs/reglas, transiciones y respuestas `409`/`400`. |
| CA-07 | REQ-02, REQ-11, NFR-04 | La API rechaza una segunda organización o Legal Entity, monedas no ISO 4217, zonas no IANA y meses fiscales fuera de 1–12; permite cambiar nombre/zona con audit y no permite cambiar códigos, moneda base ni mes fiscal. | Automática: integración y contrato HTTP con Problem Details. |
| CA-08 | REQ-09, REQ-12, REQ-13, NFR-03 | El resolver devuelve todos y solo los candidatos activos que cumplen la matriz, elimina usuarios excluidos y entrega un snapshot serializable con versión de perfil y configuración por candidato; cambiar después perfil, nivel o grant no altera la evidencia ya capturada. Si no queda ninguno devuelve conjunto vacío sin crear tarea ni elegir sustituto. | Automática: pruebas unitarias e integración con múltiples candidatos, serialización y cambio posterior de configuración. |
| CA-09 | REQ-10, REQ-12, NFR-02 | Cada comando ordinario confirmado tiene evidencia con versiones y scope; cada comando compuesto tiene un registro padre y una colección ordenada de subcambios. Al forzar un fallo no se confirma cambio ni evidencia, y ninguna operación permite editarla o borrarla. | Automática: integración transaccional y pruebas negativas de API. |
| CA-10 | REQ-05, REQ-07, REQ-12, NFR-03 | Desactivar un usuario revoca atómicamente todos los assignments y grants no revocados, incluidos los futuros; su retorno reutiliza el perfil pasando por `PENDING_SETUP` y no restaura privilegios ni crea otros nuevos. | Automática: API/E2E de grants presentes/futuros, desactivación/retorno y conservación histórica. |
| CA-11 | REQ-04, REQ-10, REQ-11, REQ-13, NFR-05 | JWT ausente o inválido produce `401`; identidad válida sin permiso produce `403`; validación, inexistencia, conflicto y otra regla de dominio producen respectivamente `400`, `404`, `409` y `422` con los problem types definidos, sin tokens, subject completo ni datos de terceros en respuesta, logs o trazas. | Automática: pruebas de contrato HTTP para cada categoría y captura de telemetría de prueba. |

## Riesgos

| Riesgo | Señal | Mitigación |
| --- | --- | --- |
| Confundir claims del IdP con permisos empresariales | Una identidad conserva acceso tras revocar su asignación local o un realm role habilita una operación | Centralizar autorización en el backend y cubrir claims engañosos en CA-02 y CA-04. |
| Escalada durante bootstrap | El primer usuario no configurado obtiene `ADMIN` o una reejecución crea otro administrador | Identidad explícita, operación idempotente, conflicto ante divergencia y evidencia `SYSTEM`. |
| Authority demasiado amplia por scope vacío | Un grant sin referencias califica para cualquier Department | Scope global explícito y rechazo de scopes vacíos o referencias inválidas. |
| Privilegio obsoleto por caché o concurrencia | Una revocación confirmada no afecta la siguiente decisión | Invalidación atómica, control de concurrencia y pruebas de NFR-03. |
| Acoplar esta SPEC al workflow futuro | El módulo crea Approval Tasks o elige un aprobador | Mantener REQ-09 como consulta de candidatos y verificar conjunto vacío sin efectos laterales. |
| Evidencia histórica reinterpretada | Cambiar orden de nivel o grant altera la explicación de una decisión pasada | Versionar configuración y devolver `EligibilityEvidence` autosuficiente según CA-05 y CA-08. |
| Pérdida de administración | Se revoca o desactiva el último `ADMIN`, o se expone recuperación por HTTP | Proteger el último admin y probar bootstrap/break-glass operativo en CA-01. |
| Auditoría con datos personales excesivos | Logs o before/after contienen token, subject completo o atributos innecesarios | Allowlist de campos auditables, seudonimización en telemetría y pruebas de CA-11. |
