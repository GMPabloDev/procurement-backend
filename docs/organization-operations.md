# Operación del módulo Organization

## Orden de despliegue

1. Configurar `ConnectionStrings:SqlServer` y el bloque `Authentication:JwtBearer` (`Authority`, `Audience` y `RequireHttpsMetadata`).
2. Aplicar las migraciones EF Core antes de publicar la API:
   `dotnet ef database update --project src/ProcureToPay.Infrastructure --startup-project src/ProcureToPay.Api`.
3. Ejecutar `OrganizationBootstrapper` desde el proceso operativo restringido, nunca mediante HTTP. El bootstrap es idempotente y rechaza una configuración divergente.
4. Publicar la API solo después de que exista el marcador persistente de bootstrap.

El bootstrap recibe código/nombre de organización, moneda ISO, zona horaria IANA, mes fiscal, Legal Entity, Department inicial y la identidad administrativa inicial. Los secretos no forman parte de los parámetros ni de los registros de auditoría.

## Identidad y autorización

Keycloak valida el JWT. El backend usa únicamente `iss` + `sub` como identidad estable y consulta la autorización local en SQL Server. El primer JWT válido desconocido crea un perfil `PENDING_SETUP`; no recibe rol por claims ni por defecto. `GET /api/v1/me` permite consultar su propio estado, pero un perfil pendiente o inactivo recibe `403` en operaciones empresariales.

Las rutas administrativas versionadas principales son:

- `GET /api/v1/me` y `GET /api/v1/organization`.
- `GET/POST /api/v1/departments`.
- `GET /api/v1/users`, setup, activate, deactivate y return-to-setup.
- Asignación/revocación de roles en `/api/v1/users/{id}/roles`.
- Creación de niveles y grants en `/api/v1/authority-levels` y `/api/v1/users/{id}/grants`.

Solo un perfil activo con `ADMIN` local y scope organizacional puede mutar. Un `AUDITOR` solo puede leer configuración habilitada por su assignment. Los claims de rol del IdP no sustituyen esta comprobación.

## Concurrencia, auditoría y recuperación

Las mutaciones usan `Version` y tokens `rowversion`; una versión obsoleta produce `409`. Toda mutación administrativa exige `Reason` y crea su evidencia atómica en `Organization.AdministrativeAuditRecords`. Los registros son append-only desde esta capacidad y no se exponen para edición o borrado.

Desactivar un usuario no elimina datos: revoca assignments y grants activos, incluidos los futuros, en la misma transacción. El último `ADMIN` activo no puede desactivarse ni revocarse. El retorno a setup no restaura privilegios automáticamente.

La recuperación break-glass de administrador se ejecuta fuera de HTTP con acceso operativo restringido, exige razón explícita y deja un único registro de auditoría. No concede grants de aprobación.

## Problem Details

- `400`: validación sintáctica o de campos.
- `401`: JWT ausente o inválido (issuer/audience incorrectos).
- `403`: perfil pendiente/inactivo o rol/scope insuficiente.
- `404`: recurso inexistente.
- `409`: unicidad, concurrencia o transición incompatible.
- `422`: regla de dominio restante.

Los errores no contienen tokens, el subject completo ni datos de terceros. OpenTelemetry conserva el `traceId` de la petición y la aplicación usa logging estructurado; la auditoría mantiene únicamente los campos operativos permitidos.

## Frontera con la siguiente SPEC

Esta capacidad no crea Approval Tasks, no evalúa políticas ni resuelve delegaciones. La siguiente SPEC debe consumir roles, scopes, grants y `EligibilityEvidence` para implementar Approval Workflow y Policy Engine.
