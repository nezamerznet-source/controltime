using System.Security.Cryptography;

namespace FamilyTime.Core;

/// <summary>Versioned, salted password verifier. Never persist the password itself.</summary>
public static class ParentPassword
{
    private const int Iterations = 600_000;
    public static string Create(string password)
    {
        if (password.Length is < 8 or > 128)
            throw new ArgumentException("Пароль должен содержать от 8 до 128 символов.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }
    public static bool Verify(string password, string encoded)
    {
        if (password.Length > 128 || encoded.Length is 0 or > 256) return false;
        try
        {
            string[] parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out int rounds) || rounds != Iterations) return false;
            byte[] salt = Convert.FromBase64String(parts[2]), expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
