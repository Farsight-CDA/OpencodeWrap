using System.CommandLine;
using System.Text.Json;

namespace OpencodeWrap.Cli.Update;

internal sealed class UpdateCliCommand : Command
{
    private const string DefaultNpmPackageName = "@farsight-cda/ocw";
    private static readonly HttpClient HttpClient = new();
    private const string LatestDistTag = "latest";
    private const string DevDistTag = "dev";

    private readonly Option<bool> _checkOnlyOption;
    private readonly Option<bool> _devOption;

    public UpdateCliCommand()
        : base("update", "Check for updates or self-update when installed via npm.")
    {
        _checkOnlyOption = new Option<bool>("--check")
        {
            Description = "Check for the latest npm version without installing it."
        };

        _devOption = new Option<bool>("--dev")
        {
            Description = "Check and install from the npm 'dev' dist-tag instead of 'latest'."
        };

        Add(_checkOnlyOption);
        Add(_devOption);

        SetAction(async parseResult =>
        {
            bool checkOnly = parseResult.GetValue(_checkOnlyOption);
            bool useDevTag = parseResult.GetValue(_devOption);
            return await ExecuteAsync(checkOnly, useDevTag);
        });
    }

    private static async Task<int> ExecuteAsync(bool checkOnly, bool useDevTag)
    {
        string packageName = GetPackageName();
        var npmCheck = await ProcessRunner.RunAsync("npm", ["--version"]);
        string distTag = useDevTag ? DevDistTag : LatestDistTag;

        if(!npmCheck.Success)
        {
            AppIO.WriteError("npm is not available in PATH. Self-update requires npm global install support.");
            return 1;
        }

        string? installedGlobalVersion = await TryGetInstalledGlobalVersionAsync(packageName);
        if(String.IsNullOrWhiteSpace(installedGlobalVersion))
        {
            AppIO.WriteError($"Package '{packageName}' is not installed globally via npm on this machine.");
            AppIO.WriteInfo($"Install with: npm i -g {packageName}@{distTag}");
            return 1;
        }

        string currentVersion = installedGlobalVersion;

        string? latestVersion = await TryGetVersionForDistTagAsync(packageName, distTag);
        if(String.IsNullOrWhiteSpace(latestVersion))
        {
            AppIO.WriteError($"Unable to fetch npm dist-tag '{distTag}' for package '{packageName}'.");
            return 1;
        }

        AppIO.WriteInfo($"Current version: {currentVersion}");
        AppIO.WriteInfo($"Target ({distTag}) version: {latestVersion}");

        if(!IsNewerVersion(latestVersion, currentVersion))
        {
            AppIO.WriteSuccess($"You are already on the newest version for the '{distTag}' dist-tag.");
            return 0;
        }

        if(checkOnly)
        {
            string command = useDevTag ? "ocw update --dev" : "ocw update";
            AppIO.WriteWarning($"Update available. Run '{command}' to install from the '{distTag}' dist-tag.");
            return 0;
        }

        AppIO.WriteInfo($"Installing '{distTag}' version via npm...");
        var updateResult = await ProcessRunner.RunAsync("npm", ["install", "-g", $"{packageName}@{distTag}"]);
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

    private static async Task<string?> TryGetVersionForDistTagAsync(string packageName, string distTag)
    {
        try
        {
            string encodedPackageName = Uri.EscapeDataString(packageName);
            string endpoint = $"https://registry.npmjs.org/-/package/{encodedPackageName}/dist-tags";

            using var response = await HttpClient.GetAsync(endpoint);
            if(!response.IsSuccessStatusCode)
            {
                return null;
            }
            
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if(doc.RootElement.TryGetProperty(distTag, out JsonElement versionElement))
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

        if(TryCompareSemVer(candidateVersion, currentVersion, out int comparison))
        {
            return comparison > 0;
        }

        return true;
    }

    private static bool TryCompareSemVer(string left, string right, out int comparison)
    {
        comparison = 0;
        if(!TryParseSemVer(left, out SemVerParts? leftParts)
            || !TryParseSemVer(right, out SemVerParts? rightParts))
        {
            return false;
        }

        SemVerParts leftSemVer = leftParts!;
        SemVerParts rightSemVer = rightParts!;

        comparison = CompareCore(leftSemVer.Core, rightSemVer.Core);
        if(comparison != 0)
        {
            return true;
        }

        if(String.IsNullOrEmpty(leftSemVer.Prerelease) && String.IsNullOrEmpty(rightSemVer.Prerelease))
        {
            comparison = 0;
            return true;
        }

        if(String.IsNullOrEmpty(leftSemVer.Prerelease))
        {
            comparison = 1;
            return true;
        }

        if(String.IsNullOrEmpty(rightSemVer.Prerelease))
        {
            comparison = -1;
            return true;
        }

        comparison = ComparePrerelease(leftSemVer.Prerelease, rightSemVer.Prerelease);
        return true;
    }

    private static int CompareCore(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        int maxLength = Math.Max(left.Count, right.Count);
        for(int i = 0; i < maxLength; i++)
        {
            int leftValue = i < left.Count ? left[i] : 0;
            int rightValue = i < right.Count ? right[i] : 0;
            int comparison = leftValue.CompareTo(rightValue);
            if(comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int ComparePrerelease(string left, string right)
    {
        string[] leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int maxLength = Math.Max(leftParts.Length, rightParts.Length);

        for(int i = 0; i < maxLength; i++)
        {
            if(i >= leftParts.Length)
            {
                return -1;
            }

            if(i >= rightParts.Length)
            {
                return 1;
            }

            string leftPart = leftParts[i];
            string rightPart = rightParts[i];

            bool leftIsNumeric = Int32.TryParse(leftPart, out int leftNumber);
            bool rightIsNumeric = Int32.TryParse(rightPart, out int rightNumber);

            if(leftIsNumeric && rightIsNumeric)
            {
                int numericComparison = leftNumber.CompareTo(rightNumber);
                if(numericComparison != 0)
                {
                    return numericComparison;
                }

                continue;
            }

            if(leftIsNumeric)
            {
                return -1;
            }

            if(rightIsNumeric)
            {
                return 1;
            }

            int stringComparison = String.Compare(leftPart, rightPart, StringComparison.Ordinal);
            if(stringComparison != 0)
            {
                return stringComparison;
            }
        }

        return 0;
    }

    private static bool TryParseSemVer(string value, out SemVerParts? semVer)
    {
        semVer = null;
        if(String.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if(trimmed.StartsWith('v'))
        {
            trimmed = trimmed[1..];
        }

        int metadataSeparator = trimmed.IndexOf('+');
        if(metadataSeparator >= 0)
        {
            trimmed = trimmed[..metadataSeparator];
        }

        string corePart = trimmed;
        string? prerelease = null;
        int prereleaseSeparator = trimmed.IndexOf('-');
        if(prereleaseSeparator >= 0)
        {
            corePart = trimmed[..prereleaseSeparator];
            prerelease = trimmed[(prereleaseSeparator + 1)..];
        }

        string[] coreSegments = corePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if(coreSegments.Length == 0)
        {
            return false;
        }

        var coreNumbers = new List<int>(coreSegments.Length);
        foreach(string segment in coreSegments)
        {
            if(!Int32.TryParse(segment, out int number))
            {
                return false;
            }

            coreNumbers.Add(number);
        }

        semVer = new SemVerParts(coreNumbers, prerelease);
        return true;
    }

    private sealed record SemVerParts(IReadOnlyList<int> Core, string? Prerelease);
}
