namespace OpencodeWrap.Services.Runtime;

internal sealed record PasteRewriteResult(string Text, bool Rewritten);

internal static class PastedImagePathService
{
    public static bool CanRewritePaste(string pastedText, string? hostWorkingDirectory)
        => TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate)
            && IsSupportedImageFile(candidate.ResolvedHostPath);

    public static PasteRewriteResult RewritePaste(string pastedText, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate))
        {
            return new PasteRewriteResult(pastedText, false);
        }

        if(!IsSupportedImageFile(candidate.ResolvedHostPath))
        {
            return new PasteRewriteResult(pastedText, false);
        }

        string stagedFileName = session.StagedPastePaths.GetOrAdd(candidate.ResolvedHostPath, BuildStagedFileName);
        string stagedHostPath = Path.Combine(session.HostPasteDirectory, stagedFileName);

        try
        {
            Directory.CreateDirectory(session.HostPasteDirectory);
            if(!File.Exists(stagedHostPath))
            {
                File.Copy(candidate.ResolvedHostPath, stagedHostPath, overwrite: false);
            }
        }
        catch(Exception ex)
        {
            session.StagedPastePaths.TryRemove(candidate.ResolvedHostPath, out _);
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
        resolvedHostPath = "";

        if(candidatePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if(!Uri.TryCreate(candidatePath, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
            {
                return false;
            }

            candidatePath = fileUri.LocalPath;
        }
        else if(Uri.TryCreate(candidatePath, UriKind.Absolute, out var absoluteUri) && !absoluteUri.IsFile)
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
            resolvedHostPath = "";
            return false;
        }
    }

    private static bool IsSupportedImageFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return !String.IsNullOrWhiteSpace(extension)
            && extension.ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => true,
                _ => false
            };
    }

    private static string BuildStagedFileName(string sourcePath)
    {
        string originalName = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);

        char[] sanitizedChars = [.. originalName.Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')];

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
