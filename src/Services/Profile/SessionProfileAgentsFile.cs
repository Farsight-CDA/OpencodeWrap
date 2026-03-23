namespace OpencodeWrap.Services.Profile;

internal static class SessionProfileAgentsFile
{
    public static void EnsureForLaunch(string agentsPath, bool includeProfileConfig, string runtimeAgentInstructions)
    {
        if(!includeProfileConfig)
        {
            return;
        }

        string? profileAgentInstructions = File.Exists(agentsPath)
            ? File.ReadAllText(agentsPath)
            : null;

        string mergedContent = MergeContent(profileAgentInstructions, runtimeAgentInstructions);
        File.WriteAllText(agentsPath, mergedContent);
    }

    public static void MergeIntoFile(string destinationPath, string sourcePath)
    {
        string? existingInstructions = File.Exists(destinationPath)
            ? File.ReadAllText(destinationPath)
            : null;
        string sourceInstructions = File.ReadAllText(sourcePath);
        File.WriteAllText(destinationPath, MergeContent(existingInstructions, sourceInstructions));
    }

    public static string MergeContent(params string?[] sections)
        => String.Join(
            Environment.NewLine + Environment.NewLine,
            sections
                .Where(content => !String.IsNullOrWhiteSpace(content))
                .Select(content => content!.TrimEnd()));
}