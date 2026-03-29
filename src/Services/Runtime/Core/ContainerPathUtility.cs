namespace OpencodeWrap.Services.Runtime.Core;

internal static class ContainerPathUtility
{
    public static bool TryNormalizeAbsolutePath(string requestedPath, out string normalizedPath)
    {
        normalizedPath = String.Empty;
        if(String.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        string trimmedPath = requestedPath.Trim();
        if(trimmedPath.Contains(','))
        {
            return false;
        }

        string posixPath = trimmedPath.Replace('\\', '/');
        if(!posixPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = new List<string>();
        foreach(string segment in posixPath.Split("/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if(segment == ".")
            {
                continue;
            }

            if(segment == "..")
            {
                if(segments.Count == 0)
                {
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        normalizedPath = segments.Count == 0
            ? "/"
            : $"/{String.Join("/", segments)}";
        return normalizedPath != "/";
    }

    public static bool PathsOverlap(string leftPath, string rightPath)
        => String.Equals(leftPath, rightPath, StringComparison.Ordinal)
            || IsAncestorPath(leftPath, rightPath)
            || IsAncestorPath(rightPath, leftPath);

    private static bool IsAncestorPath(string ancestorPath, string descendantPath)
        => descendantPath.Length > ancestorPath.Length
            && descendantPath.StartsWith(ancestorPath, StringComparison.Ordinal)
            && descendantPath[ancestorPath.Length] == '/';
}
