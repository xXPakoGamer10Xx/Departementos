# Plan: traer lo mejor de NethRent a Habita

**Origen revisado (solo lectura):** `/Users/franciscotapia/Documents/Departamentos` (NethRent: Express + PostgreSQL + Expo/React Native; ~33 inquilinos reales, un solo arrendador).
**Destino:** Habita (ASP.NET Core + Next.js), con los 9 hitos del MVP ya construidos.
**No se tocó nada de NethRent ni se leyeron datos personales** (solo estructura, rutas, esquema y encabezados de sus archivos).

## 1. Decisiones ya tomadas

| Tema | Decisión |
|---|---|
| Portal del inquilino | **Sí**, como **web instalable (PWA)** dentro de Habita, con un rol `Tenant` que solo ve lo suyo |
| Paquetes a traer | **Los cuatro**: Cobranza · Avisos · Documentos · Datos del negocio |
| Rol de cobrador | **Rol nuevo `Collector` (Cobrador)**, no permisos sueltos |
| Lectura de INE con IA | **No se copia** (el spec prohíbe guardar identificaciones y enviarlas a terceros es un riesgo de privacidad) |

## 2. Qué se copia, qué se adapta y qué NO

| NethRent tiene | En Habita | Cómo |
|---|---|---|
| Portal del inquilino (estado de cuenta, pagos, tickets, "mi depto") | **Adaptar** | PWA + rol `Tenant` + endpoints `/portal/*` que filtran por la identidad, nunca por un id del cliente |
| Códigos de invitación cortos (10 caracteres, caducan, un solo uso) | **Adaptar** | Complementan las invitaciones por correo; sirven para compartir por WhatsApp |
| Comprobante subido por el inquilino, confirmar o rechazar con comentario | **Adaptar** | `PaymentClaim` ("reporte de pago"): al confirmar crea un `Payment` real con las reglas actuales (idempotencia, bloqueos, auditoría) |
| QR de pago en efectivo + cobrador que escanea | **Adaptar** | Token de un solo uso y corta vida; el escaneo **exige sesión de personal** (el endpoint público de NethRent no se copia) |
| Promesas de pago con recordatorio | **Copiar** (modelo propio) | `PaymentPromise` con estado derivado (cumplida / incumplida) |
| Recordatorios programados (renta, contrato por vencer, depósito, promesa) | **Adaptar** | `Habita.Worker` real, por organización y en su zona horaria, idempotente |
| Notificaciones en tiempo real (SSE) y push | **Adaptar** | Centro de notificaciones + SSE + Web Push (no Expo, por ser PWA) + correo |
| Cuentas bancarias, CLABE, banco por prefijo, cuenta por departamento | **Copiar** | Con validación del dígito verificador de la CLABE y catálogo de bancos |
| Contrato Word/PDF desde plantilla con campos | **Adaptar** | DOCX con tus campos + PDF vía Gotenberg; cada versión generada queda inmutable |
| "Renta con letra" | **Copiar** | En el dominio, con pruebas exhaustivas ("TRES MIL QUINIENTOS PESOS 00/100 M.N.") |
| Recibo imprimible sin datos internos | **Adaptar** | Folio por organización, cancelado si el pago se revierte; **no es CFDI** |
| Depósito en abonos con fechas diferidas | **Adaptar** | Tipo de cargo `Deposit` aparte del ingreso; resuelve la decisión #2 del spec (pendiente de tu aprobación) |
| Inventario del departamento y fiador | **Copiar** | Inventario como acta de entrega por contrato; fiador opcional con datos mínimos |
| Días de gracia | **Adaptar** | Ajuste por organización; cambia cuándo un cargo cuenta como vencido |
| Importar tu Excel de inquilinos | **Nuevo** | Asistente con vista previa y reporte de errores (tu archivo es el caso de prueba real) |
| Auditoría, respaldos, límite de intentos, helmet | Ya existe y es **mejor** en Habita | No se copia |
| **Abonos editables y eliminables** | **NO** | Rompe la regla "un pago confirmado no se edita": Habita usa reversos con motivo |
| `ON DELETE CASCADE` de inquilino a pagos | **NO** | Habita nunca borra historial financiero |
| Migraciones destructivas sin versionar | **NO** | Habita usa migraciones EF versionadas |
| Sesión JWT de 7 días | **NO** | Habita usa cookie HttpOnly de 12 h |
| IA (Gemini) para procesar plantillas e INE | **NO** | Privacidad y costo; las plantillas se editan a mano |
| Permisos sueltos por colaborador | **NO** | Rol `Collector` fijo (decisión tuya) |

## 3. Principios que NO se negocian (vienen del spec)

Aislamiento por organización en cada consulta y en la base · la API es la autoridad · dinero exacto · nada financiero se borra ni se edita (se revierte) · transacciones atómicas e idempotencia · auditoría de lo sensible · privacidad: mínimo de datos personales. Cada hito cumple la Definition of Done de siempre (pruebas positivas y negativas, concurrencia probada con mutación, estados de carga/vacío/error, spec actualizado).

## 4. Hitos propuestos (en orden de dependencia)

Tamaño: S pequeño · M mediano · L grande.

### Hito 10 — Acceso y base compartida (L) — ✅ hecho
La base de todo lo demás y la parte más delicada en seguridad.
- **Almacenamiento privado de archivos**: volumen dedicado, tipo verificado por contenido (no por extensión), máximo 5 MB, nombre aleatorio, descarga solo con autorización, `nosniff`, hash SHA-256. Lo usan comprobantes, contratos y recibos.
- **Ajustes por organización** (`/settings`, Owner/Admin): nombre y domicilio del arrendador, días de recordatorio, días de gracia, avisos de vencimiento de contrato, métodos de pago aceptados.
- **Rol `Collector`**: registra y confirma pagos, escanea QR y ve inquilinos con datos mínimos; **no** ve contratos, reportes, miembros ni auditoría, y **no** revierte.
- **Rol `Tenant`** con vínculo `UserId ↔ TenantId`. Hoy la política `InOrganization` deja pasar a cualquier rol, así que se reemplaza por una política `Staff` en cada endpoint existente.
- **Prueba que enumera TODAS las rutas** y exige 403 para `Tenant` salvo `/portal/*`, `/me` y autenticación. Es la red de seguridad contra olvidar un endpoint.
- **Códigos de invitación cortos** ligados a un inquilino o rol, de un solo uso, con caducidad y límite de intentos; botón para compartir por WhatsApp (enlace `wa.me`, sin integrar la API).
- *Pruebas*: matriz rol × endpoint completa, fuerza bruta de códigos, reutilización y caducidad, aislamiento entre organizaciones.

### Hito 11 — Portal del inquilino (PWA) (L) — ✅ hecho
- `/portal`: **mi departamento y contrato**, **estado de cuenta** (cargos con saldo, vencidos, saldo a favor), **historial de pagos**, **reportar una falla**.
- El inquilino ve **solo lo suyo**; nunca otros inquilinos. Las incidencias que crea quedan abiertas y el personal las atiende; los comentarios internos tienen visibilidad `Internal` y no le llegan.
- **PWA**: manifest, íconos, instalable, caché del cascarón sin cachear datos de API, diseño de una columna en móvil.
- *Pruebas*: un inquilino no puede leer cargos, pagos, contratos ni incidencias de otro inquilino de la **misma** organización; ni de otra.

### Hito 12 — Cobranza: comprobantes, promesas, QR y cuentas (L) — ✅ hecho
> **Cómo quedó (diferencias con lo propuesto):** el comprobante es obligatorio en todo reporte (el efectivo va por el QR); las promesas las crea y cancela solo el personal (el inquilino aún no las ve); el código de efectivo es de 10 caracteres (también se puede escribir) y vence en 4 horas; la pantalla del cobrador es `/collect` y la cola de reportes es `/claims`; las cuentas se administran en *Ajustes → Cuentas bancarias*. Reglas completas en `Habita_SPEC.md` (Hito 12). Pendiente para el Hito 13: avisar al personal de reportes nuevos y al inquilino de la resolución.

- **Cuentas bancarias**: titular, banco detectado por el prefijo de la CLABE, CLABE validada con su dígito verificador, predeterminada y por propiedad. El inquilino ve solo la cuenta de su unidad.
- **Reporte de pago con comprobante**: el inquilino sube imagen o PDF; el personal confirma (elige las aplicaciones y se crea el `Payment`) o rechaza con comentario visible. Dos personas confirmando el mismo reporte a la vez producen **exactamente un pago**.
- **Promesa de pago**: fecha y nota por cargo; se marca cumplida si el cargo se paga a tiempo e incumplida si no.
- **QR de pago en efectivo**: el inquilino genera un token de un solo uso (caduca en horas); el cobrador lo escanea con la cámara del navegador, ve cargos y registra el pago con idempotencia ligada al token.
- **Pantalla móvil del cobrador**: "Cobrar" (escanear/confirmar) y cola de comprobantes pendientes.
- *Pruebas*: ciclo de vida del reporte, concurrencia (con prueba de mutación), archivos falsificados (`.jpg` que es otra cosa) y de más de 5 MB, descarga solo con permiso, token reutilizado o caducado, límite de intentos, dígito verificador de CLABE.

### Hito 13 — Avisos: notificaciones y recordatorios (L) — ✅ hecho
> **Cómo quedó (diferencias con lo propuesto):** todo aviso que habla de una persona **empieza por su nombre y luego sus apellidos** (petición del usuario). La renta por vencer avisa al inquilino; los cargos vencidos (1, 7, 14 y 30 días) y los contratos por terminar avisan al inquilino y a Owner/Admin/Finance; el Cobrador no recibe avisos de oficina. La hora de los recordatorios es un ajuste por organización (9:00 por defecto). El aviso en el dispositivo lleva solo el título, sin importes. El recordatorio de depósito diferido espera al Hito 15. El `worker` ya tiene su Dockerfile y servicio en `docker-compose.prod.yml` (el Hito 16 lo revisa junto con el resto de producción). Reglas completas en `Habita_SPEC.md` (Hito 13).
- **Centro de notificaciones** (campana con no leídas) por usuario.
- **Canales**: en la app, correo y **Web Push** (VAPID). SSE para tiempo real. Preferencias por usuario.
- **`Habita.Worker`** real: corre a una hora local fija por organización, idempotente (clave de deduplicación única) y seguro con varias instancias (bloqueo de PostgreSQL).
- **Recordatorios**: renta por vencer (3, 1 y 0 días, configurable), cargo vencido, contrato por vencer (30, 7 y 1 días), promesa de pago, depósito diferido (cuando exista el Hito 15).
- **Avisos al personal**: nuevo reporte de pago, nueva incidencia del inquilino, pago confirmado o rechazado (al inquilino).
- *Pruebas*: reloj simulado y zonas horarias, no duplicar al reejecutar ni con dos workers, cada usuario solo recibe lo suyo por SSE.

### Hito 14 — Documentos: contratos y recibos (L) — ✅ hecho
> **Cómo quedó (diferencias con lo propuesto):** se conservó el vocabulario `{{campo}}` de NethRent para que tu Word siga sirviendo, y se rechazan los campos que no existen en vez de imprimirlos vacíos. Fiador, inventario y fechas del depósito se llenan desde el Hito 15. El recibo se dibuja al pedirlo (así el sello CANCELADO sale solo si el pago se revierte) y su folio se asigna dentro de la transacción del pago (consecutivo y sin huecos); no hay «saldo del cargo» en el recibo, solo el saldo a favor de ese pago (el estado de cuenta da los saldos). El estado de cuenta es un PDF a la fecha. El inquilino puede bajar su contrato más reciente. Reglas completas en `Habita_SPEC.md` (Hito 14).
- **Plantilla de contrato** DOCX propia de la organización con campos (inquilino, unidad, domicilio, renta, **renta con letra**, día de pago, fechas, depósito, fiador, arrendador, inventario). Compatible con tus campos de Word.
- **Versiones de contrato inmutables**: cada generación guarda DOCX y PDF, hash, quién y cuándo; regenerar crea otra versión.
- **PDF con Gotenberg** (servicio interno, LibreOffice y Chromium) para contratos y recibos.
- **Recibo por pago** con folio por organización, desglose de lo aplicado y saldo; sin datos bancarios ni comentarios internos; se marca **CANCELADO** si el pago se revierte. **No es factura (CFDI)**.
- **Estado de cuenta imprimible** por inquilino.
- *Pruebas*: número con letra (casos exhaustivos), plantilla con campos partidos entre fragmentos de Word, inmutabilidad de versiones, unicidad de folios bajo concurrencia, el inquilino descarga solo sus recibos.

### Hito 15 — Datos del negocio (L) — ✅ hecho
> **Cómo quedó (diferencias con lo propuesto):** el **depósito** es un cargo de tipo `Deposit` por abono (se programa con fechas que suman exactamente lo del contrato); lo cobrado se muestra como **custodia** y no entra en «recibido» ni en los meses del reporte. La **liquidación** solo se hace con el contrato terminado o cancelado y con todos los abonos cobrados o anulados; es inmutable y solo se anula con motivo (queda en el historial). Los motivos de cada descuento no van a la auditoría (solo importes). El **inventario** se captura mientras el contrato está vigente y la devolución se registra al terminar. Los **días de gracia** (0 a 30) mueven la línea de «vencido» en cargos, reportes, tablero, portal, estado de cuenta, cobro en efectivo y avisos (los avisos de atraso cuentan desde que termina la gracia; la antigüedad de saldos sigue contando desde la fecha de pago). El **importador** lee .xlsx (hoja «Inquilinos» o la primera con encabezados) y .csv; las columnas se reconocen por significado (sin importar acentos ni mayúsculas); «Con letra» se ignora porque Habita la calcula; el primer teléfono es el del inquilino y los demás van a sus notas; sin día de pago se usa el del inicio (máx. 28). La vista previa no guarda nada; confirmar vuelve a leer el archivo y exige la huella de la vista previa, y todo se guarda en **una transacción** (o nada). Repetir la importación no duplica (mismo inquilino + unidad + inicio = «ya existe»). El archivo nunca se guarda. Máximo 500 filas y 5 MB; solo Propietario y Administrador. Las comprobaciones de seguridad de archivos Office (macros, objetos incrustados, enlaces externos, bombas zip) se compartieron entre la plantilla de contrato y el importador.
- **Importador de Excel/CSV** (tu hoja *Inquilinos*: nombre, depto, renta, con letra, fecha de pago, inicio, término, fiador, teléfonos, observaciones): vista previa, validaciones, detección de duplicados, reporte de errores y confirmación en una transacción. No conserva el archivo ni se versionan tus datos reales en el repositorio.
- **Depósitos**: tipo de cargo `Deposit` con calendario de abonos; se reporta como **depósitos en custodia**, no como ingreso; devolución al terminar el contrato con deducciones y motivo.
- **Inventario** del departamento y acta de entrega por contrato (estado: bueno / regular / malo).
- **Fiador** opcional (nombre y teléfono).
- **Días de gracia**: ajuste por organización que desplaza cuándo un cargo es "vencido" (cambia el cálculo en cargos, reportes y dashboard).
- *Pruebas*: importación con filas buenas y malas, repetirla no duplica, límites de los días de gracia, depósito fuera del ingreso, flujo de devolución.

### Hito 16 — Cierre y endurecimiento (M) — ✅ hecho
> **Cómo quedó:** la compose de producción ya tenía worker, Gotenberg, volumen de archivos y VAPID desde los hitos 13–14; este hito agregó el respaldo y la verificación de los archivos subidos, la suite E2E (`web/e2e`, corre en local con `./web/e2e/run.sh`), la matriz de autorización versionada y la revisión de seguridad (un hallazgo corregido: tope de archivos por persona). Detalle y pendientes en el spec («Cierre y endurecimiento»).
- **Producción**: servicios `worker` y `gotenberg`, volumen de archivos, claves VAPID; el respaldo incluye los archivos subidos y la prueba de restauración los verifica.
- **Pruebas de extremo a extremo con Playwright** de los flujos nuevos (inquilino sube comprobante → personal confirma; QR de efectivo; recordatorio).
- **Revisión de seguridad** de las superficies nuevas (subidas, tokens, SSE, portal) y actualización del README, spec y OpenAPI.

## 5. Cambios al spec que necesitan tu aprobación

1. **Portal del inquilino y PWA** entran al alcance (hoy figuran como fuera del MVP).
2. **Depósito** deja de ser solo dato del contrato (decisión #2): pasa a cargo propio con calendario y devolución.
3. **Días de gracia** (modifica la definición de "vencido" del §5/§6).
4. **Recibos internos** (decisión #6) con folio; CFDI sigue fuera.
5. **Notificaciones** (decisión #10): correo, push y en la app; WhatsApp sigue fuera (solo enlaces para compartir).
6. **Retención de los comprobantes subidos**: cuánto tiempo se guardan, quién puede borrarlos (los pagos no se borran, pero un archivo sí podría purgarse).
7. **Aviso de privacidad** para el portal (consentimiento y derechos ARCO): requiere revisión legal antes de dar acceso a inquilinos reales.

## 6. Riesgos y cómo se controlan

| Riesgo | Control |
|---|---|
| Un endpoint viejo queda abierto al `Tenant` | Prueba que enumera todas las rutas; la política `InOrganization` se elimina |
| Subidas maliciosas | Tipo por contenido, tamaño, nombre aleatorio, descarga autorizada, sin ejecución, CSP |
| Doble confirmación o doble cobro con QR | Bloqueo de filas + idempotencia + pruebas de concurrencia con mutación |
| Spam o duplicados de avisos | Clave de deduplicación única + bloqueo del worker |
| Push en iPhone | Requiere instalar la PWA (iOS 16.4 o superior); se documenta |
| Más servicios que operar (worker, Gotenberg) | Docker Compose, chequeos de salud, mismo `./start.sh` |
| Datos reales de tus inquilinos | Solo importación local desde tu archivo; ningún dato real entra al repositorio ni a las pruebas |

## 7. Cómo verifico cada hito

Igual que en los 9 anteriores: pruebas unitarias + integración contra PostgreSQL real, **pruebas de mutación** donde hay concurrencia o seguridad, recorrido en Chrome con capturas, revisión de diseño, spec y README actualizados, y todo sin commit hasta que tú lo pidas.

## 8. Notas sobre tu app actual (las encontré al leerla; no las toqué)

- `migrate.ts` empieza con un `DROP TABLE ... CASCADE`: hoy lo protege que `start.sh` solo lo corre en el primer despliegue o con `--reset`, pero cualquier ejecución manual de `npm run migrate` borra todo.
- Los cambios de esquema posteriores viven como scripts sueltos `patch_*`, sin historial de versiones.
- Hay archivos en git que conviene revisar: `.cargas_tmp.txt` (54 KB, temporal) y `frontend/google-services.json`.
- Mover tus datos a Habita (Hito 15) resuelve las tres cosas de una vez.

## 9. Siguiente paso

Si apruebas el plan empiezo por el **Hito 10**, que es la base de seguridad de todo lo demás. Si quieres cambiar el orden, el único requisito duro es: 10 → 11 → 12/13, y 14 y 15 pueden ir en cualquier orden después.
