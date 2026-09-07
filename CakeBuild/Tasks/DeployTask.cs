// As part of the build process, we want to recursively copy the built mod folder and all its contents into
// an actual Vintage Story "Mods/" folder so we can test play this mod.  But before we do that, we must
// delete the old mod.

using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;



[TaskName("Deploy")]
[IsDependentOn(typeof(PackageTask))]
public sealed class DeployTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		var source = context.Directory(
			$"../Releases/{context.Name}");

		var destination = context.Directory(
			$@"C:\Users\p\AppData\Roaming\StoryForge\installations\working_test_world\Mods\{context.Name}");

		// Delete only the previously deployed copy of this mod.
		context.EnsureDirectoryDoesNotExist(
			destination,
			new DeleteDirectorySettings
			{
				Recursive = true,
				Force = true
			});

		context.CopyDirectory(source, destination);

		context.Information(
			$"Deployed {context.Name} to {destination}");
	}
}