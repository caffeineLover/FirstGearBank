using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;



[TaskName("PackageMod")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageModTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		var releasesDirectory = "../Releases";
		var modDirectory = $"../Releases/{context.Name}";

		// Start with a clean release area.
		context.EnsureDirectoryExists(releasesDirectory);
		context.CleanDirectory(releasesDirectory);

		// Create the unpacked mod folder.
		context.EnsureDirectoryExists(modDirectory);

		// Copy the published DLL and related output.
		context.CopyFiles(
			$"../{BuildContext.ProjectName}/bin/{context.BuildConfiguration}/Mods/mod/publish/*",
			modDirectory);

		// Copy assets.
		if (context.DirectoryExists($"../{BuildContext.ProjectName}/assets"))
		{
			context.CopyDirectory(
				$"../{BuildContext.ProjectName}/assets",
				$"{modDirectory}/assets");
		}

		// Copy modinfo.json.
		context.CopyFile(
			$"../{BuildContext.ProjectName}/modinfo.json",
			$"{modDirectory}/modinfo.json");

		// Copy mod icon if present.
		if (context.FileExists($"../{BuildContext.ProjectName}/modicon.png"))
		{
			context.CopyFile(
				$"../{BuildContext.ProjectName}/modicon.png",
				$"{modDirectory}/modicon.png");
		}
	}
}
