using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.HAnimeTV.Hanime
{
    /// <summary>
    /// The request signatures and message envelope hanime.tv's site and app use.
    /// </summary>
    /// <remarks>
    /// None of this is secret: the keys are fixed and ship with hanime.tv's player. The
    /// envelope ("insecure message" in hanime.tv's code) is AES-256-GCM with the SHA-256 of a
    /// fixed label as key and a second fixed label as associated data.
    /// </remarks>
    internal static class HanimeCrypto
    {
        private static readonly byte[] MessageKey = SHA256.HashData(Encoding.ASCII.GetBytes("htv-insecure-handshake-v1"));
        private static readonly byte[] MessageAad = Encoding.ASCII.GetBytes("htv-insecure-v1");

        /// <summary>
        /// The web signature (X-Signature-Version: web2) of the handshake.
        /// </summary>
        public static string WebSignature(long time) =>
            Sha256Hex(string.Create(CultureInfo.InvariantCulture, $"{time},Xkdi29,https://hanime.tv,mn2,{time}"));

        /// <summary>
        /// The app signature (X-Signature-Version: app2) of the catalog and the login.
        /// </summary>
        public static string AppSignature(long time) =>
            Sha256Hex(string.Create(CultureInfo.InvariantCulture, $"9944822{time}8{time}113"));

        public static string Seal(JsonNode payload)
        {
            var iv = RandomNumberGenerator.GetBytes(12);
            var plaintext = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(MessageKey, tag.Length))
            {
                aes.Encrypt(iv, plaintext, ciphertext, tag, MessageAad);
            }

            var envelope = new JsonObject
            {
                ["v"] = 1,
                ["alg"] = "AES-256-GCM",
                ["iv"] = Base64Url(iv),
                ["tag"] = Base64Url(tag),
                ["data"] = Base64Url(ciphertext),
            };
            return Base64Url(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        }

        /// <summary>
        /// Opens an envelope.
        /// </summary>
        /// <exception cref="FormatException">The token is not a valid envelope.</exception>
        /// <exception cref="CryptographicException">The token was not sealed with hanime.tv's key.</exception>
        public static JsonNode Open(string token)
        {
            JsonNode? envelope;
            try
            {
                envelope = JsonNode.Parse(FromBase64Url(token));
            }
            catch (JsonException ex)
            {
                throw new FormatException("The token is not a JSON envelope", ex);
            }

            var iv = FromBase64Url(envelope?["iv"]?.GetValue<string>() ?? throw new FormatException("The token has no iv"));
            var tag = FromBase64Url(envelope["tag"]?.GetValue<string>() ?? throw new FormatException("The token has no tag"));
            var ciphertext = FromBase64Url(envelope["data"]?.GetValue<string>() ?? throw new FormatException("The token has no data"));
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(MessageKey, tag.Length))
            {
                aes.Decrypt(iv, ciphertext, tag, plaintext, MessageAad);
            }

            return JsonNode.Parse(plaintext) ?? throw new FormatException("The token is empty");
        }

        private static string Sha256Hex(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        internal static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static byte[] FromBase64Url(string value)
        {
            var base64 = value.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new FormatException("The token is not base64url", ex);
            }
        }
    }
}
