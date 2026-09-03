using System;
using System.Security.Cryptography;
using System.Text;

namespace AMCCA.Core.Database;

public static class UlidGenerator
{
    private const string CrockfordBase32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid()
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var randomBytes = new byte[10];
        RandomNumberGenerator.Fill(randomBytes);

        var sb = new StringBuilder(26);

        // 48-bit timestamp (10 Crockford characters)
        for (int i = 9; i >= 0; i--)
        {
            var charIndex = (int)((timestampMs >> (i * 5)) & 0x1F);
            sb.Append(CrockfordBase32[charIndex]);
        }

        // 80-bit randomness (16 Crockford characters)
        ulong highRandom = BitConverter.ToUInt64(randomBytes, 0);
        ushort lowRandom = BitConverter.ToUInt16(randomBytes, 8);

        for (int i = 15; i >= 0; i--)
        {
            int charIndex;
            if (i >= 3)
            {
                charIndex = (int)((highRandom >> ((i - 3) * 5)) & 0x1F);
            }
            else
            {
                charIndex = (int)((lowRandom >> (i * 5)) & 0x1F);
            }
            sb.Append(CrockfordBase32[charIndex]);
        }

        return sb.ToString();
    }
}
