using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed record PasteRewriteResult(string Text, bool Rewritten);

internal sealed class PastedImagePathService
{
    public bool CanRewritePaste(string pastedText, string? hostWorkingDirectory)
        => TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate)
            && IsSupportedImageFile(candidate.ResolvedHostPath);

    public PasteRewriteResult RewritePaste(string pastedText, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate))
        {
            return new PasteRewriteResult(pastedText, false);
        }

        if(!IsSupportedImageFile(candidate.ResolvedHostPath))
        {
            return new PasteRewriteResult(pastedText, false);
        }

        string stagedFileName = BuildStagedFileName(candidate.ResolvedHostPath);
        string stagedHostPath = Path.Combine(session.HostPasteDirectory, stagedFileName);

        try
        {
            Directory.CreateDirectory(session.HostPasteDirectory);
            File.Copy(candidate.ResolvedHostPath, stagedHostPath, overwrite: false);
        }
        catch(Exception ex)
        {
            AppIO.WriteWarning($"failed to stage pasted image '{candidate.ResolvedHostPath}': {ex.Message}");
            return new PasteRewriteResult(pastedText, false);
        }

        string containerPath = $"{session.ContainerPasteDirectory}/{stagedFileName}";
        string rewrittenCore = candidate.QuoteCharacter is char quoteCharacter
            ? $"{quoteCharacter}{containerPath}{quoteCharacter}"
            : containerPath;

        return new PasteRewriteResult(candidate.LeadingWhitespace + rewrittenCore + candidate.TrailingWhitespace, true);
    }

    private static bool TryNormalizePastedPath(string pastedText, string? hostWorkingDirectory, out NormalizedPathCandidate candidate)
    {
        candidate = default;

        if(String.IsNullOrWhiteSpace(pastedText) || pastedText.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        int start = 0;
        int end = pastedText.Length - 1;

        while(start <= end && Char.IsWhiteSpace(pastedText[start]))
        {
            start++;
        }

        while(end >= start && Char.IsWhiteSpace(pastedText[end]))
        {
            end--;
        }

        if(end < start)
        {
            return false;
        }

        string leadingWhitespace = pastedText[..start];
        string trailingWhitespace = pastedText[(end + 1)..];
        string core = pastedText[start..(end + 1)];
        if(core.Length == 0)
        {
            return false;
        }

        char? quoteCharacter = null;
        if(core.Length >= 2 && ((core[0] == '"' && core[^1] == '"') || (core[0] == '\'' && core[^1] == '\'')))
        {
            quoteCharacter = core[0];
            core = core[1..^1];
            if(core.Length == 0)
            {
                return false;
            }
        }

        if(core.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        if(!TryResolveHostPath(core, hostWorkingDirectory, out string resolvedHostPath))
        {
            return false;
        }

        if(!File.Exists(resolvedHostPath))
        {
            return false;
        }

        candidate = new NormalizedPathCandidate(leadingWhitespace, trailingWhitespace, quoteCharacter, resolvedHostPath);
        return true;
    }

    private static bool TryResolveHostPath(string candidatePath, string? hostWorkingDirectory, out string resolvedHostPath)
    {
        resolvedHostPath = String.Empty;

        if(candidatePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if(!Uri.TryCreate(candidatePath, UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile)
            {
                return false;
            }

            candidatePath = fileUri.LocalPath;
        }
        else if(Uri.TryCreate(candidatePath, UriKind.Absolute, out Uri? absoluteUri) && !absoluteUri.IsFile)
        {
            return false;
        }

        try
        {
            resolvedHostPath = Path.IsPathRooted(candidatePath)
                ? Path.GetFullPath(candidatePath)
                : Path.GetFullPath(Path.Combine(String.IsNullOrWhiteSpace(hostWorkingDirectory) ? Directory.GetCurrentDirectory() : hostWorkingDirectory, candidatePath));
            return true;
        }
        catch
        {
            resolvedHostPath = String.Empty;
            return false;
        }
    }

    private static bool IsSupportedImageFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if(String.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" => HasMagicHeader(filePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ".jpg" or ".jpeg" => HasMagicHeaderPrefix(filePath, [0xFF, 0xD8, 0xFF]),
            ".gif" => HasAnyMagicHeader(filePath, [[(byte) 'G', (byte) 'I', (byte) 'F', (byte) '8', (byte) '7', (byte) 'a'], [(byte) 'G', (byte) 'I', (byte) 'F', (byte) '8', (byte) '9', (byte) 'a']]),
            ".webp" => HasWebpHeader(filePath),
            ".bmp" => HasMagicHeaderPrefix(filePath, [(byte) 'B', (byte) 'M']),
            ".svg" => LooksLikeSvg(filePath),
            ".txt" => true,
            _ => false
        };
    }

    private static bool HasMagicHeader(string filePath, byte[] expectedHeader)
        => TryReadHeader(filePath, expectedHeader.Length, out byte[] actualHeader)
            && actualHeader.AsSpan().SequenceEqual(expectedHeader);

    private static bool HasMagicHeaderPrefix(string filePath, byte[] expectedPrefix)
        => TryReadHeader(filePath, expectedPrefix.Length, out byte[] actualHeader)
            && actualHeader.AsSpan().SequenceEqual(expectedPrefix);

    private static bool HasAnyMagicHeader(string filePath, byte[][] headers)
    {
        int headerLength = headers.Max(header => header.Length);
        if(!TryReadHeader(filePath, headerLength, out byte[] actualHeader))
        {
            return false;
        }

        return headers.Any(header => actualHeader.AsSpan(0, header.Length).SequenceEqual(header));
    }

    private static bool HasWebpHeader(string filePath)
    {
        if(!TryReadHeader(filePath, 12, out byte[] actualHeader))
        {
            return false;
        }

        return actualHeader.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && actualHeader.AsSpan(8, 4).SequenceEqual("WEBP"u8);
    }

    private static bool LooksLikeSvg(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if(bytesRead <= 0)
            {
                return false;
            }

            string snippet = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return snippet.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                || snippet.Contains("<?xml", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadHeader(string filePath, int requiredLength, out byte[] header)
    {
        header = [];

        try
        {
            using var stream = File.OpenRead(filePath);
            header = new byte[requiredLength];
            int bytesRead = stream.Read(header, 0, requiredLength);
            if(bytesRead < requiredLength)
            {
                Array.Resize(ref header, bytesRead);
            }

            return bytesRead >= requiredLength;
        }
        catch
        {
            header = [];
            return false;
        }
    }

    private static string BuildStagedFileName(string sourcePath)
    {
        string originalName = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);

        char[] sanitizedChars = originalName
            .Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray();

        string sanitizedName = new string(sanitizedChars).Trim('-', '.', '_');
        if(String.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "image";
        }

        return $"{Guid.NewGuid():N}-{sanitizedName}{extension}";
    }

    private readonly record struct NormalizedPathCandidate(
        string LeadingWhitespace,
        string TrailingWhitespace,
        char? QuoteCharacter,
        string ResolvedHostPath);
}
