# Procure-to-Pay Management Platform

## Documento funcional consolidado — Fuente de verdad previa a SPECs técnicas

> **Versión:** 2.0
> **Estado:** base funcional cerrada para iniciar SPECs funcionales
> **Propósito:** servir como fuente normativa de contexto para continuar el diseño mediante SDD antes de entrar en arquitectura, base de datos, endpoints, infraestructura o implementación.
> **Nombre definitivo del proyecto:** **Procure-to-Pay Management Platform**

---

## 0. Contrato funcional del documento

### 0.1 Autoridad y precedencia

Este documento contiene reglas funcionales, decisiones cerradas, ejemplos y extensiones futuras.

Cuando exista una aparente contradicción, se aplicará el siguiente orden de precedencia:

```text
1. Alcance de Release 1
2. Invariantes funcionales
3. Decisiones marcadas como cerradas
4. Reglas específicas de cada dominio
5. Ejemplos ilustrativos
```

Los ejemplos explican una regla, pero no crean reglas nuevas. Importes, nombres, fechas, proveedores y personas incluidos en ejemplos se consideran:

```text
ILLUSTRATIVE — NOT A DEFAULT CONFIGURATION
```

Las SPECs posteriores pueden detallar estas reglas, pero no contradecirlas. Si una SPEC necesita cambiar una decisión cerrada, primero deberá actualizarse explícitamente esta fuente de verdad.

---

### 0.2 Significado de los estados de decisión

```text
CLOSED_IN_FOUNDATION
La decisión ya está resuelta en este documento.

DEFERRED_TO_FEATURE_SPEC
La base define el principio obligatorio y la SPEC concreta cerrará el detalle.

OUT_OF_RELEASE_1
La capacidad se excluye expresamente de la primera versión.

OPEN
Todavía requiere una decisión funcional antes de implementar la funcionalidad afectada.
```

Una base funcional se considera lista para SPECs cuando no contiene decisiones estructurales en estado `OPEN`.

---

### 0.3 Alcance normativo de Release 1

Release 1 asume:

- una organización compradora;
- una sola Legal Entity;
- moneda presupuestaria base configurable, inicialmente `PEN`;
- zona horaria organizacional configurable;
- calendario fiscal configurable;
- Purchase Requests multilínea;
- políticas, aprobaciones y presupuesto evaluados con granularidad de línea y agregaciones de control cuando corresponda;
- sourcing, RFQ, selección, PO, fulfillment, Supplier Invoice, matching, preparación de pago y registro del resultado del pago;
- proveedores y respuestas de cotización registrados por usuarios internos;
- adquisición, activación y registro inicial de suscripciones;
- referencias mínimas a acuerdos comerciales gestionados externamente.

Release 1 no incluye:

- múltiples organizaciones o múltiples Legal Entities;
- integración bancaria o movimiento real de fondos;
- Supplier Portal;
- employee reimbursements;
- corporate cards;
- emergency purchases que omitan el flujo normal;
- devoluciones o credit notes posteriores al pago;
- renovación automática de suscripciones;
- Contract Lifecycle Management;
- SUNAT, contabilidad general o validación tributaria completa;
- Warehouse, Inventory o Project Management.

La plataforma prepara, aprueba, programa y registra pagos, pero el movimiento real de fondos ocurre fuera del sistema. Para cerrar un pago se registra como mínimo su fecha, importe, moneda, referencia externa y resultado.

---

### 0.4 Unidad funcional y cardinalidades cerradas

```text
Purchase Request
= contenedor de la necesidad y sus líneas

Purchase Request Line
= unidad principal de política, presupuesto, aprobación,
  sourcing, trazabilidad y fulfillment
```

Cada Purchase Request Line tendrá un único:

- Purchase Type;
- Spend Category;
- Cost Center;
- Fiscal Year;
- Beneficiary Department;
- importe estimado y moneda;
- Requested For;
- conjunto de respuestas de riesgo;
- Preferred Product o Required Product cuando corresponda.

El Cost Center pertenece a un Department. La aprobación departamental de una línea se enruta al Department propietario del Cost Center imputado, no necesariamente al Department del requester.

Cardinalidades de Release 1:

- una Purchase Request contiene una o más líneas;
- una Purchase Request puede producir varias POs;
- varias líneas compatibles de una misma Purchase Request pueden agruparse en una PO;
- una línea no puede dividirse entre varios proveedores;
- una Purchase Request Line tiene como máximo una PO Line activa en Release 1;
- una PO no mezcla líneas de diferentes Purchase Requests;
- una PO pertenece a un solo proveedor, moneda y Legal Entity;
- cada PO Line referencia una Purchase Request Line de origen;
- una PO Line puede recibir múltiples receipts/acceptances e invoice lines parciales;
- una Supplier Invoice pertenece a un solo proveedor y moneda;
- una Supplier Invoice puede relacionarse con una PO;
- cada Invoice Line referencia una PO Line o una Direct Purchase Line;
- una factura sin PO solo es válida dentro de un Direct Purchase autorizado;
- una factura puede recibir múltiples Payment Allocations parciales;
- un Payment puede agrupar facturas del mismo proveedor, moneda y Legal Entity.

Para agrupar líneas en una PO deben compartir proveedor, moneda, Legal Entity y condiciones comerciales compatibles.

Una Supplier Invoice asociada a PO debe usar la misma moneda de la PO en Release 1. Si el proveedor factura en otra moneda, se solicita corrección o un PO Amendment y la reevaluación correspondiente. Una Direct Purchase utiliza la moneda de su documento real y vuelve a evaluar el equivalente base antes de habilitar el pago.

El estado de la Purchase Request es agregado. Puede ser `PARTIALLY_APPROVED`, `PARTIALLY_ORDERED` o `PARTIALLY_FULFILLED` cuando sus líneas se encuentren en estados diferentes.

Una Approval Task puede agrupar varias líneas únicamente cuando comparten requirement, approver elegible y decisión. La tarea conserva explícitamente qué líneas cubre. Un cambio en una línea invalida solo las tareas y evaluaciones cuyo scope la incluya.

---

### 0.5 Invariantes funcionales

```text
INV-SOD-001
Nadie puede aprobar una solicitud, excepción o pago propio cuando la política exige segregación.

INV-SOD-002
Quien crea o modifica un proveedor no puede aprobar su activación ni el mismo cambio sensible de Supplier Master.

INV-PR-001
Ninguna línea puede entrar en Procurement sin completar los controles previos exigidos por su evaluación de políticas.

INV-BUD-001
Ningún importe se reserva, compromete, consume o libera sin un movimiento presupuestario auditable.

INV-BUD-002
La disponibilidad se verifica de forma atómica en el momento de reservar o incrementar una obligación.

INV-BUD-003
Una excepción de matching no autoriza por sí sola un exceso presupuestario; cualquier importe superior al compromiso requiere fondos suficientes, validación y autorización financiera adicional.

INV-BUD-004
`AVAILABLE` no puede quedar negativo en Release 1. Si no existen fondos suficientes, la línea se bloquea hasta reducir/cancelar el importe o disponer de una posición presupuestaria válida; no existe bypass de presupuesto.

INV-PO-001
Ninguna PO puede emitirse sin una fuente aprobada o una excepción expresamente autorizada.

INV-PO-002
Una PO emitida no se sobrescribe; los cambios materiales se realizan mediante una nueva versión o amendment.

INV-FUL-001
Un receipt o acceptance publicado no se edita para alterar hechos históricos; se corrige mediante reverso o documento posterior.

INV-INV-001
AP no modifica PO, Goods Receipt o Service Acceptance para forzar un match.

INV-PAY-001
Ninguna factura con excepción abierta queda habilitada para pago salvo corrección o excepción autorizada.

INV-PAY-002
La persona que prepara un pago no puede aprobar el mismo pago.

INV-AUD-001
Toda aprobación, excepción, cambio de estado, modificación sensible y movimiento monetario conserva actor, fecha, motivo y documento relacionado.
```

---

### 0.6 Ciclos de vida normativos de alto nivel

Las SPECs podrán introducir estados internos auxiliares, pero no eliminar ni reinterpretar estos estados funcionales principales.

```text
Purchase Request
DRAFT → SUBMITTED → IN_APPROVAL → APPROVED
      ↘ CHANGES_REQUESTED
      ↘ PARTIALLY_APPROVED
      ↘ REJECTED
      ↘ CANCELLED
APPROVED → IN_PROCUREMENT → PARTIALLY_ORDERED → ORDERED
ORDERED → PARTIALLY_FULFILLED → FULFILLED → CLOSED
APPROVED → DIRECT_PURCHASE_AUTHORIZED → PARTIALLY_FULFILLED | FULFILLED → CLOSED

Approval Task
UNASSIGNED → PENDING → APPROVED | REJECTED | CHANGES_REQUESTED
PENDING → SUPERSEDED | CANCELLED

RFQ
DRAFT → OPEN → CLOSED → EVALUATING → AWARDED
     ↘ CANCELLED

Purchase Order
DRAFT → PENDING_APPROVAL → APPROVED → ISSUED
ISSUED → PARTIALLY_FULFILLED → FULFILLED → CLOSED
      ↘ CANCELLED, cuando las condiciones comerciales lo permitan

Receipt / Acceptance Document
DRAFT → POSTED → REVERSED

Supplier Invoice
DRAFT → REGISTERED → MATCHING → MATCHED
                              ↘ EXCEPTION
MATCHED → APPROVED_FOR_PAYMENT → PARTIALLY_PAID → PAID
EXCEPTION → MATCHING | APPROVED_FOR_PAYMENT | REJECTED | CANCELLED

Payment
DRAFT → PENDING_APPROVAL → APPROVED → SCHEDULED → PAID
                                      ↘ FAILED
                                      ↘ CANCELLED

Supplier
DRAFT → PENDING_APPROVAL → ACTIVE → SUSPENDED | BLOCKED

Subscription
PENDING_PROVISIONING → ACTIVE → SUSPENDED | CANCELLED | EXPIRED
PENDING_PROVISIONING → PROVISIONING_FAILED → PENDING_PROVISIONING | CANCELLED
```

---

### 0.7 Regla de modificación y reevaluación

Ningún documento aprobado se modifica silenciosamente.

Cambios materiales incluyen:

- importe o moneda;
- Cost Center o Beneficiary Department;
- Spend Category o Purchase Type;
- proveedor seleccionado;
- Preferred Product o Required Product;
- respuestas de riesgo;
- condiciones de pago o contractuales;
- cantidad, precio o alcance de una PO.

Un cambio material:

1. crea una nueva versión o amendment;
2. marca como `SUPERSEDED` las tareas que ya no sean válidas;
3. vuelve a ejecutar las políticas afectadas;
4. ajusta reservas o compromisos de forma auditable;
5. solicita nuevamente las aprobaciones necesarias.

Un cambio descriptivo sin impacto funcional puede conservar las aprobaciones, pero siempre queda auditado.

---

## 1. Objetivo del proyecto

Construir una plataforma interna empresarial para gestionar el ciclo completo de compra desde que un empleado detecta una necesidad hasta que la empresa paga al proveedor.

El sistema debe resolver problemas como:

- solicitudes de compra dispersas por correo, mensajes o documentos;
- aprobaciones sin trazabilidad;
- compras realizadas sin verificar presupuesto;
- dificultad para saber de qué centro de costo sale cada gasto;
- selección informal de proveedores;
- ausencia de reglas para comparar cotizaciones;
- órdenes de compra sin seguimiento;
- falta de evidencia de recepción;
- facturas que no coinciden con lo realmente ordenado o recibido;
- pagos sin suficientes controles;
- ausencia de segregación de funciones;
- escaso control de suscripciones empresariales;
- poca visibilidad sobre gasto reservado, comprometido y consumido;
- dificultad para evaluar el desempeño histórico de proveedores.

La plataforma no pretende replicar SAP, Oracle u otro ERP completo. El objetivo es implementar un sistema empresarial coherente, extensible y suficientemente profundo para demostrar modelado de negocio real.

---

## 2. Alcance conceptual

El núcleo del proyecto cubre:

1. Solicitudes de compra.
2. Aprobación de necesidad por departamento.
3. Validación y aprobación presupuestaria.
4. Motor de políticas de compra.
5. Revisiones condicionales de IT y Legal.
6. Procurement / Compras.
7. Proveedores.
8. RFQ y cotizaciones.
9. Evaluación de ofertas.
10. Selección de proveedor.
11. Purchase Orders.
12. Recepción o aceptación.
13. Bienes físicos.
14. Servicios.
15. Suscripciones SaaS.
16. Facturas de proveedor.
17. Matching.
18. Cuentas por pagar.
19. Pagos.
20. Auditoría.
21. Cost Centers.
22. Budgets.
23. Supplier Performance.
24. Segregation of Duties.

No se diseñará todavía:

- arquitectura técnica;
- base de datos;
- endpoints;
- framework frontend/backend;
- nube;
- deployment;
- integración SUNAT;
- módulo de inventario;
- gestión completa de almacenes;
- gestión completa de contratos;
- gestión completa de proyectos;
- contabilidad general.

---

## 3. Regla terminológica principal

Para evitar ambigüedad, en este proyecto se utilizará:

**Department / Departamento**

No se alternará entre "Área" y "Departamento".

Ejemplos:

- Departamento Software / IT
- Departamento Recursos Humanos
- Departamento Finanzas
- Departamento Compras
- Departamento Legal
- Departamento Operaciones

Si posteriormente la empresa requiere una jerarquía mayor, se podrá extender a:

```text
Business Unit
    ↓
Department
    ↓
Team
```

Pero esa estructura no forma parte del alcance inicial.

---

## 4. Cinco conceptos que nunca deben confundirse

### 4.1 Department

Unidad organizativa a la que pertenece una persona.

Ejemplo:

```text
Department:
Finance
```

---

### 4.2 Job Title

Cargo laboral real de la persona.

Ejemplos:

```text
Software Developer
Finance Analyst
Engineering Manager
Procurement Specialist
Procurement Manager
CFO
Security Engineer
```

El cargo describe la posición dentro de la empresa.

---

### 4.3 System Role

Permiso o conjunto de capacidades dentro de la aplicación.

Ejemplos:

```text
REQUESTER
FINANCE_APPROVER
PROCUREMENT_BUYER
```

Un usuario puede tener múltiples roles.

---

### 4.4 Process Responsibility

Responsabilidad que una persona tiene sobre un elemento concreto del proceso.

Ejemplos:

```text
Cost Center Owner de CC-IT-DEV

Acceptance Owner de PO-2026-0142

Approval Task asignada para PR-2026-0042
```

No toda responsabilidad debe convertirse en un rol global.

---

### 4.5 Approval Authority

Mandato que determina qué clase de decisión puede aprobar una persona, hasta qué importe, sobre qué alcance y durante qué periodo.

No es un Job Title ni una tarea concreta.

Ejemplo:

```yaml
ApprovalAuthorityGrant:
  type: FINANCIAL
  level: FINANCE_LEVEL_4
  max_amount_base: 500000
  currency: PEN
  scopes:
    legal_entities: [COMPANY_PE]
    cost_centers: [CC-*]
  valid_from: 2026-01-01
  valid_to: null
```

Para recibir una Approval Task, una persona necesita:

```text
System Role compatible
+ Approval Authority suficiente
+ Scope aplicable
+ Vigencia
+ Ausencia de conflictos de Segregation of Duties
```

Los niveles de autoridad, sus nombres y sus límites son configurables. Las políticas solicitan una capacidad y nivel mínimos; nunca buscan un Job Title concreto.

Cuando un grant utiliza nivel y límite numérico, ambos deben cumplirse: el nivel debe ser igual o superior al solicitado y `max_amount_base` debe cubrir el importe evaluado en moneda base.

Tipos iniciales de authority grant:

```text
BUSINESS_NEED
FINANCIAL
PROCUREMENT
SUPPLIER_MASTER
MATCH_EXCEPTION
PAYMENT
```

IT y Legal utilizan sus System Roles y scopes de asignación; pueden incorporar niveles de autoridad posteriormente si sus políticas lo requieren.

---

## 5. Ejemplo de separación correcta

```text
Pedro

Department:
Finance

Job Title:
Finance Analyst

System Roles:
REQUESTER
FINANCE_APPROVER

Approval Authority:
FINANCIAL / FINANCE_LEVEL_1 / <= S/20,000
```

Pedro puede:

- solicitar una compra como `REQUESTER`;
- aprobar determinadas solicitudes cuando `FINANCE_APPROVER` y su Approval Authority cubran el importe y scope.

Pero si Pedro crea una solicitud:

```text
requester == Pedro
```

Pedro queda excluido como aprobador de esa misma solicitud.

---

## 6. Roles del sistema definitivos

### REQUESTER

Puede crear solicitudes de compra.

No equivale a "empleado".

La mayoría de empleados activos podrían tener este rol.

---

### DEPARTMENT_APPROVER

Evalúa si la necesidad empresarial del departamento está justificada.

Pregunta principal:

> ¿Realmente necesitamos esta compra?

Normalmente será una persona con autoridad dentro del departamento, pero no necesariamente el jefe máximo.

Para recibir la tarea necesita `DEPARTMENT_APPROVER` y Approval Authority `BUSINESS_NEED` sobre el Department o Cost Center correspondiente.

---

### FINANCE_APPROVER

Evalúa la disponibilidad presupuestaria y autorización financiera.

Pregunta principal:

> ¿Podemos permitirnos este gasto y de qué presupuesto debe salir?

No debe llamarse simplemente `FINANCE`, porque Finance es un departamento.

Para recibir una tarea necesita `FINANCE_APPROVER` y Approval Authority `FINANCIAL` suficiente para el importe, moneda base y scope correspondientes.

---

### PROCUREMENT_BUYER

Gestiona Procurement.

Responsabilidades:

- sourcing;
- proveedores;
- RFQ;
- cotizaciones;
- evaluación;
- negociación;
- selección de proveedor;
- creación de PO.

---

### PROCUREMENT_APPROVER

Aprueba determinadas decisiones de Procurement.

Ejemplos:

- PO de alto importe;
- quotation waiver;
- excepciones al proceso normal de sourcing;
- activación, suspensión y cambios sensibles de Supplier Master;
- determinadas decisiones comerciales de alto riesgo.

No equivale al cargo `Procurement Manager`, aunque una persona con ese cargo probablemente tenga este rol.

Cuando la decisión tiene threshold o scope, también requiere Approval Authority `PROCUREMENT` suficiente.

---

### AP_SPECIALIST

AP = Accounts Payable = Cuentas por Pagar.

Responsabilidades:

- registrar y revisar Supplier Invoices;
- gestionar discrepancias;
- revisar matching;
- preparar/programar pagos;
- bloquear pagos cuando existan excepciones.

No recibe cajas ni modifica Goods Receipts o POs.

---

### PAYMENT_APPROVER

Aprueba o libera pagos preparados por Accounts Payable.

No registra facturas ni prepara el mismo pago que aprueba.

Puede requerir una `Approval Authority` de tipo `PAYMENT` con alcance e importe suficientes.

El rol autoriza a participar en el proceso; la Approval Authority determina qué pagos concretos puede aprobar.

---

### IT_REVIEWER

Realiza revisión humana condicional para determinadas compras tecnológicas.

Puede evaluar:

- seguridad;
- privacidad;
- acceso a datos empresariales;
- compatibilidad;
- integraciones;
- herramientas existentes;
- duplicación de software;
- riesgos técnicos.

---

### IT_PROVISIONER

Responsable de provisionar herramientas tecnológicas adquiridas.

Especialmente útil para SaaS.

Ejemplos:

- crear/administrar workspace;
- asignar seat;
- asignar licencia;
- habilitar acceso;
- registrar activación.

---

### LEGAL_REVIEWER

Revisión jurídica condicional.

Se activa cuando las políticas lo requieren.

Ejemplos:

```text
contract_required = true
non_standard_terms = true
```

No implica construir Contract Management completo.

---

### ADMIN

Configura:

- usuarios;
- roles;
- departamentos;
- políticas;
- Cost Centers;
- presupuestos;
- catálogos;
- parámetros generales.

`ADMIN` no obtiene automáticamente autoridad para aprobar necesidades, proveedores, excepciones, facturas o pagos. La administración técnica y la autoridad empresarial permanecen separadas.

---

### AUDITOR

Acceso de solo lectura a documentos, decisiones, movimientos y Audit Trail dentro de su scope asignado.

No crea ni modifica documentos empresariales y no aprueba transacciones. Este rol evita utilizar `ADMIN` como sustituto de auditoría o compliance.

---

## 7. CFO

`CFO` no será un System Role.

Es un Job Title:

```text
Chief Financial Officer
Director Financiero
```

Ejemplo:

```text
Roberto

Department:
Finance

Job Title:
CFO

System Roles:
REQUESTER
FINANCE_APPROVER
```

La diferencia respecto a otro `FINANCE_APPROVER` está en su `Approval Authority`, no en el Job Title.

Ejemplo ilustrativo:

```yaml
Finance Analyst:
  system_role: FINANCE_APPROVER
  authority_level: FINANCE_LEVEL_1
  max_amount_base: 20000

Roberto:
  job_title: CFO
  system_role: FINANCE_APPROVER
  authority_level: FINANCE_LEVEL_4
  max_amount_base: 500000
```

Las cifras, niveles y scopes son configurables y no definitivos. Una empresa puede utilizar títulos como `Director de Finanzas`, `VP Finance`, `Head of Finance` o `Gerente Financiero` sin cambiar las reglas del sistema.

Una política nunca exige `CFO`. Exige una aprobación financiera con capacidad, nivel, alcance e importe suficientes.

---

## 8. Procurement Manager

Igual que CFO:

```text
Procurement Manager
```

es un Job Title.

La capacidad dentro del sistema se expresa mediante System Roles y, para decisiones concretas, Approval Authority:

```text
PROCUREMENT_BUYER
PROCUREMENT_APPROVER
```

Ejemplo:

```text
Lucía

Department:
Procurement

Job Title:
Procurement Manager

System Roles:
REQUESTER
PROCUREMENT_BUYER
PROCUREMENT_APPROVER

Approval Authority:
PROCUREMENT / PROCUREMENT_LEVEL_3 / configured scope
```

---

## 9. Departments de referencia

Inicialmente se pueden considerar:

```text
Software / IT
Human Resources
Finance
Procurement
Legal
Operations
Administration
```

La aplicación debe permitir configurar otros departamentos.

---

## 10. Cost Centers

Los Cost Centers permiten identificar dónde se genera el gasto.

No se asumirá:

```text
1 Department = 1 Cost Center
```

Un departamento puede tener múltiples Cost Centers.

Ejemplo:

```text
Department Software / IT
│
├── CC-IT-DEV
├── CC-IT-INFRA
└── CC-IT-TOOLS
```

Interpretación:

```text
CC-IT-DEV
Development

CC-IT-INFRA
Infrastructure

CC-IT-TOOLS
Tools / internal software
```

DEV, INFRA y TOOLS no tienen que ser subdepartamentos organizativos reales. Son ámbitos de control de gasto.

---

## 11. Grupo jerárquico de Cost Centers

Puede existir conceptualmente:

```text
IT
│
├── CC-IT-DEV
├── CC-IT-INFRA
└── CC-IT-TOOLS
```

`IT` funciona como nodo/grupo de agregación.

No será otro Cost Center imputable.

No se recomienda permitir:

```text
CC-IT
CC-IT-DEV
CC-IT-INFRA
CC-IT-TOOLS
```

si todos pueden recibir gasto, porque generaría ambigüedad.

---

## 12. Analytics por Cost Center

La separación permite análisis como:

```text
IT Spend 2026

DEV            S/91,230
INFRA          S/72,100
TOOLS          S/47,670
```

o:

```text
Monthly Spend

             Jan      Feb      Mar

DEV          8,200    7,400    12,300
INFRA        3,200    9,800     4,100
TOOLS        2,900    3,100     5,700
```

Estos analytics forman parte natural del dominio, aunque los dashboards avanzados pueden construirse después del flujo principal.

---

## 13. Cost Center Owner

`Cost Center Owner` no será un System Role.

Es una asignación/responsabilidad sobre un Cost Center concreto.

Ejemplo:

```text
CC-IT-DEV

Owner:
Carlos
```

Carlos puede tener:

```text
Job Title:
Engineering Manager

System Roles:
REQUESTER
DEPARTMENT_APPROVER
```

y además ser responsable de:

```text
CC-IT-DEV
```

---

## 14. Capacidades del Cost Center Owner

Inicialmente:

- visibilidad;
- monitoreo;
- analytics del CC;
- seguimiento de consumo;
- seguimiento de reservas;
- seguimiento de compromisos.

No obtiene automáticamente:

```text
FINANCE_APPROVER
```

Ser responsable de un Cost Center no implica poder autorizar financieramente cualquier compra.

---

## 15. Budgets

El presupuesto no se modelará únicamente como:

```text
Cost Center
+
Total anual
```

Se utilizará:

```text
Cost Center
+
Fiscal Year
+
Spend Category
```

Ejemplo:

```text
CC-IT-DEV

Budget 2026
S/150,000

├── Hardware
│   S/70,000
│
├── Software
│   S/60,000
│
└── Training
    S/20,000
```

Cada Purchase Request Line imputa a una sola posición `Cost Center + Fiscal Year + Spend Category` en Release 1. Una obligación que deba afectar varios ejercicios se divide en líneas o milestones separados por Fiscal Year.

La plataforma realiza control presupuestario operativo, no periodificación contable. El consumo permanece asociado a la posición presupuestaria aprobada aunque la factura o el pago se registren posteriormente.

---

## 16. Spend Category vs Purchase Type

Son conceptos diferentes.

### Purchase Type

Responde:

> ¿Qué tipo de fulfillment esperamos?

Valores iniciales:

```text
GOOD
SERVICE
SUBSCRIPTION
```

---

### Spend Category

Responde:

> ¿Qué clase de gasto es?

Ejemplos:

```text
HARDWARE
SOFTWARE
TRAINING
OFFICE_SUPPLIES
CONSULTING
MAINTENANCE
```

Ejemplo:

```text
Laptop

Purchase Type:
GOOD

Spend Category:
HARDWARE
```

```text
AI coding assistant

Purchase Type:
SUBSCRIPTION

Spend Category:
SOFTWARE
```

```text
Vue training course

Purchase Type:
SERVICE

Spend Category:
TRAINING
```

---

## 17. Purchase Request multilínea

Una Purchase Request puede contener una o varias líneas.

`Requester` es quien crea y presenta la solicitud. `Requested For` es la persona beneficiaria de una línea y puede ser distinta. `Requested For` no hereda permisos de aprobación por ser beneficiaria y se utiliza como candidato inicial para Acceptance Owner o User Access Confirmation.

Ejemplo:

```text
PR-2026-0092

Line 1
Laptop
GOOD
HARDWARE
S/6,000

Line 2
Antivirus anual
SUBSCRIPTION
SOFTWARE
S/500

Line 3
Instalación
SERVICE
MAINTENANCE
S/300
```

---

## 18. Cost Center por línea

Cada línea puede imputarse a un Cost Center distinto.

Ejemplo:

```text
PR-2026-0092

Laptop
CC-IT-DEV
S/6,000

Servidor
CC-IT-INFRA
S/12,000

GitHub
CC-IT-TOOLS
S/2,000
```

El control presupuestario se realiza por línea. El Department que aprueba la necesidad se determina desde el Cost Center de la línea.

---

## 19. Project como dimensión futura

No se implementará Project Management.

Pero el modelo funcional debe permitir que en el futuro una línea pueda relacionarse con:

```text
Project:
MIGRATION-DC-2027
```

Eso permitiría conectar esta plataforma con un futuro proyecto independiente de gestión de proyectos.

No implica introducir:

- tareas;
- sprints;
- Kanban;
- cronogramas;
- project managers;
- milestones técnicos.

La integración futura permitiría responder:

> ¿Cuánto se gastó en determinado proyecto?

---

## 20. Ciclo presupuestario

Se utilizarán cuatro magnitudes presupuestarias principales:

```text
REQUESTED
RESERVED
COMMITTED
CONSUMED
```

No son estados mutuamente excluyentes. Una línea parcialmente ordenada, recibida o facturada puede mantener importes simultáneos en varios buckets.

```text
AVAILABLE = ALLOCATED - RESERVED - COMMITTED - CONSUMED
```

`REQUESTED` sirve para visibilidad y forecasting, pero no reduce `AVAILABLE`.

Los buckets se calculan desde movimientos inmutables y auditables:

```text
REQUEST
RELEASE_REQUEST
RESERVE
RELEASE_RESERVATION
COMMIT
RELEASE_COMMITMENT
CONSUME
REVERSE_CONSUMPTION
```

---

## 21. REQUESTED

Existe una solicitud, pero todavía no existe una reserva presupuestaria definitiva.

Cuando la línea se reserva, rechaza o cancela, el importe correspondiente deja de formar parte de `REQUESTED` mediante un movimiento `RELEASE_REQUEST`.

Ejemplo:

```text
Employee requests:
S/6,500
```

---

## 22. RESERVED

Después de completar todos los controles previos exigidos por la evaluación de políticas:

```text
Required human approvals      ✓
Required automatic controls   ✓
```

el importe queda reservado.

Una compra LOW_VALUE puede reservar presupuesto sin `FINANCE_APPROVER` manual si la política solo exige Department Approval y Automatic Budget Check.

Ejemplo:

```text
RESERVED
S/6,500
```

Interpretación:

> La empresa ha autorizado apartar presupuesto para esta compra.

---

## 23. COMMITTED

Cuando una PO cambia a `ISSUED` y nace una obligación comercial:

```text
PO
S/6,320
```

se libera la porción correspondiente de la reserva y se crea el compromiso.

Ejemplo:

```text
Reserved:
S/6,500 → S/0

Committed:
S/0 → S/6,320
```

La diferencia:

```text
S/180
```

vuelve a estar disponible. Si la PO supera el importe reservado, el sistema no compromete la diferencia silenciosamente: vuelve a comprobar disponibilidad, políticas y Approval Authority antes de emitirla.

---

## 24. CONSUMED

Cuando se reconoce una obligación válida para pago, normalmente porque la Supplier Invoice fue aprobada según su matching policy:

```text
Committed:
S/6,320 → S/0

Consumed:
S/0 → S/6,320
```

El gasto permanece consumido aunque posteriormente el pago pase a `PAID`. En una compra `PREPAID`, la matching policy puede permitir consumo antes de la activación; la activación continúa como obligación operativa pendiente. En una Direct Purchase sin PO, el consumo ocurre al validar el documento de compra y su recepción o aceptación.

Una factura parcial consume únicamente su parte. El resto puede permanecer comprometido.

Si una factura aprobada se cancela o corrige antes del pago, se registra `REVERSE_CONSUMPTION`. El importe vuelve a `COMMITTED` cuando la PO sigue vigente; si la obligación comercial también se reduce, el PO Amendment libera la parte correspondiente.

---

### 24.1 Convenciones monetarias

La organización tiene una moneda presupuestaria base configurable. Cada documento conserva su moneda transaccional y su equivalente evaluado en moneda base.

```yaml
transaction_amount: 100
transaction_currency: USD
exchange_rate: 3.75
exchange_rate_date: 2026-09-07
evaluated_base_amount: 375
base_currency: PEN
```

El tipo de cambio se congela en cada hecho relevante:

- envío de la solicitud;
- reserva;
- emisión de la PO;
- aprobación de la factura.

Las diferencias se conservan como variación presupuestaria auditable. Las fuentes y frecuencias de los tipos de cambio se definirán en la SPEC correspondiente.

Para Release 1, el importe utilizado en presupuesto y thresholds es el total bruto esperado a pagar:

```text
subtotal + taxes + additional charges - discounts
```

El matching compara separadamente líneas, impuestos, cargos y total. Los redondeos respetan los minor units de cada moneda.

---

## 25. Payment Status es una dimensión diferente

No debe confundirse presupuesto con pago.

Ejemplos de Payment Status:

```text
DRAFT
PENDING_APPROVAL
APPROVED
SCHEDULED
PAID
FAILED
CANCELLED
```

Por ejemplo:

```text
Budget position:
Consumed amount recorded

Payment state:
SCHEDULED
```

es perfectamente válido.

`AP_SPECIALIST` prepara o programa el pago y `PAYMENT_APPROVER` lo aprueba. Release 1 no mueve fondos: `PAID` se registra a partir de un resultado externo con su referencia y fecha.

Los pagos asignan importes mediante `Payment Allocations`. Una factura puede quedar `PARTIALLY_PAID`; un Payment puede agrupar varias facturas únicamente cuando pertenecen al mismo proveedor, moneda y Legal Entity.

`FAILED` o `CANCELLED` no revierte `CONSUMED`: la obligación aprobada continúa pendiente y puede originar un nuevo Payment. Solo un resultado `PAID` aplica definitivamente sus Payment Allocations al saldo de las facturas.

---

## 26. Distintos grados de compromiso

No significa prioridad empresarial.

No significa:

```text
Compra A estaba comprometida
↓
aparece Compra B más importante
↓
quitamos dinero automáticamente a A
```

Significa nivel de obligación.

```text
REQUESTED
"Queremos gastar"

RESERVED
"Hemos autorizado y apartado presupuesto"

COMMITTED
"Ya existe una obligación comercial"

CONSUMED
"El gasto ya se materializó"
```

---

## 27. Cancelación y liberación de fondos

Si una línea conserva importe en:

```text
RESERVED
```

puede cancelarse y liberar esa reserva.

Si la línea conserva importe en:

```text
COMMITTED
```

porque ya existe una PO:

```text
PO-2026-0092
```

primero habrá que cancelar/modificar la PO u obligación comercial si las condiciones lo permiten.

Después podrá liberarse el compromiso de forma total o proporcional. Una cancelación parcial se realiza mediante PO Amendment y no altera receipts, invoices o consumos ya publicados.

---

## 28. Visibilidad presupuestaria

### REQUESTER

No ve saldos completos de Cost Centers.

Debe poder crear y seguir su solicitud, pero no necesita conocer cuánto dinero tiene el departamento.

---

### DEPARTMENT_APPROVER

Puede ver información presupuestaria detallada si es responsable del Cost Center correspondiente. Si no es owner, solo ve el resultado necesario para decidir, por ejemplo `SUFFICIENT_FUNDS` o `INSUFFICIENT_FUNDS`, no el saldo completo.

---

### FINANCE_APPROVER

Ve los Cost Centers y budgets sobre los que tenga autoridad.

---

### CFO

El Job Title `CFO` no concede visibilidad. La persona puede tener visión global o amplia cuando sus System Roles y Approval Authorities configuradas lo permitan.

Recordatorio:

```text
CFO = Job Title
FINANCE_APPROVER = System Role
```

---

### ADMIN

Visibilidad/configuración completa.

---

### PROCUREMENT_BUYER

Ve la información necesaria para ejecutar la compra, pero no necesariamente toda la información financiera de la empresa.

---

## 29. Budget Check automático

Cuando se crea o procesa una solicitud, el sistema realiza un precheck automático.

El `REQUESTER` no tiene por qué ver:

```text
Available budget:
S/837,230
```

El `FINANCE_APPROVER` sí podría ver:

```text
CC-IT-DEV / Hardware

Budget         S/70,000
Consumed       S/31,000
Committed      S/12,000
Reserved        S/4,000
Available      S/23,000

Request         S/6,500

✓ SUFFICIENT FUNDS
```

La disponibilidad deberá verificarse de nuevo y reservarse de forma atómica al completar la aprobación, para evitar decisiones concurrentes basadas en datos desactualizados.

Si el resultado es `INSUFFICIENT_FUNDS`, no se crea reserva ni se permite continuar a Procurement o Direct Purchase. El requester puede modificar o cancelar la línea. Budget Transfers y Adjustments permanecen fuera de Release 1.

---

## 30. Acciones de aprobación

Inicialmente:

```text
APPROVE
REJECT
REQUEST_CHANGES
```

Posible extensión posterior:

```text
DELEGATE
```

Las aprobaciones siguen un modelo mixto:

1. Department Approval ocurre primero.
2. Finance, IT y Legal pueden ejecutarse en paralelo cuando no existe dependencia entre ellas.
3. Procurement comienza cuando todos los controles previos requeridos están completos.
4. Procurement Approval se ejecuta antes de emitir la PO cuando la política lo exige.
5. Payment Approval se ejecuta después de que la factura quede habilitada para pago.

La delegación detallada se cerrará en la Approval SPEC. Nunca concede al delegado una autoridad superior a la que ya posee y siempre conserva delegante, delegado, vigencia, motivo y tareas afectadas.

Si no existe una persona elegible, la Approval Requirement queda `UNASSIGNED` y el proceso se bloquea. `ADMIN` puede corregir asignaciones o configuración, pero no marcar la aprobación empresarial como completada sin una autoridad válida.

---

## 31. REQUEST_CHANGES

Ejemplo:

```text
Finance:

"El presupuesto no admite S/7,000.
¿Puedes reducir el requerimiento a S/5,500?"
```

La solicitud pasa a:

```text
CHANGES_REQUESTED
```

manteniendo historial.

La edición posterior crea una nueva versión. Las tareas afectadas pasan a `SUPERSEDED`, las políticas se evalúan nuevamente y cualquier reserva previa se ajusta de forma auditable antes de continuar.

---

## 32. Segregation of Duties

Principio:

> Una sola persona no debería controlar todo el ciclo.

Ejemplos de reglas:

```text
requester != approver
```

```text
requester != finance_approver
```

```text
buyer != match_exception_approver
```

```text
buyer != procurement_approver when procurement approval is required
```

```text
invoice_registrar != payment_approver
```

```text
payment_preparer != payment_approver
```

```text
AP_SPECIALIST cannot modify PO
```

```text
AP_SPECIALIST cannot modify Goods Receipt
```

---

## 33. FINANCE_APPROVER solicitando una compra

Ejemplo:

```text
Pedro

Department:
Finance

System Roles:
REQUESTER
FINANCE_APPROVER
```

Pedro solicita:

```text
2 monitors
S/2,400
```

Pedro queda excluido de las tareas de aprobación relacionadas con su solicitud.

Flujo:

```text
Pedro
REQUESTER
        ↓

Laura
DEPARTMENT_APPROVER
        ↓

Miguel
FINANCE_APPROVER
```

No existe una regla especial de tres aprobaciones solo por trabajar en Finanzas.

---

## 34. Procurement / Compras

Department:

```text
Procurement
```

Roles relevantes:

```text
PROCUREMENT_BUYER
PROCUREMENT_APPROVER
```

---

## 35. Supplier Master

Datos iniciales de proveedor:

```text
Legal Name
Trade Name
Tax ID / RUC
Country
Address
Contacts
Email
Phone
Payment Terms
Supported Currencies
Categories Supplied
Status
Risk Status
Performance Score
Banking Details
```

Los datos bancarios deben tener permisos especiales.

Gobierno inicial:

```text
PROCUREMENT_BUYER
crea o actualiza el borrador del proveedor

PROCUREMENT_APPROVER
aprueba activación, suspensión y cambios sensibles

AP_SPECIALIST
consulta los datos necesarios para facturación y pago
```

Reglas:

- `Country + Tax ID` debe ser único;
- un proveedor `BLOCKED` no puede recibir nuevas POs;
- cambios de Banking Details requieren una segunda aprobación distinta del editor;
- los datos bancarios se muestran enmascarados salvo permiso explícito;
- toda modificación sensible conserva valor anterior, actor, motivo y aprobación;
- `ADMIN` no sustituye la aprobación empresarial del proveedor.

---

## 36. Supplier Status

Estados:

```text
DRAFT
PENDING_APPROVAL
ACTIVE
SUSPENDED
BLOCKED
```

Flujo principal:

```text
DRAFT → PENDING_APPROVAL → ACTIVE → SUSPENDED | BLOCKED
```

Un proveedor suspendido o bloqueado puede seguir siendo visible en documentos históricos.

---

## 37. RFQ

RFQ = Request For Quotation.

Pregunta al proveedor:

> ¿Cuánto cobrarías y bajo qué condiciones?

Como Supplier Portal está fuera de Release 1, el `PROCUREMENT_BUYER` registra las respuestas recibidas por canales externos. Debe conservar proveedor, fecha y hora de recepción, moneda, términos, líneas cotizadas y evidencia original adjunta.

Ejemplo:

```text
RFQ-2026-0128

Need:
5 laptops

RAM:
>= 32 GB

SSD:
>= 1 TB

Dedicated GPU:
Required

Warranty:
>= 3 years

Response deadline:
12/09/2026 18:00
```

---

## 38. RFQ Deadlines

La RFQ tendrá:

```text
Opened At
Response Deadline
Required Quotations
```

Ejemplo:

```text
Required quotations:
3
```

---

## 39. Respuestas tardías

Si el proveedor responde después del deadline:

```text
LATE
```

y por defecto no entra en evaluación.

---

## 40. EXTEND_RFQ

Procurement puede extender el deadline.

Debe quedar auditado:

```text
Old deadline:
12/09

New deadline:
14/09

Reason:
Only two valid quotations received.
```

---

## 41. QUOTATION_WAIVER

Si la política exige:

```text
3 quotations
```

y solamente existen:

```text
2 valid quotations
```

el Buyer puede:

1. extender la RFQ; o
2. solicitar una excepción.

Ejemplo:

```text
QUOTATION_WAIVER

Reason:
Only two approved suppliers exist for this equipment category.
```

La excepción puede requerir:

```text
PROCUREMENT_APPROVER
```

---

## 42. Supplier Performance Score

Mide desempeño histórico del proveedor.

Puede considerar:

```text
On-time delivery
Quantity compliance
Quality incidents
Rejected goods
Invoice discrepancies
Contract compliance
```

Ejemplo:

```text
Supplier ABC

On-time delivery       92/100
Quantity compliance    97/100
Quality                 89/100
Invoice accuracy        95/100

Overall                 93/100
```

Estos datos deben alimentarse desde hechos reales registrados en el sistema.

---

## 43. Quote Evaluation Score

Mide una cotización concreta.

Puede considerar:

```text
Price
Delivery Time
Warranty
Payment Terms
Technical Compliance
Supplier Performance
```

Los pesos deben ser configurables.

Cuando las cotizaciones usan monedas diferentes, el criterio de precio se compara mediante importes normalizados a la moneda base con un tipo de cambio y fecha de evaluación conservados en el resultado.

Ejemplo ilustrativo:

```text
Price               40%
Delivery            20%
Warranty            15%
Quality             15%
Supplier History    10%
```

No se consideran pesos definitivos.

---

## 44. Selección de proveedor

El sistema recomienda, pero Procurement conserva decisión humana.

Ejemplo:

```text
Supplier A     89/100
Supplier B     81/100
Supplier C     94/100

Recommended:
Supplier C
```

Si el Buyer selecciona B:

```text
⚠ Selected supplier is not the highest-scoring option.

Justification required:
[________________________]
```

---

## 45. PO

PO = Purchase Order = Orden de Compra.

No es una RFQ.

RFQ:

```text
"¿Cuánto me cobrarías?"
```

PO:

```text
"Te ordenamos entregar esto bajo estas condiciones."
```

---

## 46. Datos de una PO

Ejemplo:

```text
PURCHASE ORDER
PO-2026-00921

Supplier:
ABC Tech

Items:
5 laptops

Unit Price:
S/5,000

Total:
S/25,000

Delivery Location:
Lima Office

Delivery Date:
15/09/2026

Payment Terms:
NET_30
```

La PO formaliza la operación y posteriormente sirve como una de las principales fuentes para matching.

Una PO pertenece a una sola Legal Entity, proveedor y moneda. Sus líneas conservan referencia a las Purchase Request Lines de origen. Debe registrar subtotal, impuestos, cargos, descuentos, total, versión y estado.

El importe correspondiente se mueve de `RESERVED` a `COMMITTED` cuando la PO cambia a `ISSUED`. Una modificación posterior se realiza mediante PO Amendment; nunca se sobrescribe la versión emitida.

---

## 47. Financial Approval Authority vs PROCUREMENT_APPROVER

No son redundantes.

Un `FINANCE_APPROVER` con autoridad suficiente pregunta:

> ¿Autorizo financieramente este gasto de alto importe?

PROCUREMENT_APPROVER pregunta:

> ¿Autorizo esta operación de compra, PO o excepción de Procurement?

Ejemplo:

```text
Purchase:
S/200,000

DEPARTMENT_APPROVER
        ↓
FINANCE_APPROVER
with required Approval Authority
        ↓
PROCUREMENT_BUYER
        ↓
PROCUREMENT_APPROVER
```

No todas las compras requieren todos los pasos. Si una empresa desea dos aprobaciones financieras sucesivas, la política genera dos Approval Requirements diferenciados. No se infiere una aprobación adicional desde el Job Title `CFO`.

---

## 48. Acceptance Owner

No se creará inicialmente un rol global:

```text
RECEIVER
```

Cada compra tendrá un usuario responsable de aceptar aquello que el proveedor entrega.

Conceptualmente:

```text
Acceptance Owner
```

---

## 49. Acceptance Owner por tipo

### GOOD

```text
Goods Receiver
```

### SERVICE

```text
Service Acceptance Owner
```

### SUBSCRIPTION

```text
Provisioning Owner
+
User Access Confirmation
```

---

## 50. Cuándo se asigna

No necesariamente durante RFQ.

Puede surgir desde Purchase Request:

```text
Requested For:
Pablo

Delivery Location:
Lima Office
```

y quedar formalizado al preparar la PO/fulfillment:

```text
Acceptance Owner:
Pablo
```

El Acceptance Owner debe quedar asignado antes de emitir la PO o autorizar una Direct Purchase. `Requested For` es el candidato predeterminado, pero un usuario autorizado puede seleccionar otra persona cuando el tipo de entrega, ubicación o responsabilidad operativa lo requiera. El algoritmo y las validaciones exactas se definirán en la Fulfillment SPEC.

---

## 51. Extensión futura a Warehouse

En el futuro podría existir:

```text
Department:
Warehouse / Logistics

Job Title:
Receiving Clerk

System Role:
RECEIVING_CLERK
```

Pero no se implementará todavía:

- almacenes;
- bins;
- inventario;
- transferencias;
- picking;
- stock.

La plataforma debe quedar preparada para integrarlo después.

---

## 52. GOOD

Para bienes físicos:

```text
GOOD
↓
GOODS_RECEIPT
```

---

## 53. Goods Receipt

Es un registro/documento de recepción.

No es un rol.

Ejemplo:

```text
GOODS RECEIPT
GR-2026-0052

PO:
PO-2026-00921

Ordered:
5 laptops

Received:
5

Accepted:
4

Rejected:
1

Rejection Reason:
DAMAGED_ITEM
```

---

## 54. Recepción parcial

Debe soportarse.

Ejemplo:

```text
Ordered:
5

Accepted:
4

Outstanding:
1

Status:
PARTIALLY_RECEIVED
```

Cuando llega la unidad pendiente:

```text
+1 accepted

Status:
FULLY_RECEIVED
```

---

## 55. Productos dañados

Ejemplo:

```text
Received:
5

Accepted:
4

Rejected:
1

Reason:
DAMAGED_ITEM
```

Release 1 exige abrir una incidencia y seleccionar una resolución:

```text
SUPPLIER_ISSUE_OPENED
↓
REPLACEMENT_EXPECTED
or
CORRECTED_INVOICE_EXPECTED
or
PO_QUANTITY_REDUCED
```

No se debe obligar técnicamente a rechazar toda la entrega porque una unidad llegó dañada.

Consecuencias:

- `REPLACEMENT_EXPECTED`: el compromiso permanece y una recepción posterior puede completar la cantidad;
- `CORRECTED_INVOICE_EXPECTED`: la cantidad rechazada no se consume y el pago permanece bloqueado;
- `PO_QUANTITY_REDUCED`: se crea un PO Amendment y se libera el compromiso correspondiente.

Antes del pago se exige una factura corregida. Devoluciones y credit notes posteriores al pago quedan fuera de Release 1.

---

## 56. Goods Receipt y Supplier Performance

La recepción real alimenta métricas.

Ejemplo:

```text
Promised:
10 September

Received:
14 September

Late delivery:
true
```

O:

```text
Ordered:
50

Accepted:
47
```

Estos eventos pueden afectar:

```text
On-time delivery score
Quantity compliance
Quality score
```

---

## 57. SERVICE

Para servicios:

```text
SERVICE
↓
SERVICE_ACCEPTANCE
```

---

## 58. Service Acceptance

Confirma que el proveedor realizó el servicio contratado.

Ejemplo:

```text
PO

Service:
Server maintenance

20 hours
S/150/hour
```

Después:

```text
SERVICE_ACCEPTANCE

Hours Delivered:
20

Work Completed:
Yes

Accepted:
Yes
```

Conceptualmente equivale a la lógica empresarial de una Service Entry / Service Entry Sheet, pero la UI usará el término más claro `SERVICE_ACCEPTANCE`.

---

## 59. SUBSCRIPTION

Para SaaS:

```text
SUBSCRIPTION
↓
IT_PROVISIONER
↓
SUBSCRIPTION_ACTIVATION
↓
USER_ACCESS_CONFIRMATION
```

---

## 60. IT_PROVISIONER

Ejemplo:

```text
Subscription:
AI Coding Assistant

Assigned To:
pablo@company.com

Provisioned By:
it-admin@company.com

Activated:
05/09/2026

Status:
ACTIVE
```

---

## 61. Confirmación del usuario

Después del provisioning:

```text
REQUESTED_FOR USER

✓ ACCESS CONFIRMED
```

La persona para quien se solicitó la suscripción confirma que tiene acceso.

No es necesario que sea quien cree el workspace o administre técnicamente el SaaS.

---

## 62. Vendor Terms configurables

No hardcodear reglas por proveedor.

Evitar lógica como:

```text
if vendor == "Vendor X":
    minimumSeats = 2
```

Usar información configurable.

---

## 63. Subscription Offering

`Subscription Offering` representa condiciones comerciales configurables, no una suscripción adquirida.

Conceptualmente debe permitir:

```text
Vendor
Product
Plan
Billing Model
Billing Cycle
Currency
Unit Price
Minimum Quantity
Maximum Quantity
Auto Renew
Cancellation Notice
Renewal Date
Seat Based?
Usage Based?
Flat Price?
Hybrid?
Effective From
Effective To
```

Las condiciones reales de cada proveedor pueden cambiar con el tiempo.

La entidad `Subscription` representa la instancia adquirida y registra como mínimo:

```text
Offering
Owner
Assigned Users / Seats
Cost Center
Activated At
Billing Cycle
Renewal Date
Status
```

Release 1 cubre compra inicial, provisioning, activación, confirmación de acceso y registro de la renovación. No renueva ni paga automáticamente una suscripción. Toda renovación requiere una nueva decisión `RENEW`, `REDUCE` o `CANCEL`, cuyo workflow detallado queda para la SPEC de subscriptions.

Si una suscripción PREPAID ya pagada no puede activarse, pasa a `PROVISIONING_FAILED` y abre una Supplier Issue. El pago y consumo no se revierten automáticamente. Release 1 permite registrar reintento, escalamiento o cancelación; cualquier devolución o credit note posterior al pago se gestiona externamente y queda fuera de alcance.

---

## 64. IT_REVIEWER

No es automatización.

Es revisión humana condicional.

Ejemplo:

```text
Purchase Type:
SUBSCRIPTION

Spend Category:
SOFTWARE

Processes company data:
YES
```

Policy Engine puede generar:

```text
IT_REVIEW_REQUIRED
```

---

## 65. Preguntas típicas de IT Review

```text
¿Procesa código propietario?
¿Procesa datos personales?
¿Tiene controles administrativos?
¿Cumple las políticas internas?
¿Existe una herramienta equivalente ya contratada?
¿Requiere integraciones especiales?
```

---

## 66. LEGAL_REVIEWER

También revisión humana condicional.

Ejemplos:

```text
IF contract_required = true
    REQUIRE LEGAL_REVIEW

IF non_standard_terms = true
    REQUIRE LEGAL_REVIEW
```

No se implementará todavía un sistema de:

- Contract Lifecycle Management;
- cláusulas;
- e-signatures;
- repository jurídico completo.

---

## 67. SaaS de catálogo

Caso:

```text
Approved Catalog Item:
AI Coding Assistant - Business Plan
```

El usuario solicita directamente:

```text
REQUEST CATALOG ITEM

Product:
AI Coding Assistant

Quantity:
1 seat
```

Si ya existe contrato/proveedor, puede no requerirse nueva comparación competitiva.

---

## 68. SaaS no catalogado

Ejemplo:

```text
Need:
AI coding assistance

Preferred Product:
ChatGPT Business

Justification:
...
```

Procurement puede estudiar alternativas.

---

## 69. Preferred Product vs Required Product

`preferred_product`:

> preferencia del solicitante.

Procurement puede sugerir alternativa.

Acciones posibles:

```text
ACCEPT_ALTERNATIVE
KEEP_ORIGINAL_REQUIREMENT
```

---

`required_product`:

> debe adquirirse exactamente ese producto.

Ejemplo:

```text
Renew existing JetBrains license
```

No tendría sentido sustituirlo automáticamente por otra herramienta.

---

## 70. Supplier Invoice

La plataforma representa a la empresa compradora.

Por tanto:

- el proveedor emite la factura;
- nuestra plataforma registra la factura recibida;
- nuestra plataforma no emite la factura del proveedor.

---

## 71. Alcance inicial de facturas

Inicialmente registrar:

```text
Invoice Number
Supplier
Invoice Date
Currency
Items
Subtotal
Taxes
Total
Related PO
Payment Terms
Attachment
```

Una Supplier Invoice pertenece a un solo proveedor y moneda. Puede haber múltiples facturas parciales contra una PO. Una factura sin PO solo es admisible cuando referencia una Direct Purchase previamente autorizada.

La detección de duplicados utiliza como mínimo `Supplier + Invoice Number + Legal Entity`; la SPEC de invoicing detallará normalización y excepciones.

El sistema valida consistencia empresarial/documental.

No pretende hacer validación tributaria completa.

---

## 72. SUNAT

Queda fuera del núcleo inicial.

No implementar todavía:

- emisión de CPE;
- envío a SUNAT;
- contabilidad tributaria;
- declaración de IGV;
- SIRE;
- PLE.

---

## 73. Extensión SUNAT futura

Podría existir una SPEC posterior para:

```text
Supplier Invoice Validation
```

con funciones como:

- validar RUC;
- consultar comprobante;
- validar estado;
- comparar información;
- trabajar con PDF/XML si corresponde.

Pero solamente después de estudiar correctamente el dominio tributario.

---

## 74. Three-Way Match

Comparación automática entre:

```text
1. Purchase Order
2. Goods Receipt / Acceptance
3. Supplier Invoice
```

Responde:

```text
¿Qué acordamos comprar?
¿Qué recibimos realmente?
¿Qué nos está cobrando el proveedor?
```

El matching se realiza por línea y de forma acumulativa. Puede comparar varias recepciones o aceptaciones y varias facturas parciales contra la misma PO Line. Nunca habilita para pago una cantidad superior a la aceptada salvo excepción autorizada.

---

## 75. Matching Engine

Lo realiza automáticamente el sistema.

No el Receiver.

No AP manualmente.

Las tolerancias de precio y cantidad son configurables por matching policy. El resultado conserva la regla y versión aplicadas; una tolerancia no se hardcodea ni permite exceder una restricción legal o de Segregation of Duties.

Flujo:

```text
PROCUREMENT_BUYER
creates PO

        ↓

Acceptance Owner
creates Goods Receipt / Acceptance

        ↓

AP_SPECIALIST
registers Supplier Invoice

        ↓

MATCHING ENGINE
```

---

## 76. Ejemplo MATCHED

PO:

```text
5 laptops
S/5,000 each
```

Goods Receipt:

```text
5 accepted
```

Invoice:

```text
5 laptops
S/5,000 each
```

Sistema:

```text
PO quantity == accepted quantity
Invoice quantity == accepted quantity
Invoice unit price == PO unit price
```

Resultado:

```text
MATCHED
```

---

## 77. PRICE_MISMATCH

Ejemplo:

```text
PO:
S/25,000

Invoice:
S/27,000
```

Resultado:

```text
PRICE_MISMATCH

PAYMENT BLOCKED
```

---

## 78. QUANTITY_MISMATCH

Ejemplo:

```text
PO:
50 units

Accepted:
47

Invoice:
50 units
```

Resultado:

```text
QUANTITY_MISMATCH

PAYMENT BLOCKED
```

---

## 79. AP_SPECIALIST ante una discrepancia

AP puede:

```text
REQUEST_CLARIFICATION
DISPUTE_INVOICE
REQUEST_CORRECTED_INVOICE
SUBMIT_EXCEPTION_FOR_APPROVAL
```

AP no aprueba por sí mismo una excepción que requiera autoridad. El sistema asigna una Approval Task a una persona elegible con Approval Authority de tipo `MATCH_EXCEPTION`.

Una factura corregida se registra como un documento nuevo enlazado con la factura rechazada o cancelada. La aprobación excepcional conserva tolerancia aplicada, motivo, evidencia, actor y efecto presupuestario; no modifica los documentos fuente.

No debe modificar:

```text
PO
Goods Receipt
Service Acceptance
```

para forzar un match.

---

## 80. Matching en servicios

```text
PO
+
SERVICE_ACCEPTANCE
+
SUPPLIER_INVOICE
```

El principio es el mismo.

---

## 81. Matching en SaaS

No todos los SaaS siguen Three-Way Match clásico.

Ejemplo:

```text
PREPAID
↓
PAYMENT
↓
ACTIVATION
```

Por eso el sistema debe soportar:

```text
matching_policy
```

distinta según tipo/condiciones.

En `PREPAID`, la política puede permitir `APPROVED_FOR_PAYMENT` antes de la activación. El pago no elimina la obligación de provisionar, confirmar acceso y resolver un eventual incumplimiento.

---

## 82. Payment Terms

Valores iniciales:

```text
PREPAID
IMMEDIATE
NET_15
NET_30
NET_60
MILESTONE
```

---

## 83. PREPAID

Pagar antes de recibir o activar.

Ejemplo:

```text
SaaS
Payment
↓
Activation
```

---

## 84. IMMEDIATE

Pago inmediato según acuerdo.

---

## 85. NET_15 / NET_30 / NET_60

Pago dentro del plazo acordado.

Ejemplo:

```text
Supplier delivery
↓
Invoice
↓
Validation
↓
NET_30
↓
Payment
```

---

## 86. MILESTONE

Pago por hitos.

Ejemplo:

```text
Consulting Project
S/100,000

30%
Contract start

40%
MVP accepted

30%
Final delivery accepted
```

Estados:

```text
Milestone 1
S/30,000
PAID

Milestone 2
S/40,000
DUE

Milestone 3
S/30,000
NOT_DUE
```

Cada milestone es una unidad independiente de confirmación, facturación y pago. Registra nombre, importe o porcentaje, condición de vencimiento, Acceptance Owner y estado:

```text
NOT_DUE → DUE → CONDITION_CONFIRMED → INVOICED → PAID
```

Cuando el hito representa una entrega, `CONDITION_CONFIRMED` requiere aceptación. Cuando representa una condición contractual como el inicio, requiere la evidencia configurada.

Esto no implica implementar Contract Management completo.

---

## 87. Procurement Policy Engine

El flujo no debe depender únicamente del importe.

Puede evaluar:

```text
amount
purchase_type
spend_category
department
cost_center
supplier
data_risk
contract_required
preferred_supplier
```

La evaluación ocurre en tres scopes:

```text
LINE
Purchase Type, Spend Category, Cost Center, proveedor y riesgo.

REQUEST
Importe agregado, aprobación financiera y control de fraccionamiento.

SOURCING / PO
Cotizaciones requeridas, award, supplier y Procurement Approval.
```

Reglas de combinación:

1. Los controles compatibles se acumulan.
2. Para Approval Authority se exige el nivel más alto resultante.
3. Para cotizaciones se exige el número máximo resultante.
4. `BLOCK` prevalece sobre `ALLOW`.
5. Una excepción solo reduce controles mediante autoridad explícita, motivo y evidencia auditada.

Cada resultado conserva:

```yaml
PolicyEvaluation:
  policy_version: 4
  evaluated_at: ...
  scope: LINE | REQUEST | SOURCING_PO
  input_snapshot: ...
  generated_requirements: ...
  result: PASSED | REQUIREMENTS_GENERATED | BLOCKED
```

Las políticas se versionan y tienen vigencia. Una solicitud conserva la evaluación utilizada, aunque después cambie la configuración. Los cambios materiales vuelven a evaluar las reglas vigentes y dejan la evaluación anterior en el historial.

Las políticas de importe se evalúan usando el total bruto en moneda base. Los controles financieros consideran también el total agregado de la Purchase Request y aplican el resultado más estricto, evitando reducir controles mediante el fraccionamiento artificial en varias líneas.

---

## 88. Políticas iniciales de referencia

Los importes son **configurables y no definitivos**.

Los rangos siguientes se expresan en moneda base y se aplican sobre el total bruto evaluado. Son ejemplos ilustrativos, no configuración obligatoria.

---

### LOW_VALUE

Ejemplo:

```text
<= S/500
```

Proceso:

```text
REQUESTER
↓
DEPARTMENT_APPROVER
↓
AUTOMATIC BUDGET CHECK
↓
BUDGET RESERVED
↓
DIRECT_PURCHASE_AUTHORIZED
```

Características:

- sin Finance Approval manual por defecto;
- sin RFQ;
- Procurement puede no participar;
- PO no obligatoria;
- supporting document requerido según política.

---

### STANDARD

Ejemplo:

```text
S/501 - S/5,000
```

Proceso:

```text
REQUESTER
↓
DEPARTMENT_APPROVER
↓
AUTOMATIC BUDGET CHECK
↓
PROCUREMENT
↓
Approved Supplier / 1 quotation
↓
PO
```

---

### CONTROLLED

Ejemplo:

```text
S/5,001 - S/20,000
```

Proceso:

```text
REQUESTER
↓
DEPARTMENT_APPROVER
↓
FINANCE_APPROVER
↓
PROCUREMENT
↓
2 valid quotations
↓
PO
```

---

### HIGH_VALUE

Ejemplo:

```text
> S/20,000
```

Proceso:

```text
REQUESTER
↓
DEPARTMENT_APPROVER
↓
FINANCE_APPROVER
with required Approval Authority
↓
PROCUREMENT
↓
3 valid quotations
↓
PO
```

Dependiendo del importe o política también puede requerir:

```text
PROCUREMENT_APPROVER
```

---

## 89. Controles condicionales adicionales

Una compra LOW_VALUE puede igualmente requerir:

```text
IT_REVIEWER
LEGAL_REVIEWER
```

Ejemplo:

```text
Amount:
$100

Evaluated Base Amount:
configured FX conversion

Type:
SUBSCRIPTION

Category:
SOFTWARE

Processes sensitive company data:
YES
```

Resultado:

```text
LOW_VALUE
+
IT_REVIEW_REQUIRED
```

El importe no es la única dimensión de riesgo.

---

## 90. Supporting Documents en compras directas

Una compra directa requiere al menos una evidencia aceptada por su política:

```text
INVOICE
RECEIPT
OTHER
```

No convertir el núcleo en un sistema tributario completo.

### Direct Purchase — flujo y límites

```text
Purchase Request
↓
Department Approval
↓
Policy Evaluation
↓
Automatic Budget Check
↓
Budget Reservation
↓
DIRECT_PURCHASE_AUTHORIZED
↓
Requester o comprador designado realiza la compra
↓
Invoice / Receipt
↓
Goods Receipt o Acceptance
↓
Document validated
↓
Budget consumed
↓
AP prepares payment
↓
PAYMENT_APPROVER
↓
External payment recorded
```

Reglas:

- la compra no se realiza antes de `DIRECT_PURCHASE_AUTHORIZED`;
- el proveedor debe estar `ACTIVE`, aunque Procurement no participe en la transacción;
- omitir la PO no elimina recepción, evidencia, matching policy ni auditoría;
- employee reimbursements y corporate cards están fuera de Release 1;
- una compra realizada sin autorización previa requiere un proceso de excepción y no se convierte automáticamente en LOW_VALUE.

La matching policy inicial para Direct Purchase es de dos vías:

```text
Direct Purchase Authorization
+ Receipt / Supplier Invoice
+ Goods Receipt or Acceptance
```

Un importe superior al autorizado bloquea el pago y requiere reevaluación o excepción.

---

## 91. Approved Supplier Catalog

Para compras estándar puede existir un proveedor previamente homologado.

Ejemplo:

```text
Monitor Dell

Approved Supplier:
ABC Tech

Contract:
ACTIVE

Negotiated Price:
S/900
```

En ese caso:

```text
RFQ not required
```

según política.

`Contract: ACTIVE` significa que existe una referencia a un acuerdo comercial administrado fuera de la plataforma. Release 1 puede conservar:

```text
External Contract Reference
Valid From
Valid To
Status
Attachment
```

Esto permite usar precios negociados y omitir RFQ según política sin introducir cláusulas, negociación, firmas o Contract Lifecycle Management.

---

## 92. Flujo canónico

```text
REQUESTER
creates Purchase Request
        │
        ▼
DEPARTMENT_APPROVER
¿Lo necesitamos?
        │
        ▼
POLICY ENGINE
¿Qué controles requiere?
        │
        ├─────────────┬──────────────┐
        ▼             ▼              ▼
FINANCE_APPROVER  IT_REVIEWER  LEGAL_REVIEWER
        │             │              │
        └─────────────┴──────────────┘
                      │
                      ▼
               BUDGET RESERVED
                      │
                      ▼
              PROCUREMENT_BUYER
                      │
                      ▼
                 SOURCING / RFQ
               if required
                      │
                      ▼
               SUPPLIER SELECTED
                      │
                      ▼
                      PO
                      │
                      ▼
                   SUPPLIER
                      │
             ┌────────┴─────────┐
             ▼                  ▼
        FULFILLMENT          INVOICE
             │                  │
             └─────────┬────────┘
                       ▼
                MATCHING ENGINE
                       │
                  ┌────┴────┐
                  ▼         ▼
               MATCHED   EXCEPTION
                  │         │
                  ▼         ▼
              AP_SPECIALIST
                  │
                  ▼
            PAYMENT PREPARED
                  │
                  ▼
            PAYMENT_APPROVER
                  │
                  ▼
                PAYMENT
```

El diagrama representa el camino con Procurement. LOW_VALUE puede desviarse después de `BUDGET RESERVED` hacia `DIRECT_PURCHASE_AUTHORIZED`. Una PO mueve el importe correspondiente de `RESERVED` a `COMMITTED`; una factura habilitada para pago lo mueve de `COMMITTED` a `CONSUMED`.

---

## 93. Fulfillment por tipo

### GOOD

```text
PO
↓
GOODS_RECEIPT
```

### SERVICE

```text
PO
↓
SERVICE_ACCEPTANCE
```

### SUBSCRIPTION

```text
Purchase / Contract
↓
IT_PROVISIONER
↓
SUBSCRIPTION_ACTIVATION
↓
USER_ACCESS_CONFIRMATION
```

---

## 94. Ejemplo completo — Laptop

```text
Pablo

Department:
Software / IT

System Role:
REQUESTER

        ↓

PR-2026-00492

Line:
Laptop development

Purchase Type:
GOOD

Spend Category:
HARDWARE

Requirements:
32 GB RAM
1 TB SSD
Dedicated GPU

Estimated:
S/6,500

Cost Center:
CC-IT-DEV

        ↓

Carlos
DEPARTMENT_APPROVER

Question:
¿Es necesaria?

APPROVED

        ↓

Budget Check

CC-IT-DEV / Hardware

Available:
S/37,000

        ↓

Lucía
FINANCE_APPROVER

APPROVED

Reserved:
+ S/6,500

        ↓

Miguel
PROCUREMENT_BUYER

RFQ

Supplier A
Supplier B
Supplier C

        ↓

Quote Evaluation

Supplier B selected

Final Price:
S/6,400

        ↓

PO-2026-00142
S/6,400

Status:
ISSUED

Reserved:
- S/6,500

Committed:
+ S/6,400

Available:
+ S/100 returned

        ↓

Supplier delivers

        ↓

Acceptance Owner:
Pablo

GOODS_RECEIPT

1 ordered
1 received
1 accepted

        ↓

Supplier Invoice
S/6,400

        ↓

MATCHING ENGINE

PO        ✓
Receipt   ✓
Invoice   ✓

MATCHED

        ↓

Committed:
- S/6,400

Consumed:
+ S/6,400

        ↓

AP_SPECIALIST

READY_FOR_PAYMENT

        ↓

PAYMENT_APPROVER

APPROVED

        ↓

Payment:
SCHEDULED

        ↓

Payment:
PAID
```

---

## 95. Ejemplo completo — SaaS / AI coding assistant

```text
Pablo

Department:
Software / IT

System Role:
REQUESTER

        ↓

Purchase Request

Purchase Type:
SUBSCRIPTION

Spend Category:
SOFTWARE

Need:
AI assistance for coding

Preferred Product:
ChatGPT Business

Estimated Cost:
Configured from current offering

Cost Center:
CC-IT-TOOLS

        ↓

DEPARTMENT_APPROVER

APPROVED

        ↓

Budget / Finance approval
according to policy

        ↓

IT_REVIEWER

Security
Data handling
Existing alternatives
Compatibility

APPROVED

        ↓

PROCUREMENT_BUYER

Check:

Existing catalog?
Existing vendor agreement?
Required product?
Preferred product?
Alternative sourcing required?

        ↓

If alternative proposed:

[ ACCEPT_ALTERNATIVE ]
[ KEEP_ORIGINAL_REQUIREMENT ]

        ↓

PO / Commercial Purchase

Payment Terms:
PREPAID

Committed:
+ approved amount

        ↓

Supplier Invoice

        ↓

PREPAID MATCHING POLICY

Invoice approved for payment

Consumed:
+ approved invoice amount

        ↓

AP_SPECIALIST
prepares payment

        ↓

PAYMENT_APPROVER

APPROVED

        ↓

External Payment Result:
PAID

        ↓

IT_PROVISIONER

Create/manage workspace
Assign seat
Register activation

        ↓

SUBSCRIPTION_ACTIVATION

        ↓

Pablo

✓ ACCESS CONFIRMED

        ↓

Subscription Registry

Status:
ACTIVE

Cost Center:
CC-IT-TOOLS

Billing Cycle:
...

Renewal:
...

Owner:
Pablo
```

Vendor-specific rules must remain configurable and must not be hardcoded.

---

## 96. Caso de referencia — Cafetera LOW_VALUE

```text
REQUESTER
↓
Purchase Request

Coffee machine
S/350

Type:
GOOD

Category:
OFFICE_SUPPLIES

        ↓

DEPARTMENT_APPROVER

APPROVED

        ↓

Automatic Budget Check

✓ Funds available

        ↓

Budget Reservation

        ↓

DIRECT_PURCHASE_AUTHORIZED

        ↓

DIRECT PURCHASE

Active Supplier

        ↓

Supporting Document

        ↓

Goods Receipt / confirmation

        ↓

Document validated

        ↓

Budget consumed

        ↓

AP prepares payment

        ↓

PAYMENT_APPROVER

        ↓

External payment recorded
```

No necesita:

- tres proveedores;
- RFQ;
- Procurement formal;
- aprobación financiera de alto nivel;
- flujo de alto valor.

---

## 97. Caso de referencia — 50 laptops HIGH_VALUE

```text
REQUESTER
        ↓
DEPARTMENT_APPROVER
        ↓
FINANCE_APPROVER
with required Approval Authority
        ↓
PROCUREMENT_BUYER
        ↓
RFQ
3+ valid quotations
        ↓
Quote Evaluation
        ↓
Supplier Selection
        ↓
PROCUREMENT_APPROVER
if policy requires
        ↓
PO
        ↓
Delivery
        ↓
Goods Receipt
        ↓
Supplier Invoice
        ↓
Three-Way Match
        ↓
AP prepares payment
        ↓
PAYMENT_APPROVER
        ↓
Payment
```

---

## 98. Caso de referencia — Entrega parcial

```text
PO:
5 laptops

Delivery:
4 laptops

Goods Receipt:
Accepted 4
Outstanding 1

Status:
PARTIALLY_RECEIVED
```

Después:

```text
Replacement / remaining unit arrives

+1 accepted

Status:
FULLY_RECEIVED
```

---

## 99. Caso de referencia — Producto dañado

```text
PO:
5 laptops

Received:
5

Accepted:
4

Rejected:
1

Reason:
DAMAGED_ITEM
```

Resolución seleccionada:

```text
SUPPLIER_ISSUE_OPENED
↓
REPLACEMENT_EXPECTED
```

La factura no debe aprobarse automáticamente si cobra unidades no aceptadas.

---

## 100. Caso de referencia — Factura incorrecta

```text
PO:
S/25,000

Goods Receipt:
5 accepted

Invoice:
S/27,000

Matching Engine:
PRICE_MISMATCH

Payment:
BLOCKED
```

AP solicita resolución.

---

## 101. Caso de referencia — FINANCE_APPROVER como requester

```text
Pedro

Department:
Finance

Roles:
REQUESTER
FINANCE_APPROVER

        ↓

Pedro creates request

        ↓

Pedro excluded from approval tasks

        ↓

Another DEPARTMENT_APPROVER

        ↓

Another FINANCE_APPROVER
```

No autoaprobación.

---

## 102. Audit Trail

Debe existir trazabilidad de decisiones.

Ejemplo:

```text
10:32 Pablo created PR-00492
11:04 Carlos approved business need
12:12 Finance requested changes
13:02 Pablo updated request
14:41 Finance approved
15:03 Budget reserved
16:20 Procurement created RFQ
...
```

Registrar:

- actor;
- timestamp;
- action;
- previous state;
- new state;
- reason/comments;
- relevant document;
- document version;
- policy/configuration version cuando afecte la decisión;
- actor type: `USER`, `SYSTEM` o `EXTERNAL_RESULT`;
- correlation reference del proceso.

El Audit Trail es append-only desde la perspectiva funcional. No se elimina ni reescribe para ocultar una decisión anterior. Las correcciones generan eventos nuevos enlazados con el evento o documento corregido.

---

## 103. Nombre definitivo

**Procure-to-Pay Management Platform**

Describe el ciclo:

```text
Purchase Request
↓
Approvals
↓
Procurement
↓
Purchase Order
↓
Fulfillment
↓
Invoice
↓
Payment
```

---

## 104. Spend Management

`Spend Management` es un concepto más amplio.

Cuando el proyecto incorpore:

- advanced budget analytics;
- forecasts;
- subscription optimization;
- supplier performance;
- spend trends;
- contract insights;

puede describirse como:

> **Procure-to-Pay Platform with Spend Management capabilities**

Pero el dominio central sigue siendo P2P.

---

## 105. Decisiones cerradas

Las siguientes decisiones se consideran aceptadas para la siguiente fase.

### Decisión 1 — Purchase Request multilínea

Sí.

Una PR puede contener múltiples líneas.

---

### Decisión 2 — Cost Center por línea

Sí.

Cada línea puede usar su propio Cost Center.

---

### Decisión 3 — Budget por categoría

Sí.

Modelo conceptual:

```text
Cost Center
+
Fiscal Year
+
Spend Category
```

---

### Decisión 4 — PROCUREMENT_APPROVER

Sí.

Se incorpora para:

- POs de alto importe;
- quotation waivers;
- excepciones de Procurement;
- determinadas operaciones comerciales.

---

### Decisión 5 — LEGAL_REVIEWER condicional

Sí.

Existe como control condicional, sin construir Contract Management completo inicialmente.

---

### Decisión 6 — Cost Center Owner

Sí.

Es una asignación/responsabilidad sobre un Cost Center.

No es un System Role.

Proporciona visibilidad/monitoreo, no aprobación financiera automática.

---

### Decisión 7 — Approval Authority

Los Job Titles no se utilizan para enrutar aprobaciones. Una persona resulta elegible mediante System Role, Approval Authority suficiente, scope aplicable, vigencia y ausencia de conflictos de Segregation of Duties.

---

### Decisión 8 — Unidad funcional de Purchase Request

La Purchase Request es un contenedor. La Purchase Request Line es la unidad principal de política, presupuesto, aprobación y trazabilidad.

---

### Decisión 9 — Cardinalidad PR / PO

Una PR puede producir varias POs. Una línea no se divide entre proveedores en Release 1 y una PO no mezcla varias PRs.

---

### Decisión 10 — Budget mediante movimientos

`REQUESTED`, `RESERVED`, `COMMITTED` y `CONSUMED` son magnitudes acumulables calculadas desde movimientos auditables, no un único estado excluyente.

---

### Decisión 11 — Policy Engine versionado

Las políticas se evalúan en scopes `LINE`, `REQUEST` y `SOURCING_PO`, se versionan y conservan su input snapshot. Los controles compatibles se acumulan y prevalece el requisito más estricto.

---

### Decisión 12 — Cambios materiales

Los cambios materiales crean una nueva versión o amendment, invalidan tareas afectadas, vuelven a evaluar políticas y ajustan presupuesto de forma auditable.

---

### Decisión 13 — Pago en Release 1

AP prepara el pago y `PAYMENT_APPROVER` lo aprueba. La plataforma registra el resultado externo, pero no mueve fondos ni se integra con bancos.

---

### Decisión 14 — Direct Purchase

La compra directa requiere autorización previa, proveedor activo, evidencia y recepción/aceptación. Employee reimbursements y corporate cards quedan fuera de Release 1.

---

### Decisión 15 — Contratos y suscripciones

Los contratos son referencias externas. Release 1 cubre adquisición y activación de subscriptions, pero no Contract Lifecycle Management ni renovación automática.

---

### Decisión 16 — Moneda y evaluación presupuestaria

Cada documento conserva moneda transaccional y equivalente en la moneda base de la organización. Presupuesto y thresholds utilizan el total bruto evaluado en moneda base.

---

## 106. Otras decisiones consolidadas

También se consideran acordadas:

- utilizar únicamente `Department`, no alternar con `Area`;
- separar Department, Job Title, System Role, Process Responsibility y Approval Authority;
- `CFO` como Job Title;
- `Procurement Manager` como Job Title;
- eliminar `EMPLOYEE` como rol del sistema;
- usar `REQUESTER`;
- usar `DEPARTMENT_APPROVER`;
- usar `FINANCE_APPROVER`, no `FINANCE`;
- `AP_SPECIALIST` como rol de Accounts Payable;
- `PAYMENT_APPROVER` separado de la preparación del pago;
- `AUDITOR` como rol de solo lectura separado de `ADMIN`;
- Finance sigue siendo Department;
- Procurement sigue siendo Department;
- múltiples Cost Centers por Department;
- grupo jerárquico superior no imputable;
- no usar un CC general ambiguo;
- Purchase Types iniciales GOOD, SERVICE, SUBSCRIPTION;
- Spend Category separado de Purchase Type;
- Budget visibility limitada;
- Budget prechecks automáticos;
- APPROVE / REJECT / REQUEST_CHANGES;
- requester != approver;
- segregation of duties;
- RFQ deadlines;
- late quotations;
- EXTEND_RFQ;
- QUOTATION_WAIVER;
- Supplier Performance Score separado de Quote Evaluation Score;
- PO como compromiso comercial;
- no rol RECEIVER inicialmente;
- Acceptance Owner por compra;
- Goods Receipt para GOOD;
- Service Acceptance para SERVICE;
- Subscription Activation para SUBSCRIPTION;
- recepción parcial;
- productos dañados/rechazados;
- IT_PROVISIONER para SaaS;
- user access confirmation;
- Vendor Terms configurables;
- IT_REVIEWER humano;
- preferred vs required product;
- ACCEPT_ALTERNATIVE / KEEP_ORIGINAL_REQUIREMENT;
- Supplier Invoice desde perspectiva del comprador;
- SUNAT fuera del núcleo;
- Matching Engine automático;
- AP no modifica documentos de origen;
- Payment Terms configurables;
- Policy Engine configurable;
- LOW_VALUE no pasa por burocracia innecesaria;
- controles IT/Legal pueden dispararse por riesgo aunque el monto sea bajo.

---

## 107. Extensiones futuras — NO implementar todavía

### SUNAT

Posibles funcionalidades futuras:

- RUC validation;
- comprobante validation;
- PDF/XML;
- fiscal status checks.

---

### Warehouse / Inventory

Posible proyecto/extensión:

- warehouses;
- receiving clerk;
- stock;
- locations;
- bins;
- inventory movements;
- transfers;
- picking.

---

### Project Dimension

Permitir asociar gastos a un futuro sistema independiente de Project Management.

No construir Project Management dentro del P2P.

---

### Contract Management

Release 1 conserva únicamente referencia externa, vigencia, status y attachment del acuerdo. La gestión contractual completa queda para el futuro:

- authoring y negociación de contracts;
- administración estructurada de terms;
- obligations y alertas contractuales;
- e-signatures;
- contract repository;
- clause workflows.

---

### Advanced Spend Analytics

Futuro:

- spend by department;
- spend by CC;
- spend by category;
- vendor concentration;
- monthly trends;
- budget burn rate;
- savings;
- avoided costs.

---

### Forecasting

Futuro:

- projected spend;
- end-of-year forecast;
- planned commitments;
- budget exhaustion warnings.

---

### Budget Transfers / Adjustments

Futuro:

```text
Transfer budget
CC-IT-DEV
→
CC-IT-INFRA
```

con aprobación y auditoría.

---

### Supplier Portal

Futuro:

- supplier login;
- RFQ responses;
- PO acknowledgement;
- invoice upload;
- delivery updates.

---

### Subscription Optimization

Futuro:

- unused seats;
- upcoming renewals;
- price changes;
- consolidation opportunities;
- cancellation recommendations.

---

### Notifications

Futuro/iteración posterior:

- pending approvals;
- RFQ deadline;
- invoice discrepancy;
- budget threshold;
- upcoming renewal;
- payment due;
- delayed supplier.

La entrega de notificaciones por email, chat, push o integraciones externas es futura. La bandeja interna de tareas pendientes forma parte del núcleo de Release 1 para que las aprobaciones y acciones puedan ejecutarse sin depender de esos canales.

---

## 108. Casos que conviene recorrer antes de las SPEC técnicas

Antes de diseñar base de datos o endpoints, validar de punta a punta al menos estos escenarios:

1. Cafetera LOW_VALUE.
2. Laptop CONTROLLED.
3. 50 laptops HIGH_VALUE.
4. SaaS catalogado.
5. SaaS no catalogado con alternativa propuesta.
6. Servicio de consultoría con MILESTONE.
7. Entrega parcial.
8. Producto dañado.
9. Factura con PRICE_MISMATCH.
10. Factura con QUANTITY_MISMATCH.
11. RFQ con proveedor tardío.
12. RFQ sin número mínimo de cotizaciones.
13. QUOTATION_WAIVER.
14. FINANCE_APPROVER solicitando una compra.
15. Cancelación cuando está RESERVED.
16. Cancelación/modificación cuando está COMMITTED.
17. Purchase Request con múltiples líneas y múltiples Cost Centers.
18. Compra LOW_VALUE que igualmente necesita IT_REVIEW.
19. Compra con LEGAL_REVIEW condicional.
20. PO que requiere PROCUREMENT_APPROVER.
21. PR con líneas de Departments distintos y approvals independientes.
22. Precio final de PO superior a la reserva original.
23. Factura parcial seguida de Payment parcial.
24. Cambio de FX entre solicitud, reserva, PO e invoice.
25. Approval Requirement sin persona elegible.
26. Cambio de Banking Details del proveedor con doble control.
27. Payment externo con resultado FAILED y reintento.
28. Direct Purchase cuyo documento supera el importe autorizado.
29. Cambio material de una línea después de aprobada.
30. SaaS PREPAID pagado cuya activación queda pendiente o falla.

El objetivo es descubrir contradicciones antes de modelar técnicamente el sistema.

**Resultado de revisión funcional v2:** los 30 escenarios tienen un recorrido determinado, una resolución controlada o un límite explícito de Release 1. Las SPECs convertirán cada recorrido aplicable en criterios de aceptación verificables.

---

## 109. Registro de cierre funcional para las SPECs

Esta sección conserva las preguntas surgidas durante la iteración en ChatGPT Work y documenta dónde queda resuelta cada una. No quedan decisiones estructurales en estado `OPEN`.

### CLOSED_IN_FOUNDATION

- **Aprobación multilínea:** las decisiones se conservan por línea; la UI puede agrupar en una tarea líneas con ruta y decisión idénticas.
- **Una PR en varias POs:** permitido.
- **Una línea entre varios proveedores:** no permitido en Release 1.
- **Moneda distinta al budget:** se conserva moneda transaccional y equivalente congelado en moneda base.
- **Aprobación de matching exception:** requiere Approval Authority `MATCH_EXCEPTION`; AP presenta la excepción, no la autoaprueba.
- **Taxes:** presupuesto y thresholds usan total bruto; matching mantiene componentes separados.
- **CONSUMED para PREPAID:** ocurre al reconocer la obligación válida para pago según la matching policy, aunque la activación permanezca pendiente.
- **Autoridad de approvers:** System Role + Approval Authority + scope + vigencia + ausencia de conflicto.
- **Reglas coincidentes:** los controles se acumulan y prevalece el requisito más estricto.
- **Secuencia de aprobaciones:** modelo mixto. Department Approval ocurre primero; Finance, IT y Legal pueden ejecutarse en paralelo cuando son independientes; Procurement comienza al completar todos los controles previos; Procurement Approval ocurre antes de emitir la PO cuando la política lo exige; Payment Approval ocurre después de habilitar la factura para pago.
- **Cost Center Owner:** acceso inicial de lectura, monitoreo y analytics sobre sus Cost Centers; no modifica presupuesto ni obtiene aprobación financiera automática.
- **Department Approver no-owner:** puede ver la decisión de suficiencia necesaria para aprobar, pero no el saldo detallado del Cost Center.

### DEFERRED_TO_FEATURE_SPEC

- **Tolerancias de matching:** serán configurables por tipo, categoría o política; la Matching SPEC definirá estructura, defaults y límites.
- **Cancelación parcial de PO:** el principio de amendment y liberación proporcional está cerrado; la PO SPEC definirá todas las transiciones.
- **Estados exactos:** los ciclos principales están cerrados en la sección 0.6; cada SPEC definirá substates y estados técnicos auxiliares.
- **Vacaciones/delegaciones:** la Approval SPEC definirá UX, vigencia y reasignación; nunca se delega autoridad superior a la que posee el delegado y toda delegación queda auditada.
- **Acceptance Owner automático:** la Fulfillment SPEC definirá el algoritmo; debe quedar asignado antes de emitir la PO y puede partir de `Requested For`.
- **Compra recurrente con acuerdo vigente:** el acuerdo puede evitar RFQ, pero no omite controles presupuestarios o de riesgo; la Procurement SPEC detallará el recorrido.
- **Renewal approval:** la Subscription SPEC definirá el workflow `RENEW | REDUCE | CANCEL`; no existe renovación automática en Release 1.

### OUT_OF_RELEASE_1

- employee reimbursements;
- corporate cards;
- devoluciones y credit notes posteriores al pago;
- emergency purchases con bypass del flujo normal;
- integración bancaria y ejecución real de pagos;
- renovación automática de subscriptions.

---

## 110. Principio para las siguientes fases SDD

Antes de una SPEC técnica:

```text
Understand business rule
        ↓
Define actors
        ↓
Define inputs
        ↓
Define states
        ↓
Define allowed transitions
        ↓
Define exceptions
        ↓
Define acceptance criteria
        ↓
THEN
technical design
```

No inventar tablas, endpoints o servicios antes de cerrar las reglas de negocio relevantes.

### 110.1 Orden recomendado de SPECs

```text
1. Organization, Identity, Roles and Approval Authority
2. Approval Workflow and Policy Engine
3. Cost Centers, Budgets and Budget Movements
4. Purchase Requests and Purchase Request Lines
5. Supplier Master and Approved Catalog
6. Sourcing, RFQ, Quotations and Award
7. Purchase Orders and Amendments
8. Fulfillment: Goods, Services and Subscriptions
9. Supplier Invoices and Matching
10. Payment Preparation, Approval and External Result
11. Audit Trail and operational reporting
```

Las dependencias funcionales deben cerrarse en ese orden aunque la implementación técnica posterior agrupe o reorganice componentes.

---

## 111. Alcance de este documento

Este archivo es una **fuente de verdad funcional**, no una especificación técnica final.

Debe utilizarse como contexto para:

- ChatGPT Work;
- Codex;
- Claude Code;
- SDD;
- generación de SPECs;
- revisión del dominio;
- validación de flujos.

Si una futura conversación contradice una decisión marcada como cerrada, deberá actualizarse este documento explícitamente en lugar de mantener ambas versiones.

---

## 112. Resumen ejecutivo del dominio

```text
Employee need
        ↓
Purchase Request
        ↓
Department validation
        ↓
Budget / Policy validation
        ↓
Conditional reviews
        ↓
Procurement
        ↓
RFQ / Sourcing
        ↓
Supplier Selection
        ↓
PO
        ↓
Fulfillment
        ↓
Supplier Invoice
        ↓
Matching
        ↓
Accounts Payable
        ↓
Payment
        ↓
Analytics / Audit
```

La plataforma debe ser suficientemente flexible para que:

- una cafetera de S/350 no recorra la misma burocracia que 50 laptops;
- una suscripción SaaS no se trate como una caja física;
- una compra de bajo importe pueda necesitar revisión de seguridad;
- una compra grande pueda necesitar Financial Approval Authority elevada y Procurement Approval;
- un empleado de Finanzas pueda solicitar sin autoaprobarse;
- los Cost Centers permitan entender dónde se gasta;
- Procurement pueda justificar excepciones;
- AP pueda bloquear facturas discrepantes;
- Supplier Performance se calcule usando hechos reales;
- el sistema conserve trazabilidad de todas las decisiones.

Estado de cierre:

```text
FUNCTIONAL FOUNDATION: CLOSED
READY FOR FEATURE SPECS: YES
READY FOR IMPLEMENTATION: AFTER EACH FEATURE SPEC IS APPROVED
```

---

**Fin del documento funcional consolidado.**
