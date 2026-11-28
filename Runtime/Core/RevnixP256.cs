using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Revnix
{
    internal sealed class RevnixP256Key
    {
        private static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        private static readonly BigInteger A = P - 3;
        private static readonly BigInteger B = Hex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
        private static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
        private static readonly Point G = new Point(
            Hex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"),
            Hex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"),
            BigInteger.One);

        private static readonly byte[] SpkiPrefix =
        {
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
        };
        private const int CoordinateBytes = 32;

        private readonly Point _q;

        private RevnixP256Key(Point q)
        {
            _q = q;
        }

        internal static RevnixP256Key FromSpki(string base64)
        {
            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(base64 ?? "");
            }
            catch (FormatException)
            {
                return null;
            }
            if (spki.Length != SpkiPrefix.Length + 1 + 2 * CoordinateBytes) return null;
            for (var i = 0; i < SpkiPrefix.Length; i++)
            {
                if (spki[i] != SpkiPrefix[i]) return null;
            }
            if (spki[SpkiPrefix.Length] != 0x04) return null;
            var x = Unsigned(spki, SpkiPrefix.Length + 1, CoordinateBytes);
            var y = Unsigned(spki, SpkiPrefix.Length + 1 + CoordinateBytes, CoordinateBytes);
            if (x >= P || y >= P) return null;
            if (Mod(y * y) != Mod(x * x * x + A * x + B)) return null;
            return new RevnixP256Key(new Point(x, y, BigInteger.One));
        }

        internal bool Verify(byte[] message, byte[] derSignature)
        {
            if (!TryParseDer(derSignature, out var r, out var s)) return false;
            if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N) return false;
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(message);
            var e = Unsigned(digest, 0, digest.Length);
            var w = BigInteger.ModPow(s, N - 2, N);
            var u1 = e * w % N;
            var u2 = r * w % N;
            var sum = ShamirMultiply(u1, u2, _q);
            if (sum.IsInfinity) return false;
            var zInverse = BigInteger.ModPow(sum.Z, P - 2, P);
            var x = Mod(sum.X * zInverse * zInverse);
            return x % N == r;
        }

        private static Point ShamirMultiply(BigInteger u1, BigInteger u2, Point q)
        {
            var gq = Add(G, q);
            var result = Point.Infinity;
            for (var bit = 255; bit >= 0; bit--)
            {
                result = Double(result);
                var useG = !((u1 >> bit) & BigInteger.One).IsZero;
                var useQ = !((u2 >> bit) & BigInteger.One).IsZero;
                if (useG && useQ) result = Add(result, gq);
                else if (useG) result = Add(result, G);
                else if (useQ) result = Add(result, q);
            }
            return result;
        }

        private static Point Double(Point p)
        {
            if (p.IsInfinity || p.Y.IsZero) return Point.Infinity;
            var yy = Mod(p.Y * p.Y);
            var zz = Mod(p.Z * p.Z);
            var s = Mod(4 * p.X * yy);
            var m = Mod(3 * p.X * p.X + A * zz * zz);
            var x = Mod(m * m - 2 * s);
            var y = Mod(m * (s - x) - 8 * yy * yy);
            var z = Mod(2 * p.Y * p.Z);
            return new Point(x, y, z);
        }

        private static Point Add(Point p, Point q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;
            var pz2 = Mod(p.Z * p.Z);
            var qz2 = Mod(q.Z * q.Z);
            var u1 = Mod(p.X * qz2);
            var u2 = Mod(q.X * pz2);
            var s1 = Mod(p.Y * qz2 * q.Z);
            var s2 = Mod(q.Y * pz2 * p.Z);
            var h = Mod(u2 - u1);
            var r = Mod(s2 - s1);
            if (h.IsZero) return r.IsZero ? Double(p) : Point.Infinity;
            var h2 = Mod(h * h);
            var h3 = Mod(h2 * h);
            var u1h2 = Mod(u1 * h2);
            var x = Mod(r * r - h3 - 2 * u1h2);
            var y = Mod(r * (u1h2 - x) - s1 * h3);
            var z = Mod(h * p.Z * q.Z);
            return new Point(x, y, z);
        }

        private static bool TryParseDer(byte[] der, out BigInteger r, out BigInteger s)
        {
            r = s = BigInteger.Zero;
            if (der == null || der.Length < 2 || der[0] != 0x30 || der[1] >= 0x80 || der[1] != der.Length - 2) return false;
            var pos = 2;
            return TryReadInteger(der, ref pos, out r) && TryReadInteger(der, ref pos, out s) && pos == der.Length;
        }

        private static bool TryReadInteger(byte[] der, ref int pos, out BigInteger value)
        {
            value = BigInteger.Zero;
            if (pos + 2 > der.Length || der[pos] != 0x02) return false;
            int length = der[pos + 1];
            var start = pos + 2;
            if (length == 0 || length > CoordinateBytes + 1 || start + length > der.Length) return false;
            if ((der[start] & 0x80) != 0) return false;
            if (length > 1 && der[start] == 0 && (der[start + 1] & 0x80) == 0) return false;
            value = Unsigned(der, start, length);
            pos = start + length;
            return true;
        }

        private static BigInteger Unsigned(byte[] bigEndian, int offset, int length)
        {
            var littleEndian = new byte[length + 1];
            for (var i = 0; i < length; i++) littleEndian[i] = bigEndian[offset + length - 1 - i];
            return new BigInteger(littleEndian);
        }

        private static BigInteger Mod(BigInteger value)
        {
            var result = value % P;
            return result.Sign < 0 ? result + P : result;
        }

        private static BigInteger Hex(string hex) => BigInteger.Parse("00" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private readonly struct Point
        {
            public static readonly Point Infinity = new Point(BigInteger.One, BigInteger.One, BigInteger.Zero);

            public readonly BigInteger X;
            public readonly BigInteger Y;
            public readonly BigInteger Z;

            public Point(BigInteger x, BigInteger y, BigInteger z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool IsInfinity => Z.IsZero;
        }
    }
}
