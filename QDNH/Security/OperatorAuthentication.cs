using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QDNH.Security;

public enum OperatorRole { Observer, Operator, Administrator }
public sealed record OperatorPrincipal(string UserId, string Callsign, OperatorRole Role);

/// <summary>Opt-in: secreto individual mediante el mismo reto ASCII(secret)+salt de los clientes existentes.</summary>
public sealed class OperatorAuthentication
{
    private readonly (OperatorPrincipal Principal, string Secret)[] credentials;
    private OperatorAuthentication((OperatorPrincipal Principal, string Secret)[] credentials) => this.credentials = credentials;
    public static OperatorAuthentication? Load(string path) => string.IsNullOrWhiteSpace(path) ? null : Parse(File.ReadAllText(path), Environment.GetEnvironmentVariable);

    public static OperatorAuthentication Parse(string json, Func<string, string?> environment)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireProperties(root, "SchemaVersion", "Operators");
        if (root.GetProperty("SchemaVersion").GetInt32() != 1) throw new InvalidDataException("Versión de cuentas no admitida.");
        var users = root.GetProperty("Operators");
        if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() is < 1 or > 64) throw new InvalidDataException("Se requieren entre 1 y 64 cuentas.");
        var result = new List<(OperatorPrincipal, string)>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in users.EnumerateArray())
        {
            RequireProperties(entry, "UserId", "Callsign", "Role", "SecretEnvironmentVariable");
            string id = entry.GetProperty("UserId").GetString() ?? "";
            string callsign = entry.GetProperty("Callsign").GetString() ?? "";
            string role = entry.GetProperty("Role").GetString() ?? "";
            string variable = entry.GetProperty("SecretEnvironmentVariable").GetString() ?? "";
            if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,48}$") || !ids.Add(id)) throw new InvalidDataException("Identificador de cuenta inválido o duplicado.");
            if (!Regex.IsMatch(callsign, "^[a-zA-Z0-9/ -]{1,24}$") || string.IsNullOrWhiteSpace(callsign) || callsign != callsign.Trim() || !callsigns.Add(callsign)) throw new InvalidDataException("Indicativo de cuenta inválido.");
            if (!Enum.TryParse<OperatorRole>(role, false, out var parsedRole) || !Enum.IsDefined(typeof(OperatorRole), parsedRole) || role != parsedRole.ToString()) throw new InvalidDataException("Rol desconocido.");
            if (!Regex.IsMatch(variable, "^[A-Z][A-Z0-9_]{2,127}$")) throw new InvalidDataException("Nombre de variable de secreto inválido.");
            string secret = environment(variable) ?? "";
            if (secret.Length is < 16 or > 128 || secret.Any(c => c < 33 || c > 126) || !secrets.Add(secret)) throw new InvalidDataException("Secreto ausente, débil, no ASCII o duplicado. Revise las variables de las cuentas.");
            result.Add((new(id, callsign.ToUpperInvariant(), parsedRole), secret));
        }
        return new OperatorAuthentication(result.ToArray());
    }
    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Se esperaba un objeto de cuentas.");
        var names = new HashSet<string>();
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name) || !names.Add(property.Name)) throw new InvalidDataException("Propiedad desconocida o duplicada en cuentas.");
        if (names.Count != expected.Length) throw new InvalidDataException("Faltan propiedades obligatorias de cuentas.");
    }
    public OperatorPrincipal? Authenticate(byte[] challenge, byte[] salt)
    {
        if (challenge.Length != 32 || salt.Length != 32) return null;
        OperatorPrincipal? matched = null;
        // Recorrer todas las cuentas: no revelar la posición del secreto coincidente.
        foreach (var credential in credentials)
        {
            byte[] secret = Encoding.ASCII.GetBytes(credential.Secret);
            byte[] input = new byte[secret.Length + salt.Length];
            secret.CopyTo(input, 0); salt.CopyTo(input, secret.Length);
            byte[] hash = SHA256.HashData(input);
            if (CryptographicOperations.FixedTimeEquals(challenge, hash)) matched = credential.Principal;
            CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(input);
        }
        return matched;
    }
}
