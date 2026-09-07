using System;
using System.IO;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using CakeBuild.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

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



// ReSharper disable once ClassNeverInstantiated.Global
public class BuildContext : FrostingContext
{
	public const string ProjectName = "First_Gear_Bank";
	public string BuildConfiguration { get; }
	public string Version { get; }
	public string Name { get; }
	public bool SkipJsonValidation { get; }

	public BuildContext(ICakeContext context)
		: base(context)
	{
		BuildConfiguration = context.Argument("configuration", "Release");
		SkipJsonValidation = context.Argument("skipJsonValidation", false);
		var modInfo = context.DeserializeJsonFromFile<ModInfo>($"../{ProjectName}/modinfo.json");
		Version = modInfo.Version;
		Name = modInfo.ModID;
	}
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		if (context.SkipJsonValidation)
		{
			return;
		}

		var jsonFiles = context.GetFiles($"../{BuildContext.ProjectName}/assets/**/*.json");
		foreach (var file in jsonFiles)
		{
			try
			{
				var json = File.ReadAllText(file.FullPath);
				JToken.Parse(json);
			}
			catch (JsonException ex)
			{
				throw new Exception(
					$"Validation failed for JSON file: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
			}
		}
	}
}

[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		context.DotNetClean($"../{BuildContext.ProjectName}/{BuildContext.ProjectName}.csproj",
			new DotNetCleanSettings
			{
				Configuration = context.BuildConfiguration
			});


		context.DotNetPublish($"../{BuildContext.ProjectName}/{BuildContext.ProjectName}.csproj",
			new DotNetPublishSettings
			{
				Configuration = context.BuildConfiguration
			});
	}
}




[TaskName("Default")]
[IsDependentOn(typeof(DeployTask))]
public class DefaultTask : FrostingTask
{
}