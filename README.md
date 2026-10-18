# Habita

SaaS de administración de propiedades en renta: propiedades y unidades, inquilinos, contratos, cargos, pagos, mantenimiento, reportes y auditoría.
La fuente de verdad funcional es [Habita_SPEC.md](Habita_SPEC.md); las reglas aprobadas y pendientes de aprobar viven ahí.

**Stack:** ASP.NET Core 10 (API) · EF Core + PostgreSQL 16 · Next.js 16 + TypeScript + Tailwind · Docker Compose (Caddy para HTTPS).

```
src/Habita.Domain          entidades, estados e invariantes (sin dependencias)
src/Habita.Application     casos de uso, DTOs y contratos
src/Habita.Infrastructure  EF Core, PostgreSQL, Identity, bloqueos de dinero
src/Habita.Api             API HTTP, autorización, seguridad, comandos de operación
src/Habita.Worker          recordatorios diarios y envío de avisos (correo y push)
tests/                     unitarias, arquitectura e integración (PostgreSQL real con Testcontainers)
web/                       interfaz Next.js
ops/                       respaldo, prueba de restauración y Caddyfile
PDF                        Gotenberg (contenedor): contratos, recibos y estados de cuenta
docs/openapi.v1.json       contrato OpenAPI (instantánea de la API en Development)
```

## Arranque rápido

```bash
./dev.sh --seed     # DESARROLLO: PostgreSQL + Mailpit (Docker), API, worker y web; --seed crea los datos de demostración
./start.sh          # PRODUCCIÓN con Docker Compose (HTTPS, respaldos); sin .env.production crea una prueba local
```

`./dev.sh --stop` apaga PostgreSQL y Mailpit; `./start.sh --stop` apaga la producción y `./start.sh --logs` muestra sus logs. Ambos eligen un puerto libre si el habitual está ocupado y te dicen cuál.
Para un servidor real, completa `.env.production` a partir de `.env.production.example` (ver *Producción*).

## Desarrollo

```bash
cp .env.example .env                      # ajusta POSTGRES_PASSWORD (y POSTGRES_PORT si el 5432 está ocupado)
docker compose up -d                      # PostgreSQL + Mailpit (correo de prueba en http://localhost:8025)
dotnet run --project src/Habita.Api       # aplica las migraciones al arrancar (solo Development)
cd web && pnpm install && pnpm dev        # http://localhost:3000
```

- API: `GET /health`. OpenAPI (solo Development): `/openapi/v1.json`.
- Si el puerto de PostgreSQL no es el 5432, pasa `ConnectionStrings__Default="Host=localhost;Port=5434;Database=habita;Username=habita;Password=..."`.
- Los enlaces de los correos usan `Web__BaseUrl` (por defecto `http://localhost:3000`).
- **Contratos, recibos y estados de cuenta en PDF:** los hace Gotenberg, que `./dev.sh` levanta con Docker (la primera vez descarga su imagen, que es grande; el puerto aparece al arrancar). La plantilla de contrato se sube en *Ajustes → Plantilla de contrato* (hay un ejemplo descargable con todos los campos `{{así}}`). Sin Gotenberg el resto de Habita funciona; los PDF responden 503.
- **Avisos y recordatorios:** los hace el worker (`dotnet run --project src/Habita.Worker`, ya incluido en `./dev.sh`). Los correos de aviso se ven en Mailpit. Los recordatorios corren una vez al día, a partir de la *hora de recordatorios* de *Ajustes*. Los avisos en el dispositivo (Web Push) son opcionales: `dotnet run --project src/Habita.Api -- --generate-vapid` imprime el par de llaves (`Push__VapidPublicKey`, `Push__VapidPrivateKey`, `Push__Subject`); además solo funcionan en la versión de producción (el service worker no se registra en desarrollo).

### Pruebas

```bash
dotnet test Habita.sln        # unitarias, arquitectura e integración (PostgreSQL real con Testcontainers; requiere Docker)
./web/e2e/run.sh              # extremo a extremo con Playwright en Chrome, sobre una pila temporal (ver web/e2e/README.md)
```

`tests/Habita.IntegrationTests/AuthorizationMatrix.txt` es la **matriz de autorización**: cada ruta de la API contra cada rol. La prueba falla si cualquier política cambia; si el cambio es intencional, regenera el archivo con `UPDATE_AUTH_MATRIX=1 dotnet test --filter AuthorizationMatrix` y revisa el diff antes de aprobarlo.

### Datos de demostración

```bash
dotnet run --project src/Habita.Api -- --seed-demo
```

Crea datos **ficticios** del §14 del spec: *Residencial Demo* (Torre Norte, unidades 101/102/201, Ana Ejemplo y Luis Prueba, renta de $8,500 MXN)
con un cargo vencido, uno parcialmente pagado, un pago revertido, un contrato terminado, un pago con saldo a favor, una incidencia, una cuenta bancaria, una promesa de pago y un reporte de pago del inquilino esperando revisión, más un usuario por rol
y una segunda organización para probar UUID ajenos. Contraseña de todos los usuarios: `Habita-Demo-2026!`
(`propietario@`, `admin@`, `finanzas@`, `mantenimiento@`, `lectura@`, `cobrador@demo.example`, y `ana@demo.example`, la inquilina Ana con acceso al portal). Es idempotente y **se niega a correr en producción**.

### Pruebas y calidad

```bash
dotnet test                               # requiere Docker (Testcontainers levanta PostgreSQL)
cd web && pnpm lint && pnpm exec tsc --noEmit
```

Migraciones: `dotnet ef migrations add <Nombre> -p src/Habita.Infrastructure -s src/Habita.Api -o Persistence/Migrations`.

## Producción

El stack completo está en `docker-compose.prod.yml`: **PostgreSQL** y **API** solo en red interna, **web** (Next.js), **Caddy** (único servicio que publica puertos, con certificados
automáticos y redirección a HTTPS), un servicio de **respaldos** y un **worker** (recordatorios diarios y envío de correo y push; puede haber varias copias) y **Gotenberg** (PDF; solo red interna). El worker y la API salen a internet por una red aparte (`egress`) que no publica puertos: solo para correo y servicios push. Un trabajo `migrate` aplica las migraciones antes de arrancar la API.

```bash
cp .env.production.example .env.production      # completa TODOS los valores (dominio, contraseña de BD, SMTP, correo ACME)
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
```

Requisitos: un dominio cuyo DNS apunte al servidor y los puertos 80 y 443 abiertos. Genera secretos con `openssl rand -hex 24` (sin `;` ni espacios en la contraseña de BD).
La API **se niega a arrancar** en producción con una configuración insegura (contraseña de ejemplo, origen `http://`, `AllowedHosts=*`, tokens de invitación expuestos, sin SMTP, sin
carpeta de claves escribible…) y lista cada problema. El primer usuario se crea en `/register` (queda como Owner de su organización); después invita al equipo desde *Miembros*.

Actualizar: `git pull && docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build` (las migraciones corren solas). Logs: `docker compose ... logs -f api`
(JSON estructurado; solo llevan ids de organización y usuario, nunca nombres, correos ni cuerpos de solicitud).

### Respaldos y restauración

- **Automático:** el servicio `backup` hace un `pg_dump` **y un `.files.tar.gz` con los archivos subidos** (comprobantes de pago) cada `BACKUP_INTERVAL_HOURS` (24 por defecto) en `BACKUP_PATH`, valida que se puedan leer, guarda el SHA-256 de cada uno y borra los
  de más de `BACKUP_RETENTION_DAYS` (14). Los archivos se respaldan *antes* que la base, así todo archivo que la base menciona ya está en el respaldo. Cada domingo restaura el último en una base temporal y verifica esquema, protecciones (incluidas las de cobranza, avisos, documentos y depósitos), totales y que **cada archivo subido que la base menciona esté en el respaldo con su huella SHA-256 intacta** (la **prueba de restauración**). Queda *unhealthy* si no hay un respaldo reciente.
- **Manual:** `docker compose -f docker-compose.prod.yml --env-file .env.production exec backup bash /ops/backup.sh` y `... exec backup bash /ops/restore-test.sh [archivo]`.
- **Recuperar de verdad** (la base actual se reemplaza): detén la API (`stop api web`), y
  `docker compose ... exec -T backup bash -c 'pg_restore --clean --if-exists --no-owner -d "$PGDATABASE" /backups/<archivo>.dump'`; luego los archivos subidos con el mismo sello de tiempo:
  `docker run --rm -v habita_files:/data/files -v "$BACKUP_PATH":/backups alpine tar -xzf /backups/<archivo>.files.tar.gz -C /data/files`; y `start api web`.
- Copia los respaldos **fuera del servidor** (otra máquina o almacenamiento de objetos): un respaldo en el mismo disco no protege de perder el disco. Los archivos contienen datos personales y financieros; protégelos.

### Operación

| Comando | Qué hace |
|---|---|
| `dotnet Habita.Api.dll --migrate` | Aplica migraciones y termina (lo usa el trabajo `migrate`). |
| `dotnet Habita.Api.dll --seed-demo` | Datos de demostración. Solo Development. |
| `dotnet Habita.Api.dll --generate-vapid` | Imprime un par de llaves VAPID para los avisos en el dispositivo. |

## Seguridad: qué está aplicado

- **Aislamiento por organización** en cada consulta (filtro global), en cada escritura (interceptor) y en la base (llaves foráneas compuestas). Un recurso ajeno responde 404.
- **Autorización en el servidor** por rol; Maintenance nunca recibe datos financieros ni de inquilinos.
- **Sesión:** cookie `HttpOnly`, `SameSite=Lax` y `Secure` en producción; bloqueo de cuenta tras 5 contraseñas fallidas; límite de intentos por IP en login, recuperación, invitaciones y registro (429 con `Retry-After`).
- **CSRF:** `SameSite` más verificación del encabezado `Origin` en solicitudes que modifican datos.
- **Cabeceras:** HSTS, CSP, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, `Cache-Control: no-store` en la API. Límite de 1 MB por solicitud.
- **Dinero:** `numeric` en PostgreSQL, transacciones con bloqueo de filas, idempotencia en pagos y triggers que impiden editar o borrar pagos, aplicaciones, auditoría e historial de incidencias.
- **Archivos subidos:** tipo detectado por contenido, 5 MB, nombre aleatorio, descarga como adjunto, y un tope de 200 archivos por persona cada 24 horas además del límite por minuto.
- **Cobranza:** los reportes de pago atendidos y los códigos de efectivo usados no se modifican ni se borran (disparadores); confirmar un reporte o cobrar con un código es atómico y no duplica pagos aunque lleguen solicitudes simultáneas; los códigos de efectivo vencen en 4 horas, son de un solo uso y solo funcionan con sesión de personal. La cámara solo se permite al propio origen para escanear.
- **Documentos:** los contratos generados, los folios de recibo y las plantillas no se modifican ni se borran (disparadores); cada contrato es una versión nueva con su huella SHA-256. Los folios se asignan dentro de la transacción del pago (únicos y sin huecos). Las plantillas Word subidas se revisan antes de abrirlas (macros, objetos incrustados, enlaces externos, bombas zip) y Gotenberg corre en la red interna, sin internet ni puerto publicado.
- **Depósitos e importación:** las liquidaciones de depósito y sus descuentos no se modifican ni se borran (disparadores; solo se anulan con motivo); el depósito cobrado es custodia y no cuenta como ingreso; el Excel/CSV importado se revisa con una vista previa, se valida contra macros, objetos incrustados, enlaces externos y bombas zip, se guarda en una sola transacción y **nunca se conserva**. La auditoría registra solo cantidades.
- **CSV:** neutralización de fórmulas (inyección en Excel) y registro en la auditoría de cada exportación.
- **Contenedores:** API y web sin privilegios, sistema de archivos de solo lectura, sin capacidades, red interna para la base de datos.

### Antes de usarlo con cobros reales

Habita_SPEC.md §15 lista las decisiones de negocio que **deben aprobarse** (prorrateo, depósito, mora, comprobantes fiscales, retención de datos, notificaciones…) y el resumen de lo implementado por defecto.
Esto no es asesoría fiscal ni legal. Pendientes técnicos conocidos: CSP basada en *nonces* (hoy permite `unsafe-inline` por Next.js), SPF/DKIM del dominio de correo, y pruebas de carga.
