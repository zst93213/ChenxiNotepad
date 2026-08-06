using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 加密服务: 负责主密码派生、AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC) 加解密,
/// 以及密码库 (VaultData) 的整体加解密与主密码校验。
/// 全部基于 System.Security.Cryptography 实现, 不引用任何外部 NuGet 包。
/// </summary>
public static class CryptoService
{
    /// <summary>PBKDF2 盐长度 (字节)。</summary>
    public const int SaltSize = 16;

    /// <summary>AES-CBC 初始向量长度 (字节)。</summary>
    public const int IvSize = 16;

    /// <summary>HMAC-SHA256 输出长度 (字节)。</summary>
    public const int HmacSize = 32;

    /// <summary>密钥长度 (字节), 对应 AES-256。</summary>
    public const int KeySize = 32;

    /// <summary>PBKDF2-SHA256 迭代次数 (对标 Bitwarden 默认值)。</summary>
    public const int Pbkdf2Iterations = 600_000;

    private static readonly HashAlgorithmName Pbkdf2Hash = HashAlgorithmName.SHA256;

    private static readonly JsonSerializerOptions VaultJsonOptions = new()
    {
        // 允许中文等非 ASCII 字符以原样写入 (反序列化时同样兼容)。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 使用 PBKDF2-SHA256 由主密码派生 32 字节密钥。
    /// </summary>
    /// <param name="masterPassword">用户主密码。</param>
    /// <param name="salt">盐 (建议 16 字节)。</param>
    /// <returns>32 字节派生密钥。</returns>
    public static byte[] DeriveKey(string masterPassword, byte[] salt)
    {
        if (string.IsNullOrEmpty(masterPassword))
            throw new ArgumentException("主密码不能为空。", nameof(masterPassword));
        if (salt == null || salt.Length == 0)
            throw new ArgumentException("盐不能为空。", nameof(salt));

        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(masterPassword), salt, Pbkdf2Iterations, Pbkdf2Hash);
        return kdf.GetBytes(KeySize);
    }

    /// <summary>
    /// AES-256-CBC 加密 + HMAC-SHA256 认证 (Encrypt-then-MAC)。
    /// 内部生成随机 salt(16) 与 iv(16), 返回三元组 (salt, iv, ciphertext)。
    /// 其中返回的 ciphertext 字段为 <c>hmac(32) || ciphertext</c>,
    /// 因此拼接 salt + iv + ciphertext 即得完整密文块:
    ///   salt(16) || iv(16) || hmac(32) || ciphertext
    /// 该完整密文块可直接传入 <see cref="Decrypt(byte[], byte[])"/> 解密。
    /// </summary>
    /// <param name="plaintext">明文字节。</param>
    /// <param name="key">32 字节密钥。</param>
    /// <returns>(salt, iv, ciphertext), ciphertext 已含 HMAC 前缀。</returns>
    public static (byte[] salt, byte[] iv, byte[] ciphertext) Encrypt(byte[] plaintext, byte[] key)
    {
        if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
        if (key == null || key.Length != KeySize)
            throw new ArgumentException($"密钥必须为 {KeySize} 字节。", nameof(key));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        var (s, iv, mac, ct) = EncryptComponents(plaintext, key, salt);
        return (s, iv, Concat(mac, ct));
    }

    /// <summary>
    /// 使用指定 salt 加密, 返回完整密文块 salt + iv + hmac + ciphertext。
    /// 供 Vault 流程复用同一 salt 兼作 KDF 盐。
    /// </summary>
    private static byte[] EncryptToBlob(byte[] plaintext, byte[] key, byte[] salt)
    {
        var (s, iv, mac, ct) = EncryptComponents(plaintext, key, salt);
        return Concat(s, iv, mac, ct);
    }

    /// <summary>
    /// 加密核心: 返回 (salt, iv, hmac, ciphertext) 四个原始分量。
    /// HMAC-SHA256(key) 计算于 (salt || iv || ciphertext), 实现 Encrypt-then-MAC。
    /// </summary>
    private static (byte[] salt, byte[] iv, byte[] mac, byte[] ciphertext) EncryptComponents(
        byte[] plaintext, byte[] key, byte[] salt)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(IvSize);
        byte[] ciphertext = AesCbcEncrypt(plaintext, key, iv);
        byte[] mac = ComputeMac(key, salt, iv, ciphertext);
        return (salt, iv, mac, ciphertext);
    }

    /// <summary>
    /// 解密并验证 HMAC。输入应为完整密文块 salt + iv + hmac + ciphertext
    /// (即 <see cref="Encrypt"/> 三元组拼接后的结果)。
    /// 若 HMAC 校验失败则抛出 <see cref="CryptographicException"/>。
    /// </summary>
    /// <param name="encryptedData">salt + iv + hmac + ciphertext。</param>
    /// <param name="key">32 字节密钥。</param>
    /// <returns>明文字节。</returns>
    public static byte[] Decrypt(byte[] encryptedData, byte[] key)
    {
        if (encryptedData == null) throw new ArgumentNullException(nameof(encryptedData));
        if (key == null || key.Length != KeySize)
            throw new ArgumentException($"密钥必须为 {KeySize} 字节。", nameof(key));
        if (encryptedData.Length < SaltSize + IvSize + HmacSize)
            throw new CryptographicException("加密数据长度不足, 无法解析。");

        byte[] salt = encryptedData.AsSpan(0, SaltSize).ToArray();
        byte[] iv = encryptedData.AsSpan(SaltSize, IvSize).ToArray();
        byte[] mac = encryptedData.AsSpan(SaltSize + IvSize, HmacSize).ToArray();
        byte[] ciphertext = encryptedData.AsSpan(SaltSize + IvSize + HmacSize).ToArray();

        byte[] expectedMac = ComputeMac(key, salt, iv, ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
            throw new CryptographicException("HMAC 校验失败, 数据可能被篡改或主密码错误。");

        return AesCbcDecrypt(ciphertext, key, iv);
    }

    /// <summary>
    /// 将 <see cref="VaultData"/> 序列化为 JSON 并加密, 返回 Base64 字符串。
    /// 内部用同一 salt 兼作 PBKDF2 派生盐与密文块盐。
    /// </summary>
    /// <param name="data">密码库数据。</param>
    /// <param name="masterPassword">主密码。</param>
    /// <returns>Base64 编码的加密数据 (内含 salt + iv + hmac + ciphertext)。</returns>
    public static string EncryptVault(VaultData data, string masterPassword)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(data, VaultJsonOptions);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = DeriveKey(masterPassword, salt);
        byte[] blob = EncryptToBlob(plaintext, key, salt);
        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// 解密 Base64 数据并反序列化为 <see cref="VaultData"/>。
    /// 主密码错误或数据损坏时返回 null。
    /// </summary>
    /// <param name="base64Data">Base64 编码的加密数据。</param>
    /// <param name="masterPassword">主密码。</param>
    /// <returns>密码库数据; 失败返回 null。</returns>
    public static VaultData? DecryptVault(string base64Data, string masterPassword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                return null;

            byte[] blob = Convert.FromBase64String(base64Data);
            if (blob.Length < SaltSize + IvSize + HmacSize)
                return null;

            byte[] salt = blob.AsSpan(0, SaltSize).ToArray();
            byte[] key = DeriveKey(masterPassword, salt);
            byte[] plaintext = Decrypt(blob, key);
            return JsonSerializer.Deserialize<VaultData>(plaintext, VaultJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 验证主密码是否正确 (仅通过 HMAC 校验, 不进行解密, 避免填充异常)。
    /// </summary>
    /// <param name="base64Data">Base64 编码的加密数据。</param>
    /// <param name="masterPassword">待校验的主密码。</param>
    /// <returns>主密码正确返回 true, 否则 false。</returns>
    public static bool VerifyPassword(string base64Data, string masterPassword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                return false;

            byte[] blob = Convert.FromBase64String(base64Data);
            if (blob.Length < SaltSize + IvSize + HmacSize)
                return false;

            byte[] salt = blob.AsSpan(0, SaltSize).ToArray();
            byte[] key = DeriveKey(masterPassword, salt);

            byte[] iv = blob.AsSpan(SaltSize, IvSize).ToArray();
            byte[] mac = blob.AsSpan(SaltSize + IvSize, HmacSize).ToArray();
            byte[] ciphertext = blob.AsSpan(SaltSize + IvSize + HmacSize).ToArray();

            byte[] expectedMac = ComputeMac(key, salt, iv, ciphertext);
            return CryptographicOperations.FixedTimeEquals(mac, expectedMac);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>AES-256-CBC 加密 (PKCS7 填充)。</summary>
    private static byte[] AesCbcEncrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    /// <summary>AES-256-CBC 解密 (PKCS7 填充)。</summary>
    private static byte[] AesCbcDecrypt(byte[] ciphertext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>计算 HMAC-SHA256(key) over (salt || iv || ciphertext)。</summary>
    private static byte[] ComputeMac(byte[] key, byte[] salt, byte[] iv, byte[] ciphertext)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Concat(salt, iv, ciphertext));
    }

    /// <summary>按顺序拼接多个字节数组。</summary>
    private static byte[] Concat(params byte[][] arrays)
    {
        int total = 0;
        foreach (var a in arrays)
            total += a.Length;

        byte[] result = new byte[total];
        int offset = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
