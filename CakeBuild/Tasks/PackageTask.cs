// Creates the distributable Vintage Story ZIP from the staged mod.  Packaging does not modify the deployed test copy
// and does not clear older release archives from the Releases directory.

using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;

[TaskName("Package")]
[IsDependentOn(typeof(StageModTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		var releasesDirectory = "../Releases";
		var source = $"./bin/staging/{context.Name}";
		var package = $"{releasesDirectory}/{context.Name}_{context.Version}.zip";

		// Preserve existing releases while ensuring a package for the current version can be rebuilt cleanly.
		context.EnsureDirectoryExists(releasesDirectory);

		if (context.FileExists(package))
		{
			context.DeleteFile(package);
		}

		// Zip the staged mod contents directly so modinfo.json, the DLL, assets, and optional icon are at the ZIP root.
		context.Zip(source, package);
	}
}