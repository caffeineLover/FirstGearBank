using System;
using System.IO;
using System.Linq;
using Cake.Common;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Vintagestory.API.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CakeBuild;

// ReSharper disable once ClassNeverInstantiated.Global
public class BuildContext : FrostingContext
{
    public string ProjectDirectory { get; }
    public string ProjectName { get; }

    public string BuildConfiguration { get; }
    public string Version { get; }
    public string Name { get; }
    public bool SkipJsonValidation { get; }
    public string Bump { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        BuildConfiguration = context.Argument("configuration", "Release");

        SkipJsonValidation = context.Argument("skipJsonValidation", false);

        Bump = context.Argument("bump", string.Empty)
            .Trim()
            .ToLowerInvariant();

        var cakeBuildDirectory = context.Environment.WorkingDirectory.FullPath;

        var repositoryRoot = Directory.GetParent(cakeBuildDirectory)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine the repository root from '{cakeBuildDirectory}'.");

        ProjectDirectory = FindProjectDirectory(repositoryRoot);
        ProjectName = Path.GetFileName(ProjectDirectory);
        
        var modInfoPath = Path.Combine(ProjectDirectory!, "modinfo.json");

        var modInfo = context.DeserializeJsonFromFile<ModInfo>(
	        modInfoPath);

        Name = modInfo.ModID;
        Version = ApplyVersionBump(modInfo.Version, Bump);

        if (!string.IsNullOrWhiteSpace(Bump))
        {
	        var json = JObject.Parse(File.ReadAllText(modInfoPath));

	        json["version"] = Version;
	        
	        var jsonText = json.ToString(Formatting.Indented).Replace("\r\n", "\n");
	        File.WriteAllText( modInfoPath, jsonText + "\n");


	        Console.WriteLine();
	        Console.WriteLine($"The new version is: {Version}");
	        Console.WriteLine();
        }
  
        if (!string.IsNullOrWhiteSpace(Bump))
	        Console.WriteLine($"New version is: {Version}");
    }

    private static string FindProjectDirectory(string repositoryRoot)
    {
        var projectDirectories = Directory.EnumerateDirectories(repositoryRoot)
            .Where(directory =>
                File.Exists(Path.Combine(directory, "modinfo.json")))
            .ToArray();

        return projectDirectories.Length switch
        {
            1 => projectDirectories[0],

            0 => throw new InvalidOperationException(
                $"Could not find a mod project containing modinfo.json under '{repositoryRoot}'."),

            _ => throw new InvalidOperationException(
                $"Found multiple mod projects containing modinfo.json under '{repositoryRoot}'.")
        };
    }

    private static string ApplyVersionBump(string version, string bump)
    {
        if (string.IsNullOrWhiteSpace(bump))
            return version;

        var parts = version.Split('.');

        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var x) ||
            !int.TryParse(parts[1], out var y) ||
            !int.TryParse(parts[2], out var z))
        {
            throw new InvalidOperationException(
                $"Version '{version}' is not in x.y.z format.");
        }

        return bump switch
        {
            "x" => $"{x + 1}.0.0",
            "y" => $"{x}.{y + 1}.0",
            "z" => $"{x}.{y}.{z + 1}",

            _ => throw new ArgumentException(
                $"Invalid --bump value '{bump}'. Use x, y, z, or omit --bump.")
        };
    }
}

