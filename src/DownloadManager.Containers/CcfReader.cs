using System.Security.Cryptography;

namespace DownloadManager.Containers;

internal sealed record CcfDecryptAttempt(byte[]? Plain, string? Version, ContainerProblem? Problem);

internal static class CcfReader
{
    private const int AesBlockBytes = 16;

    public static bool HasCcf30Magic(ReadOnlySpan<byte> data) => data.StartsWith(CcfFormatConstants.Ccf30Magic);

    public static CcfDecryptAttempt Decrypt(byte[] data)
    {
        if (HasCcf30Magic(data))
        {
            var plain = Ccf30Cipher.Decrypt(data, out var problem);
            if (plain is null)
            {
                return new CcfDecryptAttempt(null, null, problem);
            }

            return HasMarker(plain)
                ? new CcfDecryptAttempt(plain, CcfFormatConstants.Version30, null)
                : new CcfDecryptAttempt(null, null, NotDecrypted());
        }

        if (data.Length == 0 || data.Length % AesBlockBytes != 0)
        {
            return new CcfDecryptAttempt(null, null, new ContainerProblem(ContainerProblemKind.Truncated, "This CCF file looks cut short: it does not hold a whole number of cipher blocks."));
        }

        foreach (var pair in CcfFormatConstants.Legacy)
        {
            var plain = DecryptCbc(data, pair);
            if (HasMarker(plain))
            {
                return new CcfDecryptAttempt(plain, CcfFormatConstants.Version07To10, null);
            }
        }

        var layered = DecryptCbc(data, CcfFormatConstants.Ccf50Outer);
        layered = DecryptCbc(layered, CcfFormatConstants.Ccf50Inner);
        layered = DecryptCbc(layered, CcfFormatConstants.Ccf50Outer);
        return HasMarker(layered)
            ? new CcfDecryptAttempt(layered, CcfFormatConstants.Version50, null)
            : new CcfDecryptAttempt(null, null, NotDecrypted());
    }

    public static ContainerImportResult Parse(CcfDecryptAttempt attempt, ContainerLimits limits)
    {
        var builder = new ContainerResultBuilder(ContainerFormat.Ccf, limits);
        if (attempt.Plain is null)
        {
            builder.AddProblem(attempt.Problem!.Kind, attempt.Problem.Message);
            return builder.Build();
        }

        CcfPayloadParser.Parse(attempt.Plain, builder, limits);
        return builder.Build(attempt.Version);
    }

    private static bool HasMarker(ReadOnlySpan<byte> plain) => plain.IndexOf(CcfFormatConstants.DecryptedMarker) >= 0;

    private static byte[] DecryptCbc(byte[] cipher, CcfKeyPair pair)
    {
        using var aes = Aes.Create();
        aes.Key = pair.Key;
        return aes.DecryptCbc(cipher, pair.Iv, PaddingMode.None);
    }

    private static ContainerProblem NotDecrypted() =>
        new(ContainerProblemKind.DecryptionFailed, "This CCF file could not be decrypted. It may be damaged, or a version this app cannot read.");
}
