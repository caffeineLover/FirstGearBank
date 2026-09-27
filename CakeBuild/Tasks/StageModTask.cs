// Assembles the complete unpacked VS mod in a temporary staging directory.  Both deployment and release packaging
// consume this staged directory so that they operate on exactly the same set of files.



using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;

[TaskName("StageMod")]
[IsDependentOn(typeof(BuildTask))]
public sealed class StageModTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		// Staging is a transient build artifact rather than a release artifact.  Keeping it under CakeBuild/bin
		// prevents local test builds from modifying the Releases directory.
		var modDirectory = $"./bin/staging/{context.Name}";

		// Always rebuild the staged mod from scratch.  Otherwise, files removed from the source tree could remain in
		// staging and accidentally appear in a deployment or release ZIP.
		context.EnsureDirectoryExists(modDirectory);
		context.CleanDirectory(modDirectory);

		// The VS project publishes the compiled assembly and any associated runtime files into this directory.  These
		// files belong at the root of the finished mod.
		context.CopyFiles(
			$"../{BuildContext.ProjectName}/bin/{context.BuildConfiguration}/Mods/mod/publish/*",
			modDirectory);

		// To the build system, assets are optional, so mods without an assets folder still use the same staging task.
		if (context.DirectoryExists(
			    $"../{BuildContext.ProjectName}/assets"))
		{
			context.CopyDirectory(
				$"../{BuildContext.ProjectName}/assets",
				$"{modDirectory}/assets");
		}

		// modinfo.json is required by Vintage Story to identify and load the mod.
		context.CopyFile(
			$"../{BuildContext.ProjectName}/modinfo.json",
			$"{modDirectory}/modinfo.json");

		// Preserve the optional mod icon at the mod root for Vintage Story and distribution tooling.
		if (context.FileExists(
			    $"../{BuildContext.ProjectName}/modicon.png"))
		{
			context.CopyFile(
				$"../{BuildContext.ProjectName}/modicon.png",
				$"{modDirectory}/modicon.png");
		}
	}
}