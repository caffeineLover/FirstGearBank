using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Frosting;

namespace CakeBuild.Tasks;



[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		context.DotNetClean(
			$"../{context.ProjectName}/{context.ProjectName}.csproj",
			new DotNetCleanSettings
			{
				Configuration = context.BuildConfiguration
			});

		context.DotNetPublish(
			$"../{context.ProjectName}/{context.ProjectName}.csproj",
			new DotNetPublishSettings
			{
				Configuration = context.BuildConfiguration
			});
	}
}