using System.Text;

namespace OpencodeWrap.Services.Runtime.Launcher;

internal sealed record SessionEnvironmentVariable(string Key, string Value, string SourceLabel);

internal static class SessionProfileEnvFile
{
    public static bool TryPrepareForLaunch(
        ResolvedProfile profile,
        string sessionProfileDirectoryPath,
        IReadOnlyList<ResolvedSessionAddon> sessionAddons,
        out IReadOnlyList<SessionEnvironmentVariable> environmentVariables,
        out string? errorMessage)
    {
        environmentVariables = [];
        errorMessage = null;

        var sources = BuildSources(profile, sessionAddons);
        if(sources.Count == 0)
        {
            string sessionEnvFilePath = Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_ENV_FILE_NAME);
            if(File.Exists(sessionEnvFilePath))
            {
                File.Delete(sessionEnvFilePath);
            }

            return true;
        }

        var mergedVariables = new List<SessionEnvironmentVariable>();
        var seenKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach(var source in sources)
        {
            if(!TryParseSource(source, out var parsedVariables, out errorMessage))
            {
                return false;
            }

            foreach(var parsedVariable in parsedVariables)
            {
                if(seenKeys.TryGetValue(parsedVariable.Key, out string? existingSourceLabel))
                {
                    errorMessage = $"Environment variable '{parsedVariable.Key}' is defined in both {existingSourceLabel} and {source.Label}. Duplicate keys are not supported yet.";
                    return false;
                }

                seenKeys[parsedVariable.Key] = source.Label;
                mergedVariables.Add(new SessionEnvironmentVariable(parsedVariable.Key, parsedVariable.Value, source.Label));
            }
        }

        string mergedEnvFilePath = Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_ENV_FILE_NAME);
        File.WriteAllText(mergedEnvFilePath, BuildFileContents(mergedVariables));
        environmentVariables = mergedVariables;
        return true;
    }

    private static List<SessionEnvSource> BuildSources(ResolvedProfile profile, IReadOnlyList<ResolvedSessionAddon> sessionAddons)
    {
        var sources = new List<SessionEnvSource>();

        TryAddSource(sources, Path.Combine(profile.DirectoryPath, OpencodeWrapConstants.PROFILE_ENV_FILE_NAME), $"profile '{profile.Name}' .env");

        foreach(var addon in sessionAddons)
        {
            TryAddSource(sources, Path.Combine(addon.DirectoryPath, OpencodeWrapConstants.PROFILE_ENV_FILE_NAME), $"session addon '{addon.Name}' .env");
        }

        return sources;
    }

    private static void TryAddSource(List<SessionEnvSource> sources, string filePath, string label)
    {
        if(File.Exists(filePath))
        {
            sources.Add(new SessionEnvSource(filePath, label));
        }
    }

    private static bool TryParseSource(
        SessionEnvSource source,
        out IReadOnlyList<KeyValuePair<string, string>> variables,
        out string? errorMessage)
    {
        variables = [];
        errorMessage = null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(source.FilePath);
        }
        catch(Exception ex)
        {
            errorMessage = $"Failed to read {source.Label} at '{source.FilePath}': {ex.Message}";
            return false;
        }

        var parsedVariables = new List<KeyValuePair<string, string>>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for(int index = 0; index < lines.Length; index++)
        {
            if(!TryParseLine(lines[index], index + 1, source, out var parsedVariable, out errorMessage))
            {
                return false;
            }

            if(parsedVariable is null)
            {
                continue;
            }

            if(!seenKeys.Add(parsedVariable.Value.Key))
            {
                errorMessage = $"Environment variable '{parsedVariable.Value.Key}' is defined more than once in {source.Label}. Duplicate keys are not supported yet.";
                return false;
            }

            parsedVariables.Add(parsedVariable.Value);
        }

        variables = parsedVariables;
        return true;
    }

    private static bool TryParseLine(
        string line,
        int lineNumber,
        SessionEnvSource source,
        out KeyValuePair<string, string>? variable,
        out string? errorMessage)
    {
        variable = null;
        errorMessage = null;

        var trimmedLine = line.AsSpan().Trim();
        if(trimmedLine.Length == 0 || trimmedLine[0] == '#')
        {
            return true;
        }

        var declaration = trimmedLine.StartsWith("export ", StringComparison.Ordinal)
            ? trimmedLine["export ".Length..].TrimStart()
            : trimmedLine;

        int separatorIndex = declaration.IndexOf('=');
        if(separatorIndex <= 0)
        {
            errorMessage = $"Invalid environment declaration in {source.Label} at line {lineNumber}. Expected KEY=VALUE.";
            return false;
        }

        string key = declaration[..separatorIndex].Trim().ToString();
        if(!IsValidKey(key))
        {
            errorMessage = $"Invalid environment variable name '{key}' in {source.Label} at line {lineNumber}.";
            return false;
        }

        string rawValue = declaration[(separatorIndex + 1)..].ToString();
        if(!TryParseValue(rawValue, out string value, out string? valueErrorMessage))
        {
            errorMessage = $"{valueErrorMessage} in {source.Label} at line {lineNumber}.";
            return false;
        }

        variable = new KeyValuePair<string, string>(key, value);
        return true;
    }

    private static bool TryParseValue(string rawValue, out string value, out string? errorMessage)
    {
        errorMessage = null;
        string candidate = rawValue.TrimStart();
        if(candidate.Length == 0)
        {
            value = String.Empty;
            return true;
        }

        return candidate[0] switch
        {
            '\'' => TryParseSingleQuotedValue(candidate, out value, out errorMessage),
            '"' => TryParseDoubleQuotedValue(candidate, out value, out errorMessage),
            _ => TryParseUnquotedValue(candidate, out value, out errorMessage)
        };
    }

    private static bool TryParseSingleQuotedValue(string rawValue, out string value, out string? errorMessage)
    {
        errorMessage = null;
        int closingQuoteIndex = rawValue.IndexOf('\'', 1);
        if(closingQuoteIndex < 0)
        {
            value = String.Empty;
            errorMessage = "Missing closing single quote for environment variable value";
            return false;
        }

        if(!TryValidateTrailingCharacters(rawValue[(closingQuoteIndex + 1)..], out errorMessage))
        {
            value = String.Empty;
            return false;
        }

        value = rawValue[1..closingQuoteIndex];
        return true;
    }

    private static bool TryParseDoubleQuotedValue(string rawValue, out string value, out string? errorMessage)
    {
        errorMessage = null;
        var builder = new StringBuilder(rawValue.Length);
        bool escaping = false;

        for(int index = 1; index < rawValue.Length; index++)
        {
            char current = rawValue[index];
            if(escaping)
            {
                builder.Append(current switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => current
                });
                escaping = false;
                continue;
            }

            if(current == '\\')
            {
                escaping = true;
                continue;
            }

            if(current == '"')
            {
                if(!TryValidateTrailingCharacters(rawValue[(index + 1)..], out errorMessage))
                {
                    value = String.Empty;
                    return false;
                }

                value = builder.ToString();
                return true;
            }

            builder.Append(current);
        }

        value = String.Empty;
        errorMessage = escaping
            ? "Environment variable value ends with an incomplete escape sequence"
            : "Missing closing double quote for environment variable value";
        return false;
    }

    private static bool TryParseUnquotedValue(string rawValue, out string value, out string? errorMessage)
    {
        errorMessage = null;
        int commentIndex = -1;
        for(int index = 0; index < rawValue.Length; index++)
        {
            if(rawValue[index] == '#' && (index == 0 || Char.IsWhiteSpace(rawValue[index - 1])))
            {
                commentIndex = index;
                break;
            }
        }

        string withoutComment = commentIndex >= 0
            ? rawValue[..commentIndex]
            : rawValue;
        value = withoutComment.Trim();
        return true;
    }

    private static bool TryValidateTrailingCharacters(string trailingText, out string? errorMessage)
    {
        string trailing = trailingText.Trim();
        if(trailing.Length == 0 || trailing.StartsWith('#'))
        {
            errorMessage = null;
            return true;
        }

        errorMessage = "Unexpected characters after quoted environment variable value";
        return false;
    }

    private static bool IsValidKey(string key)
        => !String.IsNullOrWhiteSpace(key)
            && !key.Contains('=')
            && !key.Any(Char.IsWhiteSpace)
            && !key.Contains('\0');

    private static string BuildFileContents(IReadOnlyList<SessionEnvironmentVariable> variables)
    {
        var builder = new StringBuilder();
        for(int index = 0; index < variables.Count; index++)
        {
            var variable = variables[index];
            builder.Append(variable.Key);
            builder.Append('=');
            builder.Append(FormatValue(variable.Value));
            if(index < variables.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string FormatValue(string value)
    {
        if(value.Length == 0)
        {
            return String.Empty;
        }

        bool requiresQuotes = value.Any(ch => Char.IsWhiteSpace(ch) || ch is '#' or '"' or '\'' or '\\' or '=' or '\n' or '\r' or '\t');
        if(!requiresQuotes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach(char ch in value)
        {
            builder.Append(ch switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\\' => "\\\\",
                '"' => "\\\"",
                _ => ch
            });
        }

        builder.Append('"');
        return builder.ToString();
    }

    private sealed record SessionEnvSource(string FilePath, string Label);
}
