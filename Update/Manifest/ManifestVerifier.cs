using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace KeyInsight.Update.Manifest
{
    /// <summary>
    /// 清单验签（路线图 PR-4 / §7.2）：RSA-3072 + SHA-256 PKCS#1 v1.5（Windows CNG 可用，
    /// 零 NuGet）。签名覆盖 manifest 原始 UTF-8 字节；客户端内置公钥集合与 KeyId。
    /// 签名通过后才信任清单中的版本、资产哈希与 URL。
    /// </summary>
    public static class ManifestVerifier
    {
        /// <summary>
        /// 内置公钥集合（KeyId → 公钥 XML，CspParameters XML 含 Modulus/Exponent）。
        /// 私钥保存在发布环境（GitHub Secret / 本地受保护文件），不入库。
        /// 密钥轮换：在此表中新旧 KeyId 并存过渡。
        /// </summary>
        private static readonly Dictionary<string, string> PublicKeys = new Dictionary<string, string>
        {
            { "release-key-2026", DevPublicKeyXml }
        };

        /// <summary>清单有效期：发布时间早于该天数拒绝（防重放旧清单）。</summary>
        public const int MaxManifestAgeDays = 60;

        /// <summary>开发期公钥（由 tools/generate-update-keys.ps1 生成；生产替换为发布密钥）。</summary>
        public const string DevPublicKeyXml =
            "<RSAKeyValue><Modulus>08WJHxoskBx8gzOX3lmdnq+Y3/0cOk+L/4oQqNiRS86qMzma64gpMorHAZ1Ys6uYEE6mDgNkLr0ZtfpxXHS+BgWdADjGDDleDaNSeMhElGTxAuYptIVp4H8EKx3wBa2LH/mZ1/tfS5Xo6GFgWWrbyAn9yYBP88Fzl/baDks/ije7tJimAqpn+suTmT0Db3ytSLKDs5BDhUBTjtKSYrIqEiNNap2NxB2NK5J+JVr9t9oLoZmqSA420UzDWfPk5XZYxcWjk3gm0V0KlzQRu/CNoT5d3pRAbaXcJue2qky4WfBALr6O2Xaa6flGnXZALesUv+fYxDEIsBqXpJBOmTGYUT1i9sxPfR1qAcXk8Szxz2GiOAIi/329JhncVzIcJfNTK5f7NC3C6AHevMaTBskzhoB3rge+8gXAOUjL6TdXDo9+3mJQrvq878IU99kL+Ll+XHiTKakT4mSIaUHaBW3qQNF87W50+VWSq1Hb+mdIGqtecmhxS6vEWqLiFwcXS2xx</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public sealed class VerifyResult
        {
            public bool Ok;
            public string Error;
        }

        public static VerifyResult Verify(byte[] manifestBytes, byte[] signature, string claimedKeyId)
        {
            VerifyResult r = new VerifyResult();
            if (manifestBytes == null || manifestBytes.Length == 0 || signature == null || signature.Length == 0)
            {
                r.Error = "manifest or signature empty";
                return r;
            }
            if (claimedKeyId == null || !PublicKeys.ContainsKey(claimedKeyId))
            {
                r.Error = "unknown keyId: " + (claimedKeyId ?? "null");
                return r;
            }

            try
            {
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(PublicKeys[claimedKeyId]);
                    r.Ok = rsa.VerifyData(manifestBytes, new SHA256CryptoServiceProvider(), signature);
                    if (!r.Ok) r.Error = "signature mismatch";
                }
            }
            catch (Exception ex)
            {
                r.Error = "verify error: " + ex.Message;
            }
            return r;
        }

        /// <summary>发布端签名（tools/release-manifest.ps1 使用同一算法）。</summary>
        public static byte[] Sign(byte[] manifestBytes, string privateKeyXml)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072))
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(privateKeyXml);
                return rsa.SignData(manifestBytes, new SHA256CryptoServiceProvider());
            }
        }

        /// <summary>Base64 辅助（manifest.sig 文件格式）。</summary>
        public static byte[] FromBase64(string text)
        {
            return Convert.FromBase64String((text ?? "").Trim());
        }

        public static string ToBase64(byte[] data)
        {
            return Convert.ToBase64String(data);
        }
    }
}
