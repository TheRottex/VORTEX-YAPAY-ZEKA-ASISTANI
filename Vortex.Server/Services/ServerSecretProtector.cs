using System.Security.Cryptography;
using System.Text;

namespace Vortex.Server.Public.Services;

public static class ServerSecretProtector
{
    private const string Prefix = "vortex-secret:v1:";

    public static string Protect(string secret, string keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(secret)) return string.Empty;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(keyMaterial), 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public static string? Unprotect(string? value, string keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var payload = Convert.FromBase64String(value[Prefix.Length..]);
        if (payload.Length < 28) throw new CryptographicException("Invalid protected secret payload.");
        using var aes = new AesGcm(DeriveKey(keyMaterial), 16);
        var plaintext = new byte[payload.Length - 28];
        aes.Decrypt(payload[..12], payload[28..], payload[12..28], plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(keyMaterial)) throw new InvalidOperationException("Server secret key material is not configured.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }
}
