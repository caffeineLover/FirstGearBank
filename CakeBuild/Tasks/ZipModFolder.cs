using Cake.Common.IO;
using Cake.Frosting;

namespace CakeBuild.Tasks;



[IsDependentOn(typeof(PackageModTask))]
public sealed class ZipModFolderTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		context.Zip(
			$"../Releases/{context.Name}",
			$"../Releases/{context.Name}_{context.Version}.zip");
	}
}