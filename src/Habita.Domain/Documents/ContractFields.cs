namespace Habita.Domain.Documents;

/// <summary>
/// The fields a contract template may use, written {{like_this}} in the Word file. The names are the ones the landlord's previous system used,
/// so an existing template keeps working; a few were added (propiedad_*, email_arrendatario). A name that is not here is rejected when the
/// template is uploaded, because a typo would otherwise print as an empty space inside a legal text.
/// </summary>
public static class ContractFields
{
    public sealed record Field(string Name, string Description, string Example);

    public static readonly IReadOnlyList<Field> All =
    [
        new("fecha_actual", "Fecha en que se genera el documento.", "17 de octubre de 2026"),
        new("nombre_completo", "Nombre del inquilino: nombre y luego apellidos, como está en su ficha.", "Ana Ejemplo"),
        new("tel_arrendatario", "Teléfono del inquilino.", "55 1234 5678"),
        new("email_arrendatario", "Correo del inquilino.", "ana@example.com"),
        new("depto_numero", "Clave de la unidad.", "101"),
        new("propiedad_nombre", "Nombre de la propiedad.", "Torre Norte"),
        new("propiedad_direccion", "Dirección de la propiedad.", "Av. Demo 123, Ciudad de México"),
        new("renta", "Renta mensual con formato de moneda.", "$8,500.00"),
        new("renta_numero", "Renta mensual solo en número.", "8500.00"),
        new("renta_letra", "Renta mensual con letra.", "OCHO MIL QUINIENTOS PESOS 00/100 M.N."),
        new("dia_pago", "Día del mes en que vence la renta.", "5"),
        new("fecha_pago", "Cuándo se paga, en texto.", "5 de cada mes"),
        new("fecha_inicio", "Inicio del contrato.", "1 de julio de 2026"),
        new("fecha_termino", "Fin del contrato, o «tiempo indefinido» si no tiene.", "30 de junio de 2027"),
        new("deposito", "Depósito o compensación con formato de moneda.", "$8,500.00"),
        new("deposito_numero", "Depósito solo en número.", "8500.00"),
        new("deposito_letra", "Depósito con letra.", "OCHO MIL QUINIENTOS PESOS 00/100 M.N."),
        new("deposito_tipo", "Cómo se entrega el depósito (aún no disponible).", ""),
        new("deposito_fechas", "Fechas de los abonos del depósito (aún no disponible).", ""),
        new("metodo_pago", "Forma de pago que se anota en el contrato.", "efectivo o transferencia"),
        new("arrendador_nombre", "Nombre del arrendador (Ajustes).", "Paola Propietaria"),
        new("arrendador_direccion", "Domicilio del arrendador (Ajustes).", "Calle 1, Ciudad de México"),
        new("fiador_nombre", "Nombre del fiador (aún no disponible).", ""),
        new("fiador_telefono", "Teléfono del fiador (aún no disponible).", ""),
        new("inventario", "Inventario de la unidad (aún no disponible).", ""),
        new("observaciones", "Notas del contrato.", "Renta mensual, sin prorrateo."),
        new("arrendatario_linea", "Línea de firma del inquilino.", "ARRENDATARIO: Ana Ejemplo — Tel: 55 1234 5678"),
        new("arrendador_linea", "Línea de firma del arrendador.", "ARRENDADOR: Paola Propietaria"),
        new("fiador_linea", "Línea de firma del fiador; vacía si no hay fiador (y su párrafo se quita).", "FIADOR: Pedro Fiador — Tel: 55 8765 4321"),
    ];

    private static readonly HashSet<string> Names = All.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string name) => Names.Contains(name);

    /// <summary>Fields a contract is hard to read without. A template that uses none of them is probably the wrong file.</summary>
    public static readonly string[] Recommended = ["nombre_completo", "renta", "fecha_inicio"];
}
