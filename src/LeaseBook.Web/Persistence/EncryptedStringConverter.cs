using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.Web.Persistence;

/// <summary>Encrypts a string column at rest with ASP.NET Data Protection. Used for the Identity
/// token store so the TOTP authenticator key and recovery codes are not stored in clear.
/// The Identity token's Value column is nullable, so null passes through untouched.</summary>
public sealed class EncryptedStringConverter(IDataProtector protector)
    : ValueConverter<string?, string?>(
        plaintext => plaintext == null ? null : protector.Protect(plaintext),
        ciphertext => ciphertext == null ? null : protector.Unprotect(ciphertext));
