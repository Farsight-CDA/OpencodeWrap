namespace OpencodeWrap.Services.Runtime;

internal sealed record PasteRewriteResult(string Text, bool Rewritten);

internal sealed partial class PastedImagePathService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool CanRewritePaste(string pastedText, string? hostWorkingDirectory)
        => TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate)
            && IsSupportedImageFile(candidate.ResolvedHostPath);

    public PasteRewriteResult RewriteClipboardImage(byte[] imageBytes, string extension, InteractiveSessionContext session, string stagingKey)
    {
        _deferredSessionLogService.Write("paste", $"clipboard image rewrite requested; extension={extension}; bytes={imageBytes.Length}; stagingKey={stagingKey}");

        if(imageBytes.Length == 0 || !IsSupportedImageExtension(extension))
        {
            _deferredSessionLogService.Write("paste", $"clipboard image rewrite rejected; unsupported extension={extension}", DeferredSessionLogService.Importance.Significant);
            return new PasteRewriteResult("", false);
        }

        string normalizedExtension = NormalizeImageExtension(extension);
        string stagedFileName = session.StagedPastePaths.GetOrAdd(
            $"clipboard-image:{stagingKey}",
            _ => BuildStagedFileName($"clipboard-image{normalizedExtension}"));
        string stagedHostPath = Path.Combine(session.HostPasteDirectory, stagedFileName);

        try
        {
            Directory.CreateDirectory(session.HostPasteDirectory);
            if(!File.Exists(stagedHostPath))
            {
                File.WriteAllBytes(stagedHostPath, imageBytes);
                _deferredSessionLogService.Write("paste", $"clipboard image staged; hostPath={stagedHostPath}");
            }
            else
            {
                _deferredSessionLogService.Write("paste", $"clipboard image reused existing staged file; hostPath={stagedHostPath}");
            }
        }
        catch(Exception ex)
        {
            session.StagedPastePaths.TryRemove($"clipboard-image:{stagingKey}", out _);
            AppIO.WriteWarning($"failed to stage pasted clipboard image '{stagedHostPath}': {ex.Message}");
            _deferredSessionLogService.Write("paste", $"clipboard image staging failed; hostPath={stagedHostPath}; error={ex.Message}", DeferredSessionLogService.Importance.Significant);
            return new PasteRewriteResult("", false);
        }

        string containerPath = $"{session.ContainerPasteDirectory}/{stagedFileName}";
        _deferredSessionLogService.Write("paste", $"clipboard image rewrite succeeded; containerPath={containerPath}", DeferredSessionLogService.Importance.Significant);
        return new PasteRewriteResult(containerPath, true);
    }

    public PasteRewriteResult RewritePaste(string pastedText, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!TryNormalizePastedPath(pastedText, hostWorkingDirectory, out var candidate))
        {
            _deferredSessionLogService.Write("paste", $"path paste rewrite skipped; text did not resolve to a file path; preview={DescribeForLog(pastedText)}");
            return new PasteRewriteResult(pastedText, false);
        }

        if(!IsSupportedImageFile(candidate.ResolvedHostPath))
        {
            _deferredSessionLogService.Write("paste", $"path paste rewrite skipped; unsupported image extension; hostPath={candidate.ResolvedHostPath}");
            return new PasteRewriteResult(pastedText, false);
        }

        _deferredSessionLogService.Write("paste", $"path paste rewrite requested; hostPath={candidate.ResolvedHostPath}");

        string stagedFileName = session.StagedPastePaths.GetOrAdd(candidate.ResolvedHostPath, BuildStagedFileName);
        string stagedHostPath = Path.Combine(session.HostPasteDirectory, stagedFileName);

        try
        {
            Directory.CreateDirectory(session.HostPasteDirectory);
            if(!File.Exists(stagedHostPath))
            {
                File.Copy(candidate.ResolvedHostPath, stagedHostPath, overwrite: false);
                _deferredSessionLogService.Write("paste", $"path paste staged; hostPath={stagedHostPath}");
            }
            else
            {
                _deferredSessionLogService.Write("paste", $"path paste reused existing staged file; hostPath={stagedHostPath}");
            }
        }
        catch(Exception ex)
        {
            session.StagedPastePaths.TryRemove(candidate.ResolvedHostPath, out _);
            AppIO.WriteWarning($"failed to stage pasted image '{candidate.ResolvedHostPath}': {ex.Message}");
            _deferredSessionLogService.Write("paste", $"path paste staging failed; sourcePath={candidate.ResolvedHostPath}; error={ex.Message}", DeferredSessionLogService.Importance.Significant);
            return new PasteRewriteResult(pastedText, false);
        }

        string containerPath = $"{session.ContainerPasteDirectory}/{stagedFileName}";
        string rewrittenCore = candidate.QuoteCharacter is char quoteCharacter
            ? $"{quoteCharacter}{containerPath}{quoteCharacter}"
            : containerPath;

        _deferredSessionLogService.Write("paste", $"path paste rewrite succeeded; containerPath={containerPath}", DeferredSessionLogService.Importance.Significant);
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
        return IsSupportedImageExtension(extension);
    }

    private static bool IsSupportedImageExtension(string extension)
        => NormalizeImageExtension(extension) switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => true,
            _ => false
        };

    private static string NormalizeImageExtension(string extension)
    {
        if(String.IsNullOrWhiteSpace(extension))
        {
            return "";
        }

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : $".{extension.ToLowerInvariant()}";
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

    private static string DescribeForLog(string value)
    {
        if(String.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        string sanitized = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return sanitized.Length <= 120 ? sanitized : sanitized[..120] + "...";
    }

    private readonly record struct NormalizedPathCandidate(
        string LeadingWhitespace,
        string TrailingWhitespace,
        char? QuoteCharacter,
        string ResolvedHostPath);
}
