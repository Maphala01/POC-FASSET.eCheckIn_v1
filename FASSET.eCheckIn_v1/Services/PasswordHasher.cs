using System;
using System.Security.Cryptography;
using System.Text;

namespace FASSET.eCheckIn_v1.Services
{
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 100000;

        public static string HashPassword(string password)
        {
            string salt = Guid.NewGuid().ToString();
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(salt + password));
                string hex = BitConverter.ToString(bytes).Replace("-", "");
                return salt + ":" + hex;
            }
        }

        public static bool VerifyPassword(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash)) return false;

            var parts = storedHash.Split(':');
            if (parts.Length != 2) return false;

            string salt = parts[0];
            string expectedHex = parts[1];

            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(salt + password));
                string actualHex = BitConverter.ToString(bytes).Replace("-", "");
                return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Constant-time comparison so verification timing can't leak
        // information about how much of the hash matched.
        private static bool SlowEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}