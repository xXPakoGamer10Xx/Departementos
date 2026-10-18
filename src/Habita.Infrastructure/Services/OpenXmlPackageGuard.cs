using System.IO.Compression;
using System.Xml;

namespace Habita.Infrastructure.Services;

/// <summary>
/// The checks every uploaded Office file passes BEFORE it is opened: it must be a real zip with the expected main part, small once expanded, with no macros,
/// no embedded objects and no links to files outside it (the PDF service or a spreadsheet reader would otherwise try to fetch them).
/// </summary>
public static class OpenXmlPackageGuard
{
    private const long MaxUncompressedBytes = 60L * 1024 * 1024;
    private const int MaxEntries = 1000;

    /// <param name="mainEntry">The part every valid file of this kind has: "word/document.xml" or "xl/workbook.xml".</param>
    /// <param name="kind">How the file is called in messages: "documento de Word (.docx)" or "archivo de Excel (.xlsx)".</param>
    /// <returns>Null when the package is acceptable; otherwise what is wrong, in words for the person who uploaded it.</returns>
    public static string? Check(byte[] bytes, string mainEntry, string kind)
    {
        var notValid = $"El archivo no es un {kind} válido.";
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K') return notValid;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return "El archivo tiene demasiadas partes internas.";
            long total = 0; var hasMain = false;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (total > MaxUncompressedBytes) return "El archivo es demasiado grande una vez descomprimido.";
                var name = e.FullName.Replace('\\', '/');
                if (name.Contains("..") || name.StartsWith('/')) return "El archivo tiene rutas internas no válidas.";
                if (name.Equals(mainEntry, StringComparison.OrdinalIgnoreCase)) hasMain = true;
                if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) || name.EndsWith("vbaData.xml", StringComparison.OrdinalIgnoreCase))
                    return "El archivo contiene macros. Guárdalo sin macros.";
                if (name.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase) || name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase))
                    return "El archivo contiene objetos incrustados. Quítalos y vuelve a subirlo.";
                if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) && ReadEntry(e).Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                    return "El archivo admite macros. Guárdalo sin macros.";
                if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) && ExternalRelationship(ReadEntry(e)) is { } outside)
                    return $"El archivo enlaza un archivo externo ({outside}). Debe ser autocontenido.";
            }
            return hasMain ? null : notValid;
        }
        // A DOCTYPE inside a part is refused by the XML reader (it throws), which is the point: it is never expanded. Same answer as any broken file.
        catch (Exception e) when (e is InvalidDataException or XmlException) { return notValid; }
    }

    private static string ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open(); using var r = new StreamReader(s);
        var buffer = new char[(int)Math.Min(e.Length, 5_000_000)];
        return new string(buffer, 0, r.ReadBlock(buffer, 0, buffer.Length));
    }

    /// <summary>The kind of relationship, if the part points outside the file. Ordinary web links are fine; nothing else may leave the file.</summary>
    private static string? ExternalRelationship(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 5_000_000 });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            if (!string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
            var type = reader.GetAttribute("Type") ?? "";
            if (!type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase)) return type[(type.LastIndexOf('/') + 1)..];
        }
        return null;
    }
}
