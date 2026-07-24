using Microsoft.AspNetCore.DataProtection;

namespace CICDTrg.Services
{
    public class EncryptionService
    {
        private readonly IDataProtector _protector;

        public EncryptionService(IDataProtectionProvider provider)
        {
            // The "CICDTrg.Secrets" string acts as a unique purpose indicator.
            // Data encrypted with this purpose can only be decrypted by a protector with the same purpose.
            _protector = provider.CreateProtector("CICDTrg.Secrets");
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            return _protector.Protect(plainText);
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return cipherText;
            try 
            { 
                return _protector.Unprotect(cipherText); 
            }
            catch 
            { 
                return "DECRYPTION_FAILED"; 
            }
        }
    }
}
