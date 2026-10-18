namespace Habita.Api.Security;

/// <summary>
/// Refuses to start in production with a configuration that would silently weaken security
/// (default passwords, plain HTTP origins, exposed invitation tokens, ...). Better to fail loudly at boot.
/// </summary>
public static class ProductionGuards
{
    public static bool IsProduction(IHostEnvironment env) => !(env.IsDevelopment() || env.IsEnvironment("Testing"));

    public static IReadOnlyList<string> Problems(IConfiguration c, IHostEnvironment env)
    {
        var problems = new List<string>();
        if (!IsProduction(env)) return problems;

        var cs = c.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs)) problems.Add("ConnectionStrings__Default no está definida.");
        else if (new[] { "change-me", "password=password", "password=postgres", "password=habita;" }.Any(w => cs.Contains(w, StringComparison.OrdinalIgnoreCase)))
            problems.Add("ConnectionStrings__Default usa una contraseña de ejemplo. Define una contraseña propia.");

        if (!Uri.TryCreate(c["Web:BaseUrl"], UriKind.Absolute, out var web) || web.Scheme != Uri.UriSchemeHttps)
            problems.Add("Web__BaseUrl debe ser una URL https:// (se usa en los enlaces de correo).");

        var origins = OriginCheckMiddleware.ReadAllowed(c);
        if (origins.Count == 0) problems.Add("Security__AllowedOrigins debe listar al menos el origen https:// de la web.");
        else if (origins.Any(o => !o.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) problems.Add("Security__AllowedOrigins solo admite orígenes https:// en producción.");

        var hosts = c["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Trim() == "*") problems.Add("AllowedHosts no puede ser '*' en producción: indica tu dominio.");
        if (c.GetValue<bool>("Auth:ExposeInvitationTokens")) problems.Add("Auth__ExposeInvitationTokens debe ser false en producción.");
        if (string.IsNullOrWhiteSpace(c["Smtp:Host"])) problems.Add("Smtp__Host es obligatorio: invitaciones y recuperación de contraseña dependen del correo.");
        if (string.IsNullOrWhiteSpace(c["DataProtection:KeysPath"])) problems.Add("DataProtection__KeysPath es obligatorio para que las sesiones sobrevivan a reinicios.");
        if (string.IsNullOrWhiteSpace(c["Pdf:GotenbergUrl"])) problems.Add("Pdf__GotenbergUrl es obligatorio: los contratos y recibos en PDF dependen del servicio de PDF.");
        if (!RateLimiting.Enabled(c)) problems.Add("RateLimiting__Enabled no puede ser false en producción.");
        if (c["Security:SecureCookies"] is { } sc && bool.TryParse(sc, out var secure) && !secure) problems.Add("Security__SecureCookies no puede ser false en producción.");
        // Push is optional, but a half-configured push is a mistake: either both VAPID keys or neither.
        var pub = c["Push:VapidPublicKey"]; var priv = c["Push:VapidPrivateKey"];
        if (string.IsNullOrWhiteSpace(pub) != string.IsNullOrWhiteSpace(priv)) problems.Add("Push__VapidPublicKey y Push__VapidPrivateKey van juntas: define las dos o ninguna.");
        var subject = c["Push:Subject"];
        if (!string.IsNullOrWhiteSpace(pub) && (subject is null || !(subject.StartsWith("mailto:") || subject.StartsWith("https://"))))
            problems.Add("Push__Subject debe ser un contacto 'mailto:' o una URL https:// cuando hay llaves VAPID.");
        return problems;
    }

    public static void Enforce(IConfiguration c, IHostEnvironment env)
    {
        var problems = Problems(c, env);
        if (problems.Count > 0)
            throw new InvalidOperationException("Configuración insegura para producción:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
    }
}
