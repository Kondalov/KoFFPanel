using KoFFPanel.Infrastructure.Services;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KoFFPanel.Tests;

public class ProfileCryptoTests
{
    private const string MasterKey = "Test_Master_Key_For_Unit_Tests_2026";

    [Theory]
    [InlineData("MySecretPassword123!")]
    [InlineData("VeryComplexP@ssw0rd__with--symbols!!")]
    [InlineData("РусскийТекстПароля")]
    [InlineData("")]
    public void AesGcm_EncryptAndDecrypt_ShouldRestoreOriginalText(string originalText)
    {
        // Act
        string cipherText = ProfileRepository.EncryptAesGcm(originalText, MasterKey);
        string decryptedText = ProfileRepository.DecryptAesGcm(cipherText, MasterKey);

        // Assert
        Assert.Equal(originalText, decryptedText);
        if (!string.IsNullOrEmpty(originalText))
        {
            Assert.NotEqual(originalText, cipherText);
        }
    }

    [Fact]
    public void AesGcm_TamperedCiphertext_ShouldThrowAuthenticationTagMismatchException()
    {
        // Arrange
        string original = "RootPassword12345";
        string cipherText = ProfileRepository.EncryptAesGcm(original, MasterKey);

        byte[] cipherBytes = Convert.FromBase64String(cipherText);
        // Tamper with last byte
        cipherBytes[^1] ^= 0xFF;
        string tamperedCipher = Convert.ToBase64String(cipherBytes);

        // Act & Assert
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileRepository.DecryptAesGcm(tamperedCipher, MasterKey));
    }

    [Fact]
    public void AesGcm_WrongKey_ShouldThrowAuthenticationTagMismatchException()
    {
        // Arrange
        string original = "Secret";
        string cipherText = ProfileRepository.EncryptAesGcm(original, MasterKey);

        // Act & Assert
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileRepository.DecryptAesGcm(cipherText, "Wrong_Password_12345"));
    }

    [Fact]
    public void DecryptLegacyAesCbc_ShouldSuccessfullyDecryptLegacyCbcPayload()
    {
        // Arrange: create an authentic AES-CBC encrypted payload with SHA256(key)
        string plaintext = "LegacySecretPassword";
        byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(MasterKey));

        byte[] cipherWithIv;
        using (var aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plaintext);
            }
            cipherWithIv = ms.ToArray();
        }

        string legacyCipherBase64 = Convert.ToBase64String(cipherWithIv);

        // Act
        string result = ProfileRepository.DecryptLegacyAesCbc(legacyCipherBase64, MasterKey);

        // Assert
        Assert.Equal(plaintext, result);
    }
}
