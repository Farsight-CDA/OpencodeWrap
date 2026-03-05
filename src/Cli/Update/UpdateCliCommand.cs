using System.CommandLine;
using System.Reflection;
using System.Text.Json;

namespace OpencodeWrap.Cli.Update;

internal sealed class UpdateCliCommand : Command
{
    private const string DefaultNpmPackageName = "@farsight-cda/ocw";
    private static readonly HttpClient HttpClient = new();

    private readonly Option<bool> _checkOnlyOption;

    public UpdateCliCommand()
        : base("update", "Check for updates or self-update when installed via npm.")
    {
        _checkOnlyOption = new Option<bool>("--check")
        {
            Description = "Check for the latest npm version without installing it."
        };

        Add(_checkOnlyOption);

        SetAction(async parseResult =>
        {
            bool checkOnly = parseResult.GetValue(_checkOnlyOption);
            return await ExecuteAsync(checkOnly);
        });
    }

    private static async Task<int> ExecuteAsync(bool checkOnly)
    {
        string packageName = GetPackageName();
        string currentVersion = GetCurrentVersion();

        string? latestVersion = await TryGetLatestVersionAsync(packageName);
        if(String.IsNullOrWhiteSpace(latestVersion))
        {
            AppIO.WriteError($"Unable to fetch latest version for npm package '{packageName}'.");
            return 1;
        }

        AppIO.WriteInfo($"Current version: {currentVersion}");
        AppIO.WriteInfo($"Latest version: {latestVersion}");

        if(!IsNewerVersion(latestVersion, currentVersion))
        {
            AppIO.WriteSuccess("You are already on the latest version.");
            return 0;
        }

        if(checkOnly)
        {
            AppIO.WriteWarning("Update available. Run 'ocw update' to install the latest version.");
            return 0;
        }

        var npmCheck = await ProcessRunner.RunAsync("npm", ["--version"]);
        if(!npmCheck.Success)
        {
            AppIO.WriteError("npm is not available in PATH. Self-update requires npm global install support.");
            return 1;
        }

        string? installedGlobalVersion = await TryGetInstalledGlobalVersionAsync(packageName);
        if(String.IsNullOrWhiteSpace(installedGlobalVersion))
        {
            AppIO.WriteError($"Package '{packageName}' is not installed globally via npm on this machine.");
            AppIO.WriteInfo("Install with: npm i -g @farsight-cda/ocw");
            return 1;
        }

        AppIO.WriteInfo("Installing latest version via npm...");
        var updateResult = await ProcessRunner.RunAsync("npm", ["install", "-g", $"{packageName}@latest"]);
        if(!updateResult.Success)
        {
            string error = String.IsNullOrWhiteSpace(updateResult.StdErr)
                ? updateResult.StdOut
                : updateResult.StdErr;
            AppIO.WriteError("Self-update failed via npm.");
            if(!String.IsNullOrWhiteSpace(error))
            {
                AppIO.WriteError(error.Trim());
            }

            return 1;
        }

        string? finalVersion = await TryGetInstalledGlobalVersionAsync(packageName) ?? latestVersion;
        AppIO.WriteSuccess($"Updated to version {finalVersion}.");
        return 0;
    }

    private static string GetPackageName()
        => Environment.GetEnvironmentVariable("OCW_NPM_PACKAGE")?.Trim() is { Length: > 0 } packageFromEnv
            ? packageFromEnv
            : DefaultNpmPackageName;

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if(!String.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator > 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static async Task<string?> TryGetLatestVersionAsync(string packageName)
    {
        try
        {
            string encodedPackageName = Uri.EscapeDataString(packageName);
            string endpoint = $"https://registry.npmjs.org/{encodedPackageName}/latest";

            using var response = await HttpClient.GetAsync(endpoint);
            if(!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if(doc.RootElement.TryGetProperty("version", out JsonElement versionElement))
            {
                return versionElement.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetInstalledGlobalVersionAsync(string packageName)
    {
        var result = await ProcessRunner.RunAsync("npm", ["ls", "-g", "--depth=0", packageName, "--json"]);
        if(!result.Started || String.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if(!doc.RootElement.TryGetProperty("dependencies", out JsonElement dependencies))
            {
                return null;
            }

            if(!dependencies.TryGetProperty(packageName, out JsonElement packageNode))
            {
                return null;
            }

            if(!packageNode.TryGetProperty("version", out JsonElement versionNode))
            {
                return null;
            }

            return versionNode.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNewerVersion(string candidateVersion, string currentVersion)
    {
        if(String.Equals(candidateVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if(TryParseLooseVersion(candidateVersion, out Version? candidate)
            && TryParseLooseVersion(currentVersion, out Version? current))
        {
            return candidate > current;
        }

        return true;
    }

    private static bool TryParseLooseVersion(string value, out Version? version)
    {
        version = null;
        if(String.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if(trimmed.StartsWith('v'))
        {
            trimmed = trimmed[1..];
        }

        int prereleaseIndex = trimmed.IndexOf('-');
        if(prereleaseIndex >= 0)
        {
            trimmed = trimmed[..prereleaseIndex];
        }

        return Version.TryParse(trimmed, out version);
    }
}
