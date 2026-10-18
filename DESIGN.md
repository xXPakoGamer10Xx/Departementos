---
name: Habita
description: Administración de propiedades en renta con la claridad de un talonario de recibos.
colors:
  paper: "#f2f4f3"
  paper-deep: "#e8ecea"
  leaf: "#ffffff"
  ink: "#13202b"
  ink-soft: "#46555f"
  ink-faint: "#5d6c76"
  rule: "#d5dfdb"
  rule-strong: "#b6c5bf"
  accent: "#1d3fb8"
  accent-hover: "#162f8c"
  accent-soft: "#e8edfb"
  stamp: "#b3261e"
  stamp-soft: "#fbeae8"
  ledger: "#1b6b4a"
  ledger-soft: "#e4f2ea"
  pending: "#855400"
  pending-soft: "#fbf0d9"
typography:
  headline:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "24px"
    fontWeight: 600
    lineHeight: 1.25
    letterSpacing: "-0.025em"
  title:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "20px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "-0.025em"
  body:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 500
    lineHeight: 1.4
  caption:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
  folio:
    fontFamily: "Geist Mono, ui-monospace, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
rounded:
  sm: "4px"
  md: "6px"
  lg: "8px"
  pill: "9999px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "32px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.leaf}"
    rounded: "{rounded.md}"
    height: "40px"
    padding: "0 16px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
  button-secondary:
    backgroundColor: "{colors.leaf}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    height: "40px"
    padding: "0 16px"
  button-danger:
    backgroundColor: "{colors.leaf}"
    textColor: "{colors.stamp}"
    rounded: "{rounded.md}"
    height: "32px"
    padding: "0 12px"
  input:
    backgroundColor: "{colors.leaf}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    height: "40px"
    padding: "0 12px"
  leaf:
    backgroundColor: "{colors.leaf}"
    rounded: "{rounded.lg}"
  badge-active:
    backgroundColor: "{colors.ledger-soft}"
    textColor: "{colors.ledger}"
    rounded: "{rounded.sm}"
    padding: "2px 8px"
  nav-item-active:
    backgroundColor: "{colors.leaf}"
    textColor: "{colors.accent}"
    rounded: "{rounded.md}"
    height: "36px"
---
# Design System: Habita

## Overview

**Creative North Star: "El talonario"**

Habita se ve como un talonario de recibos bien llevado: hojas blancas sobre papel frío, renglones finos, folios y números tabulares. El dinero se lee como en un libro de cuentas, sin adornos de panel de control. La interfaz es una herramienta de trabajo diario para dueños y equipos administradores; la confianza viene de la exactitud y la quietud, no de la expresión.

Un solo color de marca, el azul de copia al carbón, se reserva para lo que el usuario puede hacer o dónde está. Todo lo demás es tinta y papel. Las sombras no existen: la profundidad la dan las hojas con borde de renglón y la perforación que separa encabezado y cuerpo.

**Key Characteristics:**
- Tema claro único (escena: oficina de día).
- Hojas blancas con borde de 1px y esquinas de 8px; sin sombras.
- Perforación punteada con muescas como firma del sistema.
- Geist para todo; Geist Mono solo para folios e identificadores.
- Un acento, usado con escasez; rojo, verde y ámbar solo como semántica de estado.

## Colors

Papel frío y tinta azul-negra, con un único azul de acción y tres colores de estado.

### Primary
- **Carbon-Copy Blue** (#1d3fb8): acciones primarias, enlace de acción, elemento de navegación activo, anillo de foco y selección de texto. Pulsado: **Pressed Carbon Blue** (#162f8c). Lavado: **Blue Wash** (#e8edfb) para hover de filas seleccionables.

### Secondary
- **Ledger Green** (#1b6b4a) sobre **Ledger Wash** (#e4f2ea): estados correctos (activo, pagado, éxito).
- **Pending Amber** (#855400) sobre **Amber Wash** (#fbf0d9): estados pendientes. Definido, aún sin uso en pantalla.

### Tertiary
- **Stamp Red** (#b3261e) sobre **Stamp Wash** (#fbeae8): errores, reversos y acciones destructivas.

### Neutral
- **Cool Paper** (#f2f4f3): fondo de página. **Deep Paper** (#e8ecea): riel lateral, encabezado móvil, cabecera de tablas y placeholders de carga.
- **Leaf White** (#ffffff): hojas, campos y botones secundarios.
- **Ledger Ink** (#13202b): texto principal. **Soft Ink** (#46555f): texto secundario. **Faint Ink** (#5d6c76): ayudas, identificadores y placeholders (≥4.9:1 sobre papel).
- **Ledger Rule** (#d5dfdb): bordes de hojas y separadores. **Strong Rule** (#b6c5bf): bordes de campos y perforación.

### Named Rules
**The One Ink Rule.** El azul aparece solo en acciones, navegación activa, foco y selección, y en las gráficas como la única serie destacada (énfasis). Etiquetas de rol, avatares y cifras son neutros; si el azul decora, deja de significar.
**The Stamp Only Rule.** El rojo significa error o reverso, nunca énfasis.

## Typography

**Display Font:** ninguna; el sistema no usa fuente de exhibición.
**Body Font:** Geist (con ui-sans-serif, system-ui)
**Label/Mono Font:** Geist Mono, solo para folios (N.º) e identificadores.

**Character:** una sola sans de oficio, tensa y neutral. Los números usan `font-variant-numeric: tabular-nums` en todo el documento para que las cifras alineen en columnas.

### Hierarchy
- **Headline** (600, 24px, 1.25, -0.025em): título de página.
- **Title** (600, 20px, 1.3, -0.025em): título de hoja de autenticación.
- **Body** (400, 15px, 1.5): texto general; texto de ayuda en 14px.
- **Label** (500, 14px): etiquetas de campo, botones y navegación.
- **Caption** (400, 12px): ayudas bajo campos, correos secundarios y badges (500).
- **Folio** (Geist Mono 400, 12px): "N.º 1E892B".

### Named Rules
**The Tabular Rule.** Toda cifra es tabular; los importes futuros se alinean a la derecha y nunca se mezclan con texto en la misma celda.

## Layout

Operación en escritorio con riel lateral fijo de 248px (papel profundo) y columna de contenido de máximo 960px con relleno de 32px (16px en móvil). En menos de 1024px el riel pasa a un menú lateral de 280px abierto desde una barra superior de 56px. Las pantallas de autenticación son una hoja centrada de 400px de ancho máximo. Ritmo: separaciones de 8, 16, 24 y 32px; campos separados 20px dentro de una hoja; filas de lista con 14px de relleno vertical. Las filas de tabla se apilan en móvil en lugar de desbordarse.

## Elevation & Depth

Sin sombras. La profundidad es tonal y por contorno: hojas blancas con borde de 1px sobre papel; el riel lateral en papel profundo. La única "elevación" es la perforación: una línea punteada de 2px con dos muescas semicirculares en los bordes que separa el encabezado de una hoja de su cuerpo.

### Named Rules
**The Flat Leaf Rule.** Ninguna hoja proyecta sombra. Si algo debe separarse, se usa un renglón o una perforación.

## Shapes

Esquinas moderadas y consistentes: 8px en hojas, 6px en controles, 4px en badges, círculo completo en avatares. Bordes de 1px en todo; la firma geométrica es la muesca semicircular de la perforación.

## Components

### Buttons
- **Shape:** 6px, 40px de alto (32px en tamaño pequeño), texto 14px/500.
- **Primary:** fondo Carbon-Copy Blue, texto blanco; hover Pressed Carbon Blue.
- **Secondary:** hoja blanca con borde Strong Rule; hover Cool Paper.
- **Danger:** blanco con texto Stamp Red; hover Stamp Wash.
- **Loading / Disabled:** spinner de 15px a la izquierda del texto; 50% de opacidad y cursor no permitido.

### Inputs / Fields
- **Style:** 40px de alto, borde Strong Rule de 1px, fondo blanco, radio 6px, relleno de 12px.
- **Focus:** borde y anillo de 2px en Carbon-Copy Blue. **Error:** borde y mensaje en Stamp Red. **Disabled:** fondo Cool Paper.
- **Select:** mismo control con chevron dibujado a la derecha.

### Cards / Containers (Leaf)
- **Corner Style:** 8px. **Background:** blanco. **Border:** 1px Ledger Rule. **Shadow:** ninguna. **Padding:** 20-28px.

### Badges
- 4px de radio, 12px/500. Activo = Ledger; error = Stamp; pendiente = Amber; neutro = Deep Paper con Soft Ink (roles).

### Navigation
- Elemento de 36px, icono de 16px y texto 14px/500. Activo: hoja blanca con anillo de renglón y texto azul. Inactivo: Soft Ink, hover blanco al 60%. Móvil: menú lateral con fondo oscurecido.

### Charts
- Forma por trabajo: columnas agrupadas para tendencia mensual (lo recibido en azul de acento, lo cargado como contexto en gris `#84958e`; contraste ≥ 3:1 y ΔE 29.8 validados), barras horizontales para el adeudo vencido por antigüedad (rampa ordinal de un solo tono rojo `#e8a39d → #7a1712`, validada) y para incidencias por prioridad. La ocupación es una barra apilada con 2px de separación.
- Marcas delgadas (≤ 24px; las columnas de 20px), extremo de datos redondeado a 4px y base cuadrada; rejilla de 1px sólida y apenas visible; una sola escala (sin doble eje).
- Leyenda siempre con 2 o más series; solo se etiqueta el último valor recibido; el resto va en el tooltip (también con foco de teclado) y en la **vista de tabla** que toda gráfica ofrece. El texto de etiquetas y valores usa tokens de texto, nunca el color de la serie.
- El dibujo se mide a su contenedor para que el texto conserve su tamaño en móvil.

### Perforated Leaf (signature)
- Hoja con encabezado y cuerpo separados por una perforación. Se usa en toda pantalla de autenticación y en formularios importantes de la app.

## Do's and Don'ts

### Do:
- **Do** usar Carbon-Copy Blue solo para acciones, navegación activa y foco.
- **Do** separar con renglones de 1px (#d5dfdb) y perforaciones, no con sombras.
- **Do** usar cifras tabulares y Geist Mono solo para folios e identificadores.
- **Do** dar a cada pantalla estados de carga (skeleton), vacío, error y éxito.
- **Do** mantener contraste de texto ≥4.5:1 (Faint Ink es el mínimo permitido para texto).

### Don't:
- **Don't** agregar tarjetas de icono + título + texto como estructura de página.
- **Don't** usar rojo para enfatizar; es solo error o reverso.
- **Don't** introducir tema oscuro o degradados decorativos.
- **Don't** usar azul en etiquetas de rol, avatares o datos.
