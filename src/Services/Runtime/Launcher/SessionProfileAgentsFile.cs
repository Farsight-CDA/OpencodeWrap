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

        string mergedContent = BuildMergedContent(globalAgentInstructions, profileAgentInstructions, runtimeAgentInstructions);
        File.WriteAllText(agentsPath, mergedContent);
    }

    internal static string BuildMergedContent(string? globalAgentInstructions, string? profileAgentInstructions, string? runtimeAgentInstructions)
    {
        List<string> sections = [];
        AppendSection(sections, globalAgentInstructions);
        AppendSection(sections, profileAgentInstructions);
        AppendSection(sections, runtimeAgentInstructions);
        return String.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static void AppendSection(List<string> sections, string? content)
    {
        if(String.IsNullOrWhiteSpace(content))
        {
            return;
        }

        sections.Add(content.TrimEnd());
    }
}
