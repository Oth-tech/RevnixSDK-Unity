using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    internal sealed class TestSigningKey
    {
        public const string ApiKey = "rvx_pk_test";

#if NET5_0_OR_GREATER
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string Spki => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        public byte[] SignDer(byte[] message)
            => _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        public byte[] SignRaw(byte[] message)
            => _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
#else
        public string Spki => "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsVQ+iVnl3H7IWe6uma8VASdqlJ09SrtOIKpxB5vF1xF7f3nD+SBzXlse1+skTi/jv/CLa4fUz9qGfZ4T8M+Nhw==";

        public byte[] SignDer(byte[] message) => Unsupported<byte[]>();

        public byte[] SignRaw(byte[] message) => Unsupported<byte[]>();

        private static T Unsupported<T>()
        {
            Assert.Ignore("signing test vectors need the .NET 5+ ECDsa API");
            return default;
        }
#endif

        public string Signature(string body, string cid, long signedAt, string apiKey = ApiKey)
            => Convert.ToBase64String(SignDer(Encoding.UTF8.GetBytes(
                signedAt.ToString(CultureInfo.InvariantCulture) + "." + Sha256Hex(apiKey.Trim()) + "." +
                Sha256Hex(cid) + "." + body)));

        public RevnixHttpResponse Response(string body, string cid, long signedAt, string apiKey = ApiKey)
            => new RevnixHttpResponse(200, body, null,
                signedAt.ToString(CultureInfo.InvariantCulture), Signature(body, cid, signedAt, apiKey));

        public string CacheEntry(string body, string cid, long signedAt, long fetchedAt, string apiKey = ApiKey)
            => RevnixJson.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["body"] = body,
                ["signedAt"] = signedAt.ToString(CultureInfo.InvariantCulture),
                ["signature"] = Signature(body, cid, signedAt, apiKey),
                ["fetchedAt"] = fetchedAt,
            });

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
