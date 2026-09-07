using System;
using System.IO;
using Cake.Common.IO;
using Cake.Frosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CakeBuild.Tasks;


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