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
[IsDependentOn(typeof(BuildTask))]
public class DefaultTask : FrostingTask
{
}