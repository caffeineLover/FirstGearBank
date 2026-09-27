// Deploys the staged mod to the local Vintage Story test installation.  The previously deployed copy is removed first
// so deleted or renamed files cannot survive from an earlier build.

using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;



[TaskName("Deploy")]
[IsDependentOn(typeof(StageModTask))]
public sealed class DeployTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		// Deployment consumes the same staged mod that release packaging uses, ensuring local testing matches the
		// contents that will ultimately be distributed.
		var source = context.Directory(
			$"./bin/staging/{context.Name}");

		var destination = context.Directory(
			$@"C:\Users\p\AppData\Roaming\StoryForge\installations\working_test_world\Mods\{context.Name}");

		// Remove only the previously deployed copy of this mod so stale files cannot remain after source changes.
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