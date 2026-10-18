# Habita — Especificación funcional y reglas de negocio

**Versión:** 1.0  
**Estado:** Borrador para validación antes del desarrollo  
**Producto:** SaaS de administración de propiedades en renta  
**Stack objetivo:** Next.js + TypeScript, ASP.NET Core, PostgreSQL y Entity Framework Core

## 1. Propósito

Este documento es la fuente de verdad funcional para diseño, desarrollo, pruebas y revisión de cambios. Define el alcance del MVP, reglas de negocio, permisos, entidades, procesos y criterios de aceptación. No se deben inventar políticas financieras durante la implementación: las decisiones pendientes deben aprobarse y documentarse.

## 2. Alcance del MVP

### Incluido
- Organizaciones y usuarios con roles.
- Propiedades y unidades rentables.
- Inquilinos y datos de contacto.
- Contratos de arrendamiento.
- Cargos de renta y cargos manuales.
- Registro de pagos completos y parciales.
- Aplicación de pagos a cargos.
- Saldos, vencimientos e indicadores financieros.
- Incidencias de mantenimiento.
- Historial de auditoría para operaciones sensibles.
- Reportes básicos y exportación CSV.

### Fuera del alcance inicial
- Contabilidad fiscal completa, CFDI y facturación electrónica.
- Conciliación bancaria automática y cobros automáticos.
- Firma electrónica avanzada.
- Cálculo automático de intereses moratorios o penalizaciones.
- Portal completo del inquilino y aplicación móvil.
- Integraciones de WhatsApp/SMS.
- Administración de condominios, amenidades, accesos o cuotas vecinales; son módulos diferentes.

## 3. Principios obligatorios

1. **Aislamiento por organización:** cada registro de negocio pertenece a una organización. La API obtiene la organización autorizada desde la identidad autenticada y no confía en un `OrganizationId` enviado por el frontend.
2. **La API es la autoridad:** las reglas financieras y permisos se validan en ASP.NET Core, no solo en la interfaz.
3. **Dinero exacto:** usar `decimal` en .NET y `numeric` en PostgreSQL; nunca `float`/`double` para importes monetarios. La moneda debe estar definida explícitamente.
4. **Auditoría:** no borrar físicamente operaciones financieras confirmadas. Corregir mediante reversos o ajustes auditables.
5. **Transacciones atómicas:** registrar un pago y sus asignaciones como una sola operación de base de datos.
6. **Fechas:** almacenar instantes técnicos en UTC; las fechas contractuales y vencimientos son fechas locales sin hora cuando corresponda. La zona horaria se configura por organización.
7. **Idempotencia:** reintentos de operaciones sensibles no deben duplicar pagos ni cargos.
8. **Privacidad:** minimizar datos personales y limitar el acceso por rol y organización.
9. **Estados explícitos:** los estados críticos no deben depender de texto libre.
10. **Cambios de reglas:** cualquier regla aprobada nueva actualiza este documento y sus pruebas.

## 4. Roles y permisos

### Owner
Administra todos los datos de su organización, la configuración y los usuarios. No puede acceder a otras organizaciones salvo membresía explícita.

### Admin
Administra propiedades, unidades, inquilinos, contratos, cargos, pagos e incidencias. No puede transferir la propiedad de la organización.

### Finance
Consulta propiedades y contratos; genera cargos, registra pagos, ve reportes financieros y solicita ajustes. No administra usuarios.

### Maintenance
Consulta incidencias y datos operativos mínimos de ubicación/unidad; actualiza incidencias asignadas. No puede consultar importes de renta, pagos ni información financiera.

### ReadOnly
Consulta información permitida sin modificarla.

### Reglas de autorización
- Toda solicitud privada requiere autenticación.
- Todo recurso debe validarse contra la organización actual; conocer un UUID no concede acceso.
- Ocultar botones en frontend no es una medida de seguridad.
- Solo Owner/Admin pueden invitar, desactivar o cambiar roles de usuarios.
- Las operaciones financieras requieren Owner, Admin o Finance.
- Las acciones no autorizadas responden `403`; para recursos ajenos puede usarse `404` para no revelar su existencia.
- La desactivación de un usuario no elimina el historial que haya creado.

## 5. Entidades y campos

Todas las entidades de negocio deben tener `Id` (UUID), `OrganizationId`, fechas de creación/modificación apropiadas y restricciones de integridad. `OrganizationId` debe estar presente en las tablas de negocio para facilitar el aislamiento.

### Organization
- `Id`, `Name`, `Slug`, `Currency`, `TimeZone`, `Status`, `CreatedAt`.
- Moneda inicial sugerida: MXN.
- `Slug` único globalmente.
- Una organización suspendida no puede realizar escrituras.

### User / Membership
- Identidad autenticable y relación con organización/rol.
- Email normalizado.
- La política para usuarios que pertenecen a varias organizaciones debe definirse antes de producción.

### Property
- `Id`, `OrganizationId`, `Name`, `Address`, `Notes`, `Status`.
- Estados: `Active`, `Archived`.
- Una propiedad archivada conserva su historial y no admite nuevos contratos.

### Unit
- `Id`, `OrganizationId`, `PropertyId`, `Code`, `Type`, `Bedrooms` opcional, `BaseRent`, `Status`.
- Estados: `Available`, `Occupied`, `Maintenance`, `Archived`.
- `Code` es único dentro de una propiedad.
- No puede haber contratos activos que se solapen para una misma unidad.
- La ocupación debe derivarse de los contratos vigentes o actualizarse de forma transaccional.

### Tenant
- `Id`, `OrganizationId`, `FullName`, `Email` opcional, `Phone` opcional, `Notes`, `Status`.
- No se elimina físicamente si tiene contratos o movimientos asociados; se archiva.
- No almacenar identificaciones oficiales u otros datos sensibles si no son necesarios.

### Lease
- `Id`, `OrganizationId`, `UnitId`, `TenantId`, `StartDate`, `EndDate`, `MonthlyRent`, `DueDay`, `DepositAmount` opcional, `Status`, `TermsNotes`.
- Estados: `Draft`, `Active`, `Ended`, `Cancelled`.
- `StartDate <= EndDate`, si hay fecha final.
- `MonthlyRent >= 0`.
- `DueDay` entre 1 y 28 para evitar ambigüedad en meses cortos.
- Solo puede existir un contrato activo por unidad para un mismo periodo.
- La renta acordada se copia al cargo generado; cambiar el contrato no modifica cargos históricos.
- Por defecto, no se prorratea automáticamente la renta al entrar o salir a mitad de mes.

### RentCharge
- `Id`, `OrganizationId`, `LeaseId`, `PeriodYear`, `PeriodMonth`, `Description`, `Amount`, `DueDate`, `Status`, `CreatedAt`.
- Tipos iniciales: `Rent`, `Other`.
- Estados posibles: `Open`, `PartiallyPaid`, `Paid`, `Overdue`, `Voided`.
- No duplicar cargo de renta para el mismo contrato y periodo: imponer restricción única en base de datos.
- Importe no negativo. Descuentos se registran como ajustes explícitos y auditables.
- Un cargo es vencido si tiene saldo pendiente y la fecha local actual de la organización es posterior a `DueDate`.
- Solo se anula con permiso y motivo obligatorio. Si tiene pagos aplicados, primero deben revertirse o reasignarse esas aplicaciones.

### Payment
- `Id`, `OrganizationId`, `TenantId`, `ReceivedAt`, `Amount`, `Method`, `Reference`, `Notes`, `Status`, `CreatedBy`, `IdempotencyKey` opcional.
- Métodos: `Cash`, `BankTransfer`, `Card`, `Other`.
- Estados: `Confirmed`, `Reversed`.
- `Amount > 0`.
- Puede aplicarse a uno o varios cargos autorizados del mismo inquilino.
- La suma de asignaciones activas no puede superar el importe del pago.
- Un pago puede tener saldo no asignado; debe mostrarse claramente como saldo a favor.
- Revertir requiere permiso y motivo, revierte las asignaciones de forma atómica y deja auditoría.
- No editar el importe de un pago confirmado. Para corregirlo, revertir y registrar otro.

### PaymentAllocation
- `Id`, `OrganizationId`, `PaymentId`, `RentChargeId`, `Amount`, `CreatedAt`, `ReversedAt` opcional.
- `Amount > 0`.
- Pago y cargo deben pertenecer a la misma organización.
- No superar el saldo pendiente del cargo ni el saldo no asignado del pago.
- Validar límites dentro de una transacción para prevenir carreras concurrentes.
- No asignar a cargos anulados ni de otra organización.

### MaintenanceIssue
- `Id`, `OrganizationId`, `PropertyId`, `UnitId` opcional, `Title`, `Description`, `Priority`, `Status`, `ReportedAt`, `AssignedToUserId` opcional, `ResolvedAt` opcional.
- Prioridades: `Low`, `Normal`, `High`, `Urgent`.
- Estados: `Open`, `InProgress`, `Waiting`, `Resolved`, `Closed`.
- Una incidencia resuelta requiere fecha y usuario que la resolvió.
- Conservar historial de cambios de estado.

### AuditLog
- `Id`, `OrganizationId`, `ActorUserId`, `Action`, `EntityType`, `EntityId`, `OccurredAt`, `Reason` opcional, `Metadata` sin secretos.
- Auditar pagos, reversos, cambios de contratos, cargos, roles, configuración y archivado.
- Nunca registrar contraseñas, tokens ni secretos.
- Los usuarios normales no pueden modificar ni eliminar auditoría.

## 6. Reglas financieras

### Saldo pendiente de un cargo
`SaldoPendiente = ImporteCargo - SumaAsignacionesActivas`

- Nunca debe ser negativo.
- Saldo cero significa `Paid`.
- Saldo mayor que cero y asignaciones mayores que cero significa `PartiallyPaid`, salvo que esté vencido.
- Saldo mayor que cero y fecha actual posterior a vencimiento significa `Overdue`.
- Si no tiene asignaciones y no está vencido, significa `Open`.
- El servidor es responsable de calcular el estado definitivo.

### Saldo no asignado de un pago
`SaldoNoAsignado = ImportePago - SumaAsignacionesActivas`

- Nunca debe ser negativo.
- No se considera automáticamente pago de meses futuros.
- Se aplica a cargos solo mediante una acción explícita autorizada.

### Ingresos y reportes
- Ingresos recibidos se calculan por fecha de recepción del pago.
- Renta esperada se calcula por cargos generados en el periodo.
- No confundir flujo de efectivo con renta cargada.
- Mostrar por separado pagos revertidos, pagos recibidos, cargos generados y saldo pendiente.
- Totales calculados en backend con filtros por organización, fechas y permisos.

### Generación de cargos mensuales
- Usuario autorizado solicita una vista previa para un periodo.
- Solo considerar contratos aplicables al periodo según vigencia.
- La generación es idempotente; ejecutarla dos veces no crea duplicados.
- El vencimiento se calcula con `DueDay` y el periodo del cargo.
- El MVP no prorratea automáticamente.
- Un cambio posterior de renta no modifica cargos existentes.
- Debe existir restricción única en base de datos, no solo una comprobación de frontend.

### Pagos parciales y aplicación
- Al registrar un pago, el usuario selecciona cargos y montos a aplicar.
- La suma aplicada no puede superar el importe del pago.
- La asignación a cada cargo no puede superar su saldo pendiente.
- No aplicar automáticamente el saldo sobrante a cargos futuros.
- Por defecto, un pago se aplica a cargos del mismo inquilino; cualquier excepción exige flujo explícito y auditado.
- Pago, asignaciones y auditoría relevante se guardan de forma consistente.

### Ajustes, descuentos y depósitos
- Todo descuento o corrección tiene importe, motivo, usuario y fecha.
- No implementar mora automática en el MVP.
- El depósito es dinero **en custodia**: se cobra en abonos (cargos de tipo `Deposit`), no cuenta como ingreso y se liquida al terminar el contrato (ver «Reglas de datos del negocio»).
- No permitir importes negativos como mecanismo implícito de descuento.

## 7. Flujos de negocio

### Alta de propiedad y unidad
1. Usuario autorizado crea propiedad.
2. Registra unidades con códigos únicos.
3. El servidor valida la organización y las relaciones.
4. Se registra auditoría.
5. La unidad queda disponible si no tiene contrato vigente.

### Alta y activación de contrato
1. Crear o seleccionar inquilino.
2. Seleccionar unidad y vigencia.
3. Capturar renta mensual, día de vencimiento y condiciones.
4. Validar fechas y solapamientos.
5. Guardar borrador o activar.
6. Al activar, calcular ocupación y registrar auditoría.
7. Generar cargos mediante flujo separado con vista previa.

### Registro de pago
1. Seleccionar inquilino y cargos abiertos.
2. Capturar importe, fecha, método y referencia opcional.
3. Seleccionar aplicaciones.
4. Mostrar importe aplicado y saldo no asignado antes de confirmar.
5. Validar permisos, límites y organización.
6. Guardar pago y aplicaciones en una transacción.
7. Mostrar confirmación; la generación de recibo PDF puede añadirse en la siguiente iteración.
8. Registrar auditoría.

### Finalización de contrato
1. Usuario autorizado registra terminación/cancelación y motivo.
2. No borrar cargos ni pagos históricos.
3. No generar cargos fuera de la vigencia aprobada.
4. Recalcular ocupación según otros contratos válidos.
5. Registrar auditoría.
6. No prorratear automáticamente el último mes.

### Incidencia de mantenimiento
1. Crear incidencia con propiedad/unidad, descripción y prioridad.
2. Asignar responsable opcional.
3. Actualizar estado y comentarios.
4. Marcar resuelta con fecha y usuario.
5. Conservar historial.

## 8. Requisitos funcionales

### RF-01 Autenticación
- Inicio/cierre de sesión seguro.
- Toda API privada requiere autenticación.
- Recuperación de acceso mediante proveedor de identidad elegido.
- No implementar criptografía propia.

### RF-02 Organizaciones y miembros
- Configurar organización.
- Invitar/desactivar usuarios y asignar roles.
- Registrar cambios sensibles.

### RF-03 Propiedades y unidades
- Crear, consultar, editar, archivar, buscar y filtrar.
- Validar códigos duplicados.
- Conservar registros con historial.

### RF-04 Inquilinos
- Crear, consultar, editar y archivar.
- Buscar por nombre, email o teléfono.
- Ver contratos y cargos según permisos.

### RF-05 Contratos
- Crear borrador, activar, terminar y cancelar.
- Validar fechas, renta y solapamientos.
- Consultar historial y cargos.
- No cambiar retrospectivamente cargos ya generados.

### RF-06 Cargos
- Crear cargo manual.
- Generar cargos mensuales sin duplicados.
- Consultar vencimientos y saldos.
- Anular con motivo y permisos.

### RF-07 Pagos
- Registrar pagos completos/parciales.
- Mostrar saldo no asignado.
- Revertir con motivo y permiso.
- Proteger contra doble envío.
- Conservar historial de correcciones.

### RF-08 Mantenimiento
- Crear, asignar, comentar, actualizar y resolver incidencias.
- Filtrar por estado, prioridad, propiedad y responsable.
- No exponer datos financieros al rol Maintenance.

### RF-09 Reportes
- Cargos por periodo.
- Pagos recibidos por periodo.
- Saldos pendientes y vencidos.
- Exportación CSV respetando filtros y permisos del servidor.

### RF-10 Auditoría
- Consultar eventos autorizados.
- Registrar actor, entidad, fecha y motivo cuando corresponda.
- Impedir modificaciones por usuarios de negocio.

## 9. Dashboard

Mostrar como mínimo:
- Ingresos recibidos en el periodo.
- Cargos de renta generados en el periodo.
- Saldo pendiente total.
- Número de cargos vencidos e inquilinos con adeudo vencido.
- Unidades ocupadas frente al total de unidades activas.
- Incidencias abiertas por prioridad.
- Actividad reciente.

Cada indicador debe especificar periodo, moneda y definición. Ocupación = unidades ocupadas / unidades activas no archivadas. Las unidades en mantenimiento deben distinguirse visualmente.

## 10. Arquitectura objetivo

- `Habita.Web`: Next.js + TypeScript.
- `Habita.Api`: ASP.NET Core Web API.
- `Habita.Domain`: entidades, estados e invariantes.
- `Habita.Application`: casos de uso, validación y orquestación.
- `Habita.Infrastructure`: EF Core, PostgreSQL, identidad y servicios externos.
- `Habita.Worker`: tareas programadas futuras.
- `Habita.UnitTests`: reglas de dominio.
- `Habita.IntegrationTests`: persistencia, API, permisos y transacciones.
- `Habita.ArchitectureTests`: dependencias entre capas.

El frontend nunca se conecta directamente a PostgreSQL. La API es la puerta de entrada a los datos.

## 11. Endpoints iniciales sugeridos

- `GET /api/v1/me`
- `GET/POST /api/v1/properties`
- `GET/PATCH /api/v1/properties/{id}`
- `GET/POST /api/v1/properties/{id}/units`
- `GET/POST /api/v1/tenants`
- `GET/PATCH /api/v1/tenants/{id}`
- `GET/POST /api/v1/leases`
- `POST /api/v1/leases/{id}/activate`
- `POST /api/v1/leases/{id}/terminate`
- `POST /api/v1/rent-charges/generate-preview`
- `POST /api/v1/rent-charges/generate`
- `GET/POST /api/v1/rent-charges`
- `GET/POST /api/v1/payments`
- `POST /api/v1/payments/{id}/reverse`
- `POST /api/v1/payments/{id}/allocations`
- `GET/POST /api/v1/maintenance-issues`
- `PATCH /api/v1/maintenance-issues/{id}`
- `GET /api/v1/reports/financial-summary`
- `GET /api/v1/audit-logs`

Los nombres son propuestas y deben mantenerse consistentes. Los endpoints financieros requieren autorización, validación, transacción y auditoría.

## 12. Requisitos no funcionales

- Validación centralizada y DTOs explícitos en backend.
- PostgreSQL con migraciones EF Core versionadas.
- OpenAPI actualizado.
- Frontend con estados de carga, error, vacío y éxito.
- HTTPS obligatorio en producción.
- Secretos fuera del repositorio y gestionados por variables/gestor de secretos.
- Logs estructurados sin exponer datos personales innecesarios.
- Copias de seguridad automáticas y pruebas de restauración.
- Pruebas unitarias para reglas de dominio e integración para permisos y transacciones.
- Paginación o límites en listados.
- Control de concurrencia en pagos y asignaciones.
- Docker Compose es suficiente para el primer despliegue; no se requiere Kubernetes al inicio.

## 13. Criterios de aceptación

1. Organización A no puede leer ni modificar registros de organización B, aunque conozca sus UUID.
2. No se puede activar un contrato con fechas solapadas en la misma unidad.
3. Generar cargos dos veces para el mismo periodo no duplica registros.
4. Pago de $1,000 con $700 aplicados muestra $700 aplicados y $300 no asignados.
5. No se puede aplicar más que el saldo del cargo ni más que el saldo no asignado del pago.
6. Un pago confirmado no se elimina ni se edita directamente; se revierte con motivo y auditoría.
7. Revertir un pago recalcula correctamente los saldos afectados.
8. Un cargo con saldo después de su vencimiento aparece vencido conforme a la zona horaria de la organización.
9. Cambiar la renta del contrato no modifica cargos históricos.
10. Maintenance no puede consultar rentas ni pagos.
11. Un cargo anulado no admite asignaciones nuevas.
12. Si falla una parte del registro de pago, no queda un estado parcial inconsistente.
13. Un reintento de la misma solicitud de pago no crea un segundo pago.
14. Las exportaciones CSV respetan permisos y aislamiento por organización.

## 14. Datos ficticios para pruebas

- Organización: `Residencial Demo`.
- Propiedad: `Torre Norte`.
- Unidades: `101`, `102`, `201`.
- Inquilinos: `Ana Ejemplo`, `Luis Prueba`.
- Renta de ejemplo: `$8,500 MXN`.
- Pago de ejemplo: `$5,000 MXN` aplicado parcialmente.
- Incidencia: `Fuga en lavabo`, prioridad normal.

Incluir casos de cargo vencido, pago revertido, cargo parcialmente pagado, contrato terminado, usuario sin permiso, UUID de otra organización y dos solicitudes concurrentes aplicando el mismo saldo. Usar exclusivamente datos ficticios.

## 15. Decisiones pendientes antes de producción

Estas decisiones deben aprobarse explícitamente:
1. **Prorrateo:** ¿renta completa o prorrateada al entrar/salir a mitad de mes?
2. **Depósito:** ¿solo dato contractual o movimiento financiero separado?
3. **Mora:** ¿se requieren intereses/penalizaciones y con qué fórmula?
4. **Moneda:** ¿solo MXN o múltiples monedas?
5. **Autenticación:** proveedor, invitaciones y recuperación de cuenta.
6. **Comprobantes:** recibo interno PDF o CFDI; son alcances distintos.
7. **Pagos no asignados:** ¿se pueden aplicar a otro inquilino? ¿Qué permisos requiere?
8. **Terminación:** quién autoriza y qué efectos tiene sobre cargos futuros.
9. **Retención de datos:** conservación, anonimización y eliminación conforme a obligaciones aplicables.
10. **Notificaciones:** correo, WhatsApp o avisos internos.

### Decisiones aprobadas (2026-10)
- **Autenticación (#5):** ASP.NET Core Identity con cookie HttpOnly; recuperación de contraseña e invitaciones por correo (token de un solo uso, expira en 7 días, se guarda solo su hash).
- **Multi-organización:** un usuario puede pertenecer a varias organizaciones (`Membership`). La organización activa va en un claim de la sesión y la API revalida la membresía y el estado de la organización en cada solicitud.
- **Roles en miembros:** Admin no puede asignar ni modificar a un Owner; la organización debe conservar al menos un Owner activo.

### Reglas de propiedades y unidades implementadas (Hito 2, pendientes de aprobar)
- `Property.Address` es obligatoria. `Unit.Type` toma valores `Apartment`, `House`, `Room`, `Commercial`, `Other`.
- El código de unidad se guarda en mayúsculas y es único por propiedad sin distinguir mayúsculas (`a-1` = `A-1`).
- Archivar una propiedad archiva también todas sus unidades (queda auditado con motivo opcional). Restaurarla no reactiva las unidades: se reactivan una por una.
- Una propiedad archivada no admite unidades nuevas ni reactivar unidades.
- `Occupied` nunca se asigna manualmente; se derivará de los contratos vigentes (Hito 4). Estados manuales: `Available`, `Maintenance`, `Archived`.
- Lectura de propiedades y unidades: todos los roles. Escritura: Owner y Admin. Maintenance recibe unidades sin `BaseRent`.
- Una unidad no puede apuntar a una propiedad de otra organización: lo impide una llave foránea compuesta `(OrganizationId, PropertyId)` en PostgreSQL.

### Reglas de inquilinos implementadas (Hito 3, pendientes de aprobar)
- `Tenant.Status`: `Active` o `Archived`. Nunca se elimina físicamente; se archiva con motivo opcional y puede restaurarse.
- Lectura de inquilinos: Owner, Admin, Finance y ReadOnly. Escritura: Owner y Admin. **Maintenance no tiene acceso** (datos personales; principio 8).
- Correo en minúsculas y con formato válido; teléfono de al menos 7 dígitos y solo números, espacios y `+ - ( ) .`. Ambos opcionales. No se exige unicidad de correo ni teléfono.
- La auditoría de inquilinos registra la acción, el motivo y los **nombres de los campos** modificados, nunca sus valores (sin datos personales en la bitácora).
- Antes de archivar un inquilino con contratos vigentes se bloqueará la acción (se activará en el Hito 4).

### Reglas de contratos implementadas (Hito 4, pendientes de aprobar)
- **En vigor** = estado `Active` o `Ended`. Un contrato terminado con fecha final futura sigue reservando la unidad hasta esa fecha. `Draft` y `Cancelled` nunca reservan.
- **No solapamiento:** dos contratos en vigor de una misma unidad no pueden cubrir el mismo día (fechas inclusivas; sin fecha final = abierto). Se valida al activar y lo garantiza PostgreSQL con una restricción de exclusión, incluso con solicitudes concurrentes. Los borradores sí pueden solaparse.
- **Ocupación derivada:** una unidad `Available` se muestra `Occupied` cuando tiene un contrato en vigor que cubre el día de hoy en la zona horaria de la organización. `Maintenance` y `Archived` prevalecen. Nunca se guarda `Occupied`.
- **Transiciones:** `Draft → Active` (activar); `Active → Ended` (terminar, con fecha y motivo obligatorio; la fecha no puede ser anterior al inicio ni posterior a la fecha final pactada); `Draft`/`Active → Cancelled` (cancelar, con motivo obligatorio, libera la unidad). `Ended` y `Cancelled` son finales.
- **Edición:** un `Draft` admite cualquier cambio. Un `Active` solo admite renta, día de vencimiento, depósito y condiciones; no cambia fechas, unidad ni inquilino (para eso, cancelar y crear otro). Los cargos ya generados nunca se modifican.
- **Bloqueos de archivado:** no se puede archivar una unidad, propiedad o inquilino con un contrato `Active`; una unidad/propiedad/inquilino archivados no admiten contratos nuevos ni activar borradores.
- **Permisos:** lectura de contratos para Owner, Admin, Finance y ReadOnly; escritura solo Owner y Admin; Maintenance sin acceso (importes e identidad de inquilinos).
- Un contrato `Active` cuya fecha final ya pasó sigue `Active` hasta que alguien lo termine; la interfaz lo marca como "Fecha final vencida" y deja de ocupar la unidad por fechas.

### Reglas de cargos implementadas (Hito 5, pendientes de aprobar)
- `RentCharge` tiene `Type` (`Rent` o `Other`). Los cargos de renta se generan; los manuales son `Other`. El periodo (año y mes) es obligatorio en `Rent`; en `Other` es opcional pero siempre va completo (año y mes) o ninguno. La interfaz usa el mes de vencimiento si no se indica periodo.
- **Estado:** se guarda `Open`, `PartiallyPaid`, `Paid` o `Voided`. `Overdue` nunca se guarda: se deriva al leer cuando hay saldo y la fecha local de la organización es posterior al vencimiento (vencer hoy no es estar vencido). Una restricción de la base impide guardar `Overdue`.
- **Importe:** un cargo debe ser mayor que cero (máx. 99,999,999.99, 2 decimales); el saldo nunca es negativo; un cargo anulado no tiene saldo. Los contratos con renta 0 se omiten al generar.
- **Generación mensual:** solo contratos en vigor (`Active` o `Ended`) que cubren algún día del mes. Se cobra la renta completa del contrato, sin prorrateo en el mes de entrada ni de salida. Tras terminar un contrato no se generan meses posteriores a su fecha final. Vencimiento = día de vencimiento del contrato dentro del mes. La vista previa no escribe nada.
- **Idempotencia:** un índice único parcial `(LeaseId, PeriodYear, PeriodMonth)` donde `Type = 'Rent'` y el estado no es `Voided` impide duplicados; ejecuciones concurrentes crean exactamente un cargo por contrato y periodo.
- **Anulación:** requiere motivo; solo Owner y Admin; es definitiva y libera el periodo para volver a generarlo (por ejemplo, tras corregir la renta). Con pagos aplicados (Hito 6) deberán revertirse antes.
- **Permisos:** lectura para Owner, Admin, Finance y ReadOnly; crear, generar y previsualizar para Owner, Admin y Finance; Maintenance sin acceso.
- **Auditoría:** cada generación registra un lote (`charges.generated`) con periodo, cantidad, total y los ids creados; los cargos manuales y las anulaciones se registran individualmente.
- **Decisión abierta:** el vencimiento del primer mes puede caer antes del inicio del contrato (p. ej. contrato que inicia el día 20 con `DueDay` 5). Se aplica la regla del spec sin ajuste; conviene decidir si ese primer cargo vence al iniciar.

### Reglas de pagos implementadas (Hito 6, pendientes de aprobar)
- **Fecha de recepción:** el pago guarda `ReceivedOn` (fecha local, sin hora) en lugar de `ReceivedAt`, porque los ingresos se reportan por fecha de recepción en la zona de la organización. No puede ser futura. `CreatedAt` conserva el instante técnico.
- **Importe y aplicaciones:** el pago es mayor que cero con 2 decimales. Cada aplicación es mayor que cero, no supera el saldo del cargo y la suma no supera el importe del pago. Un mismo cargo no puede repetirse dentro de una solicitud. Solo se aplica a cargos del mismo inquilino y no anulados. Lo no aplicado es **saldo a favor** y nunca se asigna solo a cargos futuros.
- **Atomicidad y concurrencia:** pago, aplicaciones, estados de cargos y auditoría se guardan en una transacción. Se bloquean con `FOR UPDATE` primero el pago y luego los cargos ordenados por id (orden fijo, sin interbloqueos). Un trigger en PostgreSQL revisa los mismos límites como segunda defensa.
- **Idempotencia:** `POST /payments` acepta `Idempotency-Key` (hasta 100 caracteres). El mismo reintento devuelve el pago original con 200 y la cabecera `Idempotent-Replayed: true`; la misma clave con contenido distinto responde 409. Solicitudes simultáneas con la misma clave crean un solo pago.
- **Inmutabilidad:** un pago confirmado no se edita ni se elimina; la base de datos lo impide con triggers. Las aplicaciones solo pueden revertirse una vez.
- **Reversos (solo Owner y Admin, con motivo obligatorio):** revertir un pago revierte todas sus aplicaciones y recalcula los cargos; un pago revertido no admite aplicaciones y su saldo a favor es cero. Revertir una aplicación suelta libera ese importe en el pago para reasignarlo (es la "reasignación" que pide el spec antes de anular un cargo).
- **Anular cargos:** un cargo con aplicaciones activas no puede anularse hasta revertirlas.
- **Permisos:** lectura para Owner, Admin, Finance y ReadOnly; registrar pagos y aplicar saldo para Owner, Admin y Finance; Maintenance sin acceso.
- **Estados de cargo:** se recalculan al aplicar o revertir: pago total = `Paid`, parcial = `PartiallyPaid`, ninguno = `Open`; `Overdue` sigue derivándose por fecha.
- **Auditoría:** `payment.created`, `payment.allocated`, `payment.reversed` y `allocation.reversed`, sin texto libre de notas ni referencias.

### Reglas de mantenimiento implementadas (Hito 7, pendientes de aprobar)
- **Permisos:** todos los roles leen las incidencias (Maintenance incluido: ve todas, con datos operativos mínimos). Crear, editar, asignar y cerrar: solo Owner y Admin. Maintenance comenta y cambia el estado **solo de las incidencias asignadas a él**, y no puede cerrarlas ni devolverlas a "Abierta". Finance y ReadOnly solo leen.
- **Sin datos financieros ni de inquilinos:** las incidencias y su historial nunca incluyen rentas, pagos ni identidad de inquilinos. El rol Maintenance recibe 403 en contratos, cargos, pagos e inquilinos.
- **Estados:** `Open`, `InProgress`, `Waiting`, `Resolved`, `Closed`. Transiciones: `Open`/`InProgress`/`Waiting` se mueven entre sí y cualquiera puede pasar a `Resolved`; `Resolved` pasa a `Closed` o se reabre a `InProgress`; `Closed` es final. Una incidencia cerrada no admite cambios, comentarios ni reasignación.
- **Resolver:** guarda fecha (`ResolvedAt`) y usuario (`ResolvedByUserId`). Reabrir los limpia en la incidencia; el historial conserva el evento. Una restricción de la base garantiza que `Resolved` siempre tenga ambos y que un estado no resuelto no los tenga.
- **Asignación:** solo a un miembro activo de la organización con rol Maintenance, Admin u Owner. Un usuario desactivado o de otra organización se rechaza.
- **Ubicación:** la propiedad debe estar activa y la unidad (opcional) debe pertenecer a esa propiedad y no estar archivada; una llave foránea compuesta lo impone en la base. Sin unidad = incidencia de la propiedad (áreas comunes).
- **Historial inmutable:** cada creación, cambio de estado (con estado anterior y nuevo), asignación, edición y comentario queda como evento con su autor; la base impide editarlo o borrarlo. Los comentarios admiten hasta 2000 caracteres.
- **Concurrencia:** control optimista (`xmin`); dos cambios simultáneos sobre la misma incidencia no se pisan: uno gana y el otro recibe 409.
- **Listado:** ordenado por prioridad (Urgente primero) y luego por fecha de reporte más reciente; filtros por estado (o "activas"), prioridad, propiedad, unidad, responsable, "asignadas a mí" y búsqueda por título, propiedad o unidad.
- **Endpoints:** además de `GET/POST /maintenance-issues` y `PATCH /maintenance-issues/{id}` (título, descripción, prioridad, unidad), se agregan `GET /{id}`, `POST /{id}/status`, `POST /{id}/assign` y `POST /{id}/comments`.
- La prioridad y el estado de la unidad ("En mantenimiento") son independientes: una incidencia no cambia el estado de la unidad.

### Reglas de reportes, dashboard y CSV implementadas (Hito 8, pendientes de aprobar)
- **Ingresos recibidos:** pagos `Confirmed` cuya fecha de recepción (`ReceivedOn`, fecha local) cae en el rango, con extremos inclusivos. Los pagos `Reversed` se muestran aparte, por su fecha de recepción original, y nunca se restan ni suman al ingreso.
- **Cargos generados:** un cargo pertenece al mes de su periodo (`PeriodYear`/`PeriodMonth`) o, si no tiene periodo, al mes de su vencimiento. Se separan renta, otros cargos y anulados; los anulados no cuentan como generados.
- **Cifras "a la fecha"** (no dependen del periodo elegido): saldo pendiente = total cargado (sin anulados) menos lo aplicado por pagos activos; cargos vencidos y su saldo, e inquilinos con adeudo vencido, con la fecha local de la organización; saldo a favor = pagos confirmados menos lo aplicado.
- **Desglose mensual:** hasta 24 meses (más allá solo totales). Rango máximo del resumen: 36 meses; de un CSV, 1,100 días.
- **Dashboard (§9):** cada indicador muestra periodo, moneda y definición. Ocupación = unidades ocupadas / unidades activas no archivadas; las unidades en mantenimiento se cuentan aparte y se distinguen visualmente. Incidencias abiertas (abiertas, en proceso, en espera) por prioridad. Actividad reciente: las últimas 10 acciones auditadas con etiqueta legible, actor y fecha, **sin metadatos**.
- **Gráficas del dashboard:** (1) cargado frente a recibido de los últimos 6 meses terminando en el periodo elegido (columnas agrupadas; el cargado es renta más otros cargos sin anulados, el recibido son pagos confirmados por fecha de recepción, y los revertidos se muestran solo en el tooltip y la tabla); (2) saldo vencido por antigüedad en días de atraso: 1–30, 31–60, 61–90 y más de 90 (cada cargo vencido cae en exactamente un tramo y los tramos suman el saldo vencido); (3) incidencias abiertas por prioridad; (4) ocupación. Toda gráfica tiene vista de tabla y el mismo detalle por teclado que por ratón.
- **Permisos:** reportes y CSV para Owner, Admin, Finance y ReadOnly. El rol Maintenance recibe un dashboard sin sección financiera (ocupación, incidencias y solo actividad de incidencias) y 403 en reportes y exportaciones. La actividad reciente excluye cambios de miembros, invitaciones y exportaciones para Finance y ReadOnly.
- **CSV:** UTF-8 con BOM (Excel), RFC 4180, números con punto y 2 decimales, fechas ISO, descarga con `Cache-Control: no-store`. Las celdas de texto que empiezan con `=`, `+`, `-`, `@`, tabulador o retorno se prefijan con apóstrofo para evitar inyección de fórmulas. Se escribe en streaming por páginas. Tres reportes: cargos del periodo (con filtro de estado), pagos recibidos (con filtro de estado) y saldos pendientes y vencidos a la fecha.
- **Auditoría:** cada exportación aceptada registra `report.exported` con el reporte y sus filtros; una solicitud rechazada no se registra.

### Reglas de auditoría, seguridad y operación implementadas (Hito 9, pendientes de aprobar)
- **Consulta de auditoría (RF-10):** `GET /api/v1/audit-logs`, solo Owner y Admin, con filtros por tipo de registro, acción, persona, registro (id) y rango de fechas (días locales de la organización), paginado. Devuelve etiqueta legible, actor, motivo y metadatos estructurados. No existe ningún endpoint que cree, edite o borre entradas; la base de datos impide además `UPDATE` y `DELETE`. Los metadatos nunca contienen contraseñas ni tokens (probado).
- **Datos de demostración (§14):** comando `--seed-demo`, solo en Development, idempotente, con datos ficticios: el escenario del spec y los casos de cargo vencido, pago revertido, cargo parcialmente pagado, contrato terminado, usuarios sin permiso, UUID de otra organización y un pago con saldo a favor para probar dos solicitudes concurrentes.
- **Sesión y fuerza bruta:** bloqueo de la cuenta en el 5.º intento fallido; límite por IP de 10 solicitudes por minuto en login, restablecimiento y aceptación de invitaciones, y de 5 por 10 minutos en registro y recuperación de contraseña (429 con `Retry-After`). Configurable en `RateLimiting__*`.
- **HTTPS:** obligatorio en producción. TLS se termina en el proxy de borde (Caddy, certificados automáticos y redirección HTTP→HTTPS); la cookie de sesión es `Secure` y la API solo confía en `X-Forwarded-*` de redes privadas.
- **CSRF y cabeceras:** además de `SameSite=Lax`, las solicitudes que modifican datos con encabezado `Origin` deben provenir de un origen permitido (`Security__AllowedOrigins`). Cabeceras de seguridad en API y web; solicitudes limitadas a 1 MB.
- **Arranque seguro:** en producción la API se niega a iniciar con contraseña de BD de ejemplo, origen o `Web__BaseUrl` sin https, `AllowedHosts=*`, tokens de invitación expuestos, SMTP o carpeta de claves ausentes, o límites de intentos / cookies seguras desactivados.
- **Respaldos y restauración:** respaldo lógico automático (24 h por defecto, 14 días de retención) con verificación de lectura y SHA-256; prueba de restauración semanal en una base temporal que verifica migraciones, tablas, triggers, restricción de contratos e invariantes de dinero. Probado con datos reales, incluido un respaldo corrupto (la prueba falla) y la retención.
- **Logs:** JSON estructurado en producción con solo identificadores de organización y usuario.

### Estado de las decisiones pendientes (§15)
| # | Decisión | Estado en la implementación | Falta |
|---|---|---|---|
| 1 | Prorrateo | Renta completa, sin prorrateo en entrada ni salida | Aprobar |
| 2 | Depósito | Dinero en custodia cobrado en abonos (`Deposit`); no es ingreso; se liquida al terminar el contrato con descuentos y motivo (Hito 15) | Aprobar; decidir si lo retenido pasa a ingreso |
| 3 | Mora | No hay intereses ni penalizaciones | Definir fórmula si se requiere |
| 4 | Moneda | MXN por defecto; una moneda por organización, sin conversión | Aprobar o ampliar |
| 5 | Autenticación | **Decidido:** ASP.NET Core Identity, cookie, invitaciones y recuperación por correo | Configurar SMTP real y SPF/DKIM |
| 6 | Comprobantes | Comprobante que sube el inquilino (Hito 12) y **recibo interno con folio consecutivo en PDF** (Hito 14). No hay CFDI | Aprobar el recibo interno o definir facturación |
| 7 | Pago no asignado a otro inquilino | No permitido: solo se aplica a cargos del mismo inquilino | Definir si hace falta un flujo con permiso |
| 8 | Terminación | Owner y Admin, con fecha y motivo; no se generan cargos posteriores a la fecha final | Aprobar |
| 9 | Retención de datos | Sin eliminación ni anonimización; respaldos 14 días; auditoría inmutable | Decidir con asesoría legal |
| 10 | Notificaciones | En la app, correo y aviso en el dispositivo (opcional), recordatorios diarios y avisos al personal (Hito 13). WhatsApp fuera | Aprobar canales, textos y retención |

Otros puntos que requieren decisión: vencimiento del primer cargo cuando el contrato inicia después del día de vencimiento (Hito 5), reglas nuevas marcadas como "pendientes de aprobar" en cada hito, y la CSP con *nonces* (hoy `unsafe-inline`).

### Reglas de accesos, ajustes y archivos implementadas (Hito 10, pendientes de aprobar)

Origen: plan de integración de NethRent (`docs/PLAN_INTEGRACION_NETHRENT.md`). Amplía el alcance del MVP con un portal de inquilino; requiere aprobación.

- **Nuevos roles.** `Collector` (Cobrador) y `Tenant` (Inquilino).
  - *Collector:* ve inquilinos **sin correo ni notas** y la búsqueda solo coincide por nombre; lee cargos y pagos; registra y asigna pagos. No ve contratos, propiedades, mantenimiento, reportes, exportaciones, miembros, auditoría ni ajustes, y no crea cargos ni revierte.
  - *Tenant:* un login ligado a **un** registro de inquilino de su organización (`Membership.TenantId`, FK compuesta, único por organización, CHECK: rol Tenant ⇔ TenantId). Solo puede usar `/me`, `/files` y (Hito 11) `/portal/*`. Todo lo demás responde 403.
  - La política `InOrganization` desaparece: `Staff` (Owner, Admin, Finance, Maintenance, ReadOnly) la reemplaza. `AnyMember` solo se usa donde el servicio comprueba la propiedad del recurso.
  - Una prueba enumera **todas** las rutas `/api/v1` y exige 403 para un Tenant (salvo la lista blanca) y 401 sin sesión.
- **Rol Tenant no asignable a mano.** No se da por invitación ni cambio de rol; un acceso de inquilino no cambia de rol (se desactiva y se da otro).
- **Códigos de acceso** (Owner/Admin). 10 caracteres de un alfabeto sin ambigüedades (50 bits), de un solo uso, vigencia 1–30 días (7 por defecto), solo se guarda el hash y se muestra una vez. Dan rol Collector o Tenant (con inquilino activo y sin acceso previo). Un código nuevo para el mismo inquilino revoca el anterior. El canje es público, con límite por IP (10/min), responde igual ante código desconocido, usado, revocado o vencido, y el consumo es atómico (de dos canjes simultáneos solo uno gana). Una cuenta existente debe iniciar sesión para canjear; no se toma por correo.
- **Ajustes por organización** (Owner/Admin): nombre y domicilio del arrendador, días de recordatorio de renta (0–60, máx. 5; por defecto 3, 1, 0) y de fin de contrato (1–60; por defecto 30, 7, 1). Lista vacía = sin recordatorio. Los usarán los Hitos 13 y 14. Auditoría registra qué campos cambiaron, no sus valores.
- **Archivos privados.** Máx. 5 MB; solo JPG, PNG, WebP y PDF, decididos por los **primeros bytes** (no por nombre ni tipo declarado); nombre en disco generado (GUID) y nombre visible saneado; SHA-256 guardado; descarga siempre como `attachment` con `nosniff`. Los abre quien lo subió o Owner/Admin/Finance/Collector; para el resto responde 404. Subidas limitadas a 20/min por cliente.
- **Pendiente de decidir:** cuánto tiempo se conservan los archivos y quién puede purgarlos; el respaldo incluirá los archivos en el Hito 16.

### Reglas del portal del inquilino implementadas (Hito 11, pendientes de aprobar)

- **Alcance.** El portal (`/portal/*`, solo rol Tenant) amplía el MVP: el inquilino ve **solo lo suyo** y puede reportar fallas. Ninguna ruta del portal recibe un id de inquilino: el inquilino es el de la sesión. Cualquier otro rol recibe 403.
- **Estado de cuenta.** Resumen (saldo por pagar, vencido, próximo vencimiento, saldo a favor, contrato vigente), cargos y pagos propios. Los cargos **anulados no se muestran**. Un pago revertido aparece como «Revertido» sin motivo ni notas; nunca se exponen notas internas, quién registró el pago ni datos de otros inquilinos.
- **Reportes de mantenimiento.** El inquilino crea reportes sobre **la unidad de su contrato vigente** (no elige unidad, propiedad ni prioridad: queda en prioridad Normal). Máximo 10 abiertos por inquilino. Solo ve sus propios reportes; el de otro inquilino responde 404.
- **Visibilidad.** El inquilino lee: el reporte, los cambios de estado (sin la nota del personal) y los comentarios marcados **«Lo ve el inquilino»**. Todo comentario del personal es **interno por defecto**. Nunca se muestran nombres del personal. Al rol Mantenimiento se le oculta el nombre del inquilino («Inquilino») y los indicadores de visibilidad.
- **Relevo de acceso.** Si el acceso de un inquilino se desactiva, un código nuevo entrega esa membresía a la cuenta nueva y la anterior pierde el acceso. Mientras un acceso esté activo no se puede crear otro.
- **PWA.** Instalable (manifiesto, íconos, tema). El service worker solo guarda la página «Sin conexión» y dos recursos estáticos: **nunca** respuestas de la API ni páginas con datos de cuenta. Se registra solo en producción.
- **Pendiente:** aviso de privacidad del portal (revisión legal) antes de dar acceso a inquilinos reales; avisos al personal por reportes nuevos (Hito 13).

### Reglas de cobranza implementadas (Hito 12, pendientes de aprobar)

Origen: plan de integración de NethRent. Amplía el MVP (transferencias con comprobante, promesas y cobro en efectivo por QR); requiere aprobación.

- **Cuentas bancarias** (alta, edición y archivo: Owner/Admin; lectura: quien lee reportes). CLABE de 18 dígitos validada con su **dígito verificador**; el banco se detecta por los 3 primeros dígitos (catálogo parcial: un prefijo desconocido es válido y se muestra «Banco no identificado»). Una cuenta *general* (una sola predeterminada activa) y, opcionalmente, **una cuenta activa por propiedad**; PostgreSQL lo garantiza con índices únicos. Las cuentas se archivan, no se borran. El inquilino ve **solo** la de la propiedad de su contrato vigente o, si no hay, la predeterminada. La auditoría guarda los últimos 4 dígitos, nunca la CLABE completa.
- **Reporte de pago** (`PaymentClaim`). El inquilino declara importe, fecha, método (transferencia, efectivo u otro; no tarjeta), referencia opcional y **adjunta un comprobante obligatorio** que él mismo subió (el archivo de otra persona se trata como inexistente). Máximo **5 reportes en revisión** por inquilino y un comprobante solo respalda un reporte. Fecha no futura ni de hace más de un año. No cuenta como ingreso hasta que el personal lo **confirma**.
  - *Confirmar* (Owner, Admin, Finance, Collector): crea un `Payment` real con las reglas de siempre (idempotencia con la clave `claim:{id}`, bloqueos, aplicaciones elegidas por quien confirma; lo no aplicado queda como saldo a favor) y liga el reporte al pago. Todo en una transacción: si el pago no se puede registrar, el reporte sigue pendiente. Dos personas confirmando a la vez producen **exactamente un pago**.
  - *Rechazar*: comentario obligatorio (máx. 500) que **lee el inquilino**; no revela quién revisó. *Cancelar*: solo el inquilino y solo si sigue pendiente.
  - Un reporte atendido **no cambia ni se borra** (disparador en la base). Si el mismo archivo (SHA-256) está en más de un reporte, el personal ve la alerta «comprobante repetido».
- **Promesa de pago** (Owner, Admin, Finance, Collector crean y cancelan; lectura con los permisos financieros). Fecha entre hoy y 90 días, nota opcional, sobre un cargo con saldo. **Una promesa abierta por cargo**: una nueva reemplaza a la anterior. El estado **se deriva** del dinero recibido: *Cumplida* si lo recibido hasta el día prometido (por fecha de recepción de pagos confirmados) cubre el cargo, aunque sea antes; *Incumplida* si el día pasó sin cubrirlo; *Pendiente* en otro caso; *Cancelada* si se canceló o el cargo se anuló. Un pago posterior al día prometido no la cumple.
- **Efectivo con QR.** El inquilino genera un código de 10 caracteres (50 bits, mismo alfabeto que los códigos de acceso), **un solo uso, vigencia 4 horas**, máximo 10 por día; generar otro revoca el anterior. Solo se guarda el hash. El QR contiene `habita:cash:XXXX-XXXX-XX`; también se puede escribir.
  - *Resolver* (personal con permiso de registrar pagos, **sesión obligatoria**, 30 por minuto por persona): muestra inquilino y cargos con saldo; **no consume** nada. Código desconocido, usado, revocado, vencido, de otra organización o de un inquilino archivado responden **lo mismo**.
  - *Cobrar*: crea un pago en efectivo con fecha de hoy (la de la organización) y consume el código en la misma transacción; la clave de idempotencia es el código. La misma solicitud repetida devuelve el pago ya creado (200); una distinta responde 409. Si el pago no es válido, el código sigue vigente.
  - El cobrador **no puede** poner otra fecha. El código solo no prueba nada: sin sesión de personal no abre nada.
- **Cámara.** La web permite la cámara solo para su propio origen (`Permissions-Policy: camera=(self)`) para escanear; si falla, se escribe el código.
- **Sin notificaciones todavía.** El personal no recibe aviso de un reporte nuevo ni el inquilino de su resolución hasta el Hito 13 (decisión #10).
- **Pendiente de decidir:** cuánto se conservan los comprobantes (sigue pendiente desde el Hito 10); si un cobrador debe poder rechazar reportes o solo confirmarlos; límite de monto para cobros en efectivo.

### Reglas de avisos y recordatorios implementadas (Hito 13, pendientes de aprobar)

Origen: plan de integración de NethRent. Cierra la decisión #10 (notificaciones) en su propuesta; requiere aprobación.

- **Qué es un aviso.** Un mensaje para **una persona** dentro de **una organización**. Se guarda en la misma transacción que el hecho que cuenta (si el pago no se guardó, el aviso no existe). Lo que dice **no se modifica** (disparador en la base); solo cambian «leído» y el estado de entrega. Siempre aparece en la app (campana y página Avisos); además puede llegar por **correo** y por **aviso en el dispositivo (Web Push)**. Cada persona elige correo y dispositivo para sí; el aviso en la app no se apaga.
- **El nombre va primero.** Todo aviso que habla de una persona **empieza por su nombre y luego sus apellidos**, tal como están en su ficha: «Ana Ejemplo reportó un pago», «Ana Ejemplo, tu renta vence hoy», «María Fernanda Pérez López prometió pagar hoy». Nunca «Pérez López, María Fernanda». El sistema **no reordena** nombres (solo quita espacios sobrantes): como el nombre es un único campo, conviene capturarlo como «Nombre Apellidos»; una ficha capturada con los apellidos primero se repetirá así.
- **A quién se avisa.**
  | Hecho | Recibe | Texto (empieza por el nombre) |
  |---|---|---|
  | Un inquilino reporta un pago | Owner, Admin, Finance | «{Nombre} reportó un pago» |
  | Se confirma o rechaza su reporte | Ese inquilino | «{Nombre}, tu pago de $X fue confirmado» / «…no fue aceptado» (con el motivo) |
  | Se cobra su efectivo con QR | Ese inquilino | «{Nombre}, registramos tu pago en efectivo de $X» |
  | Un inquilino reporta una falla | Owner, Admin | «{Nombre} reportó una falla» |
  | Renta por vencer (días de *Ajustes*, 3, 1 y 0 por defecto) | El inquilino, si tiene acceso | «{Nombre}, tu renta vence en N días / hoy» |
  | Cargo vencido (1, 7, 14 y 30 días después) | El inquilino y Owner, Admin, Finance | «{Nombre}, tienes un cargo vencido» / «{Nombre} tiene un cargo vencido hace N días» |
  | Contrato por terminar (días de *Ajustes*, 30, 7 y 1) | El inquilino y Owner, Admin, Finance | «{Nombre}, tu contrato termina en N días» / «{Nombre}: su contrato termina en N días» |
  | Promesa de pago: el día prometido, y si se incumple al día siguiente | Quien la registró (si ya no está, Owner y Admin) | «{Nombre} prometió pagar hoy» / «{Nombre} no cumplió su promesa de pago» |
  Cobrador y Mantenimiento **no** reciben los avisos de la oficina. Un inquilino sin acceso al portal no recibe nada propio, pero el equipo sí se entera de sus vencidos. El recordatorio de un abono del depósito (Hito 15) sale con su propio texto.
- **Recordatorios diarios.** Una corrida **por organización y por día local**, a partir de la **hora de recordatorios** de *Ajustes* (por defecto 9:00, hora de la organización, 0–23). Usa la **zona horaria de cada organización**, no la del servidor. Solo corre si la hora ya llegó y hoy no se ha hecho; si el worker estuvo apagado varios días, hace **una** corrida con la fecha de hoy (no reconstruye días pasados). Las organizaciones suspendidas se omiten. Cada aviso lleva una **clave de deduplicación** por persona: repetir la corrida, o que dos workers coincidan, nunca guarda un aviso dos veces; además un candado de PostgreSQL hace que solo un worker haga la corrida.
- **Entrega por correo y dispositivo.** La hace el **worker** cada pocos segundos, con las filas bloqueadas con `SKIP LOCKED` (varios workers pueden convivir sin enviar dos veces). Un canal que falla se reintenta tras 1, 5, 15 y 60 minutos y se abandona al **5.º intento**; uno ya enviado no se reenvía. Avisos de **más de 48 horas** no se envían (siguen en la app). Si la persona apagó un canal, no hay envío ni reintento. El **correo** lleva el detalle y un enlace; el aviso en el **dispositivo** lleva solo el título (con el nombre) y «Abre Habita para ver el detalle», **sin importes**, porque una pantalla bloqueada la puede ver cualquiera.
- **Dispositivos (Web Push).** Opcional: requiere el par de llaves VAPID del servidor (`--generate-vapid`); sin él, la función está apagada y la web lo dice. Máximo 10 dispositivos por persona; un dispositivo es de **una sola persona** (si otra inicia sesión en el mismo navegador, se le reasigna). El servidor solo envía a direcciones **https, puerto 443, sin credenciales y de un servicio push conocido** (FCM, Mozilla, Apple, Windows; ampliable con `Push__AllowedHosts`), sin seguir redirecciones: así nadie puede apuntar al servidor hacia una dirección interna. Un dispositivo que el navegador ya dio de baja se olvida. En iPhone requiere instalar Habita en la pantalla de inicio (iOS 16.4 o superior).
- **En vivo (SSE).** La campana se actualiza sola. El flujo `/notifications/stream` solo trae **los avisos de la persona que lo abre**, se cierra a los **5 minutos** (el navegador reconecta y se vuelve a validar la sesión, así que quien sale de la organización deja de recibir en minutos) y se limita a **5 abiertos por persona**. No se comprime ni se retiene en ningún proxy (Next y Caddy están configurados para ello).
- **Privacidad y alcance.** Cada persona solo lee y marca **sus** avisos (otro id responde 404). Los avisos no se borran (retención por decidir). WhatsApp sigue fuera: solo enlaces para compartir.
- **Pendiente de decidir:** cuánto tiempo se conservan los avisos; si el Cobrador debe recibir avisos de cobranza; horario de silencio; si el correo debe poder desactivarse por tipo de aviso (hoy es un solo interruptor).

### Reglas de contratos, recibos y estado de cuenta implementadas (Hito 14, pendientes de aprobar)

Origen: plan de integración de NethRent. Cierra la decisión #6 (comprobantes) en su propuesta: recibos internos con folio, **sin** factura (CFDI); requiere aprobación.

- **Plantilla de contrato.** Es el Word de la organización, con campos `{{así}}`. Owner y Admin la suben; Owner, Admin, Finance y ReadOnly la leen. Se usa **el mismo vocabulario del sistema anterior** (`{{nombre_completo}}`, `{{renta_letra}}`, `{{fiador_linea}}`…) más algunos nuevos (`propiedad_nombre`, `propiedad_direccion`, `email_arrendatario`, `dia_pago`, `deposito_letra`). Un campo puede venir **partido en varios fragmentos de formato** (Word lo hace al cambiar negritas a la mitad) y se encuentra igual; el valor toma el formato de donde empezó el campo. Al subirla se **rechaza** (con un mensaje claro) si tiene un campo que no existe (un error de escritura saldría como un hueco en un texto legal), llaves mal formadas, ningún campo, macros, objetos incrustados, enlaces a archivos externos (los hipervínculos normales sí se permiten), una estructura sospechosa (bomba zip, rutas que escapan, más de 60 MB descomprimido) o más de 5 MB. Hay **una plantilla activa** por organización; al subir otra, la anterior se conserva en el historial y los contratos ya generados no cambian. Un ejemplo descargable usa todos los campos.
- **Cómo se llena.** Nombre del inquilino como en su ficha (nombre y luego apellidos); fechas «17 de octubre de 2026»; moneda «$8,500.00»; **renta con letra** «OCHO MIL QUINIENTOS PESOS 00/100 M.N.» (centavos sobre 100 con redondeo de dinero; «UN MILLÓN DE PESOS» cuando son millones exactos; «VEINTIÚN», «CIENTO UN»; hasta 999,999,999,999.99; USD «DÓLARES»; otra moneda lleva su clave). Sin fecha final: «tiempo indefinido». `metodo_pago` dice «efectivo o transferencia»: si tu contrato necesita otra frase, escríbela directo en el Word. `arrendador_linea` empieza «ARRENDADOR:». `fiador_*`, `inventario`, `deposito_tipo` y `deposito_fechas` quedan **vacíos** hasta el Hito 15. Un renglón cuyos campos quedaron vacíos **se quita**, y también su título si se queda solo («INVENTARIO» sin nada debajo), como en el sistema anterior. Un dato que parezca un campo (un nombre con `{{renta}}`) **no se vuelve a expandir**.
- **Contratos generados.** Cada generación guarda el Word llenado y su PDF como una **versión nueva y congelada** (1, 2, 3…) con huella SHA-256, quién y cuándo; generar otra vez nunca toca las anteriores. Ni la fila ni los archivos se pueden modificar ni borrar (disparadores y llaves foráneas). Genera Owner o Admin; leen Owner, Admin, Finance y ReadOnly. Un contrato cancelado no genera documentos. Dos personas generando a la vez obtienen versiones distintas y consecutivas. El inquilino descarga el PDF **más reciente de su propio contrato vigente** (nunca el de otro).
- **PDF.** Lo hace **Gotenberg** (LibreOffice para el Word, Chromium para recibos y estados de cuenta), un servicio en la red interna, sin salida a internet y sin puerto publicado. Si no responde, la API contesta **503** sin gastar la versión ni el folio. Las descargas de PDF se limitan a 20 por minuto por persona.
- **Recibos.** Cada pago tiene un **folio consecutivo por organización** («R-000001»), que se toma **dentro de la misma transacción del pago**: son únicos aunque dos pagos lleguen a la vez, no tienen huecos (un pago que falla devuelve su número) y un reintento con la misma clave de idempotencia no usa otro. Los pagos que ya existían se numeraron al migrar, por orden de captura. El folio no se modifica ni se borra. La página se dibuja **al pedirla**: arrendador (o el nombre de la organización), «Recibí de» (nombre y apellidos), fecha, forma de pago, referencia, importe con letra, a qué cargos se aplicó y el saldo a favor de ese pago. **No** lleva cuenta bancaria, notas internas, motivo de un reverso ni ids. Si el pago se revierte, el mismo folio sale con el sello **CANCELADO** (sin decir por qué) y no sirve como comprobante; el número nunca se reutiliza. Lleva la leyenda «no es una factura (CFDI)». Lo piden quienes leen pagos (Owner, Admin, Finance, ReadOnly y Cobrador) y cada inquilino **solo los suyos** (el de otro responde 404).
- **Estado de cuenta.** Un PDF por inquilino, a la fecha de hoy: cargado, pagado (solo lo aplicado a cargos), saldo por pagar y saldo a favor; la lista de cargos (sin los anulados; el estado «vencido» manda sobre «pago parcial», como en el resto de Habita) y de pagos con su folio (los cancelados aparecen tachados y no suman). Lo piden Owner, Admin, Finance y ReadOnly (no el Cobrador ni Mantenimiento) y cada inquilino el **suyo**.
- **Seguridad.** Toda la información que entra en una página se escapa (nombres, referencias…); no hay scripts ni recursos externos. Los documentos se ofrecen como **descarga** (no se muestran dentro del sitio). La auditoría registra la generación de contratos y la subida de plantillas con sus huellas, **no** su contenido.
- **Pendiente de decidir:** si el folio y el recibo interno bastan o se necesita factura (CFDI, decisión #6); cuánto tiempo se conservan los contratos y plantillas; plantilla distinta por propiedad; firma electrónica; que `metodo_pago` sea un ajuste de la organización.

### Reglas de datos del negocio implementadas (Hito 15, pendientes de aprobar)

Origen: plan de integración de NethRent. Propone la política del depósito (decisión #2) y la definición de «vencido» con días de gracia.

- **Días de gracia.** Ajuste por organización de 0 a 30 (la base de datos también lo exige). Un cargo está **vencido** cuando hoy es posterior a `fecha de pago + días de gracia`; con 0 es como antes. Aplica a la lista y filtros de cargos, reportes (saldo vencido, CSV), tablero, portal del inquilino, estado de cuenta, cobro en efectivo y recordatorios. Los avisos de atraso (1, 7… días) cuentan desde que termina la gracia; la antigüedad de saldos sigue contando desde la fecha de pago. Cambiarlo queda en la auditoría.
- **Fiador.** Opcional: nombre y teléfono (el teléfono exige nombre). Vaciar el nombre quita ambos. Cambia también en un contrato vigente. La auditoría solo dice que cambió, no a quién.
- **Inventario de entrega.** Lista por contrato (artículo, cantidad 1–999, estado Bueno/Regular/Malo, notas; máx. 100). Se reemplaza completa respetando los artículos existentes por id; solo mientras el contrato no esté terminado ni cancelado. Al terminar se registra el estado de devolución de cada artículo junto al de entrega.
- **Depósito.** Se programa en 1–12 abonos con fecha que suman **exactamente** lo que dice el contrato (el sobrante no programado se puede programar después; anular un abono libera su importe). Cada abono es un cargo `Deposit` («Depósito (abono 2 de 3)») que se cobra como cualquier cargo y recibe recordatorios propios. Lo aplicado a un depósito **no es ingreso**: se resta de «recibido» y de los meses del reporte, y aparece como `depositsHeld` (custodia), `depositsReceived`, `depositsReturned` y `depositsRetained`. No cuenta como renta ni como «otro cargo». Revertir el pago devuelve la custodia.
- **Liquidación.** Solo con contrato terminado o cancelado, con todos los abonos cobrados o anulados y algo cobrado. Parte de lo realmente cobrado; cada descuento lleva motivo e importe (máx. 20; la suma no pasa de lo retenido); lo demás se devuelve. Es inmutable (disparadores): solo se **anula** con motivo y se puede volver a liquidar; las anuladas quedan en el historial. Una sola liquidación vigente por contrato. Los motivos no van a la auditoría (solo importes).
- **Contrato (Word).** Se llenan `fiador_nombre`, `fiador_telefono`, `fiador_linea`, `inventario`, `deposito_tipo` («en una sola exhibición» / «en N abonos») y `deposito_fechas`; las líneas sin dato se omiten.
- **Importador.** Ver el plan (Hito 15). Reglas: obligatorias Nombre, Depto, Renta e Inicio; día de pago 1–28 (29–31 se rechazan); fechas día/mes/año o de Excel; importes con `$` y comas; el mismo nombre (sin acentos, mayúsculas ni espacios de más) es la misma persona: se reutiliza el inquilino activo y se rechaza si hay dos activos o uno archivado; un contrato que se solapa con otro vigente de la unidad se rechaza (si se activan); el nombre se conserva tal como viene (nombre y luego apellidos). La auditoría `import.confirmed` solo guarda cantidades.
- **Pendiente de decidir:** si lo retenido de un depósito pasa a ingreso (hoy solo se informa como «retenido»); si el depósito debe poder cobrarse sin programar abonos; si el importador debe poder actualizar contratos existentes; límites de filas/tamaño; que el recibo de un depósito diga «depósito en custodia».

### Cierre y endurecimiento (Hito 16, pendientes de aprobar)

- **Respaldos.** Además del `pg_dump`, el respaldo incluye los archivos subidos (`<base>-<sello>.files.tar.gz`, con su SHA-256), archivados antes de volcar la base. La prueba de restauración verifica la huella del archivo comprimido, que cada fila de `StoredFiles` tenga su archivo con el SHA-256 que dice la base, las protecciones (disparadores) de todas las tablas inmutables y las cuentas de las liquidaciones de depósito. Falla si falta el respaldo de archivos teniendo filas, si un archivo falta, está alterado o el archivo comprimido está dañado. Comprobado con cinco sabotajes (archivo alterado, ausente, comprimido corrupto, sin respaldo de archivos, carpeta de archivos inexistente).
- **Pruebas de extremo a extremo** (Playwright, Chrome del sistema, pila temporal con `web/e2e/run.sh`): comprobante → confirmación con avisos en vivo, cobro en efectivo con código de un solo uso, recordatorio del worker, importación de Excel/CSV, depósito en custodia y accesos prohibidos por rol.
- **Matriz de autorización** versionada (`AuthorizationMatrix.txt`): 118 combinaciones ruta × método contra 7 roles; cualquier cambio de política rompe la prueba hasta que se revise y se apruebe. Revisada contra este documento sin hallazgos de acceso indebido.
- **Revisión de seguridad de las superficies nuevas.**
  - *Subidas:* tipo por contenido, 5 MB, nombre generado, descarga como adjunto. **Hallazgo:** no había tope de archivos por persona (solo 20 por minuto) → se agregó un máximo de 200 por persona cada 24 horas.
  - *Importador de Excel:* macros, objetos incrustados, enlaces externos y bombas zip se rechazan antes de abrir el archivo; XML con DOCTYPE/entidades (bomba de entidades) en el libro, las hojas, las cadenas o los estilos se rechaza rápido con un 400, sin expandirse (probado); el archivo nunca se guarda; máximo 500 filas y 5 MB; 20 solicitudes por minuto por persona; solo Propietario y Administrador.
  - *Códigos de efectivo:* ≈49 bits de entropía, 4 horas de vida, un solo uso, solo se guarda su hash, 30 búsquedas por minuto por persona, 10 códigos al día por inquilino.
  - *Flujo en vivo (SSE):* solo los avisos de quien lo abre, 5 flujos por persona, se cierra a los 5 minutos y revalida la sesión.
  - *Portal del inquilino:* todas las rutas están cerradas a los demás roles y el inquilino solo alcanza las suyas (matriz y pruebas de propiedad de cada recurso).
- **Pendiente de decidir:** que el Cobrador solo pueda abrir los comprobantes de los reportes que atiende (hoy los roles que revisan —Propietario, Administrador, Finanzas y Cobrador— abren cualquier archivo de la organización; hace falta el id, que no se adivina); cuota de almacenamiento por organización (hoy solo por persona); retención y purga de comprobantes (decisión #6); aviso de privacidad del portal (revisión legal) antes de dar acceso a inquilinos reales; CSP con *nonces*.

### Aclaraciones al spec detectadas en la implementación (pendientes de aprobar)
- `RentCharge` necesita el campo `Type` (`Rent`/`Other`); `PeriodYear/PeriodMonth` serían opcionales para `Other`.
- El estado `Overdue` se deriva al leer con la fecha local de la organización; no se persiste.
- Los descuentos/ajustes no tienen entidad definida; propuesta para el MVP: anular con motivo y crear un cargo nuevo.
- `MaintenanceIssue` requiere `ResolvedByUserId` y una entidad de historial de eventos.

### Supuestos iniciales recomendados
- Moneda inicial MXN.
- Zona horaria configurable por organización.
- Día de vencimiento entre 1 y 28.
- Sin prorrateo automático, mora automática ni facturación fiscal.
- Pagos registrados manualmente.
- Operaciones financieras confirmadas se conservan; correcciones mediante reversos/ajustes.
- El producto se centra en propiedades en renta.

## 16. Definition of Done

Una funcionalidad está terminada solo cuando:
- Cumple requisito y reglas de negocio.
- Valida permisos y organización en servidor.
- Tiene estados de carga, vacío, éxito y error en interfaz.
- Incluye pruebas unitarias o de integración apropiadas.
- Maneja transacciones y concurrencia cuando hay dinero.
- Registra auditoría si la operación es sensible.
- Actualiza OpenAPI y esta especificación cuando cambia el contrato.
- No expone secretos ni datos de otra organización.
- Tiene instrucciones de ejecución/migración actualizadas.

## 17. Instrucciones para agentes de IA

Antes de implementar una tarea:
1. Leer esta especificación completa.
2. Identificar requisitos, entidades y pruebas afectadas.
3. Si existe contradicción o decisión pendiente, explicarla y pedir aprobación; no inventar políticas financieras.
4. Implementar validación en backend y reflejarla en frontend.
5. Añadir pruebas de casos correctos y negativos.
6. Actualizar este documento si cambia una regla aprobada.
7. No declarar una función terminada si solo existe una pantalla sin persistencia y API real.

**Nota:** esta es una especificación base v1.0 para iniciar el MVP, no asesoría fiscal ni legal. Las decisiones pendientes deben aprobarse antes de usar el sistema para cobros reales o como registro financiero oficial.
