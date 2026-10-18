# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
Dueños individuales de propiedades en renta y equipos de empresas administradoras, en México, por igual. Trabajan a diario en escritorio y a veces en celular, con roles distintos: Owner, Admin, Finance, Maintenance, ReadOnly y Collector (cobrador en campo). Los inquilinos entran a un portal propio. Maintenance nunca ve información financiera.

## Product Purpose
Habita es un SaaS de administración de propiedades en renta. Su trabajo principal es hacer clara la cobranza: saber quién debe qué, registrar pagos completos y parciales y ver saldos y vencimientos sin errores. Contratos, inquilinos, unidades y mantenimiento son soporte. Éxito: el usuario confía en los números y no necesita una hoja de cálculo aparte.

## Positioning
Control financiero exacto y auditable sobre rentas: los pagos nunca se editan ni se borran, se revierten con motivo, y todo cambio sensible queda registrado.

## Operating Context
Español de México, moneda MXN (configurable por organización), zona horaria por organización. Un usuario puede pertenecer a varias organizaciones y elegir la activa. Fuente de verdad funcional: Habita_SPEC.md.

## Capabilities and Constraints
Stack: Next.js 16 + TypeScript + Tailwind v4 en web/, API ASP.NET Core. Construido: autenticación, organizaciones, miembros e invitaciones, propiedades y unidades, inquilinos, contratos, cargos, pagos con reversos, mantenimiento, reportes, auditoría, roles Cobrador e Inquilino, portal del inquilino instalable (PWA) y cobranza (cuentas bancarias, reportes de pago con comprobante, promesas de pago y cobro en efectivo con QR). También hay avisos y recordatorios (en la app, por correo y, opcionalmente, en el dispositivo), que empiezan por el nombre de la persona. También hay contratos en Word y PDF desde tu plantilla, recibos con folio y estados de cuenta en PDF. Falta la importación de datos, fiador, inventario y depósitos. No hay logo ni identidad de marca definidos.

## Brand Commitments
Nombre: Habita. El usuario pidió un diseño "profesional y limpio". No hay otros compromisos de marca.

## Evidence on Hand
Sin testimonios, clientes ni métricas reales; no inventarlos.

## Product Principles
- Los números financieros son lo más importante de cada pantalla y deben leerse sin ambigüedad.
- Nada financiero se oculta ni se edita en silencio: estados, motivos y auditoría son visibles.
- La seguridad la aplica el servidor; la interfaz solo refleja permisos.
- Cada pantalla tiene estados de carga, vacío, error y éxito.
