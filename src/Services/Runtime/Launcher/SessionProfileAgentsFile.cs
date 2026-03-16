namespace OpencodeWrap.Services.Runtime.Launcher;

internal static class SessionProfileAgentsFile
{
    public static void EnsureForLaunch(string agentsPath, bool includeProfileConfig, string? globalAgentInstructions, string runtimeAgentInstructions)
    {
        if(!includeProfileConfig)
        {
            return;
        }

        string? profileAgentInstructions = File.Exists(agentsPath)
            ? File.ReadAllText(agentsPath)
            : null;

        string mergedContent = String.Join(
            Environment.NewLine + Environment.NewLine,
            new[] { globalAgentInstructions, profileAgentInstructions, runtimeAgentInstructions }
                .Where(content => !String.IsNullOrWhiteSpace(content))
                .Select(content => content!.TrimEnd()));
        File.WriteAllText(agentsPath, mergedContent);
    }
}
