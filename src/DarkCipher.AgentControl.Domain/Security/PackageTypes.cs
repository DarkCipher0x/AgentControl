namespace DarkCipher.AgentControl.Domain.Security;

public sealed record ExtractionLimits(int MaxEntries = 2000, long MaxTotalBytes = 200L * 1024 * 1024, long MaxEntryBytes = 100L * 1024 * 1024, double MaxCompressionRatio = 200);

public sealed record ExtractionResult(bool Success, string Message, IReadOnlyList<string> Files);

public enum SignatureStatus { Unsigned, Valid, Invalid, Untrusted, NotChecked }

/// <summary>Port: integrity checks and archive extraction that cannot escape the destination.</summary>
public interface IPackageVerifier
{
    string ComputeSha256(string filePath);
    bool VerifySha256(string filePath, string expectedHex);
    ExtractionResult ExtractZipSafely(string zipPath, string destinationDirectory, ExtractionLimits limits);
    SignatureStatus CheckAuthenticode(string filePath);
}
