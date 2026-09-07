using Cake.Frosting;
using CakeBuild.Tasks;

namespace CakeBuild;

public static class Program
{
	public static int Main(string[] args)
	{
		return new CakeHost()
			.UseContext<BuildContext>()
			.Run(args);
	}
}



[TaskName("Default")]
[IsDependentOn(typeof(DeployTask))]
public class DefaultTask : FrostingTask
{
}