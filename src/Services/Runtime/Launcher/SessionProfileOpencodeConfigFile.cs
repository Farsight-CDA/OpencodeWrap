using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpencodeWrap.Services.Runtime.Launcher;

internal static class SessionProfileOpencodeConfigFile
{
    public static bool TryPrepareForLaunch(
        ResolvedProfile profile,
        string sessionProfileDirectoryPath,
        IReadOnlyList<ResolvedSessionAddon> sessionAddons,
        out string? errorMessage)
    {
        errorMessage = null;

        var sources = BuildSources(profile, sessionAddons);
        string sessionConfigDirectoryPath = Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
        string mergedConfigFilePath = Path.Combine(sessionConfigDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_CONFIG_FILE_NAME);

        if(sources.Count == 0)
        {
            if(File.Exists(mergedConfigFilePath))
            {
                File.Delete(mergedConfigFilePath);
            }

            return true;
        }

        JsonObject? mergedRoot = null;
        foreach(var source in sources)
        {
            if(!TryParseSource(source, out JsonObject? sourceRoot, out errorMessage))
            {
                return false;
            }

            mergedRoot = mergedRoot is null
                ? (JsonObject)sourceRoot!.DeepClone()
                : MergeObjects(mergedRoot, sourceRoot!);
        }

        Directory.CreateDirectory(sessionConfigDirectoryPath);

        using var stream = File.Create(mergedConfigFilePath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true
        });

        mergedRoot!.WriteTo(writer);
        writer.Flush();
        return true;
    }

    private static List<SessionOpencodeConfigSource> BuildSources(ResolvedProfile profile, IReadOnlyList<ResolvedSessionAddon> sessionAddons)
    {
        var sources = new List<SessionOpencodeConfigSource>();

        TryAddSource(
            sources,
            Path.Combine(profile.DirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME, OpencodeWrapConstants.PROFILE_OPENCODE_CONFIG_FILE_NAME),
            $"profile '{profile.Name}' opencode/opencode.json");

        foreach(var addon in sessionAddons)
        {
            TryAddSource(
                sources,
                Path.Combine(addon.DirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME, OpencodeWrapConstants.PROFILE_OPENCODE_CONFIG_FILE_NAME),
                $"session addon '{addon.Name}' opencode/opencode.json");
        }

        return sources;
    }

    private static void TryAddSource(List<SessionOpencodeConfigSource> sources, string filePath, string label)
    {
        if(File.Exists(filePath))
        {
            sources.Add(new SessionOpencodeConfigSource(filePath, label));
        }
    }

    private static bool TryParseSource(
        SessionOpencodeConfigSource source,
        out JsonObject? rootObject,
        out string? errorMessage)
    {
        rootObject = null;
        errorMessage = null;

        try
        {
            JsonNode? rootNode = JsonNode.Parse(File.ReadAllText(source.FilePath));
            if(rootNode is null)
            {
                errorMessage = $"{source.Label} at '{source.FilePath}' is empty. Expected a JSON object.";
                return false;
            }

            if(rootNode is not JsonObject jsonObject)
            {
                errorMessage = $"{source.Label} at '{source.FilePath}' must contain a JSON object at the root.";
                return false;
            }

            rootObject = jsonObject;
            return true;
        }
        catch(JsonException ex)
        {
            errorMessage = $"Failed to parse {source.Label} at '{source.FilePath}': {ex.Message}";
            return false;
        }
        catch(Exception ex)
        {
            errorMessage = $"Failed to read {source.Label} at '{source.FilePath}': {ex.Message}";
            return false;
        }
    }

    private static JsonObject MergeObjects(JsonObject destination, JsonObject source)
    {
        foreach(var entry in source)
        {
            if(entry.Value is JsonObject sourceChildObject && destination[entry.Key] is JsonObject destinationChildObject)
            {
                destination[entry.Key] = MergeObjects(destinationChildObject, sourceChildObject);
                continue;
            }

            destination[entry.Key] = entry.Value?.DeepClone();
        }

        return destination;
    }

    private sealed record SessionOpencodeConfigSource(string FilePath, string Label);
}
