using System.Net;

namespace HotspotApp.Services.Infrastructure;

internal static class ManualDnsSettings
{
    // Exigir IPv4 decimal completo: IPAddress.TryParse também aceita formas
    // abreviadas e números inteiros, pouco claros em um campo de configuração.
    public static IPAddress[] Parse(string preferred, string alternate)
    {
        static IPAddress Address(string value)
        {
            var parts = value.Trim().Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9') || !byte.TryParse(p, out _)))
                throw new ArgumentException("Invalid DNS IPv4 address");
            var bytes = parts.Select(byte.Parse).ToArray();
            if (bytes[0] is 0 or 127 or >= 224 || (bytes[0] == 169 && bytes[1] == 254))
                throw new ArgumentException("DNS address must be reachable through the upstream interface");
            return new IPAddress(bytes);
        }
        var first = Address(preferred);
        return string.IsNullOrWhiteSpace(alternate) ? [first] : new[] { first, Address(alternate) }.Distinct().ToArray();
    }
}
