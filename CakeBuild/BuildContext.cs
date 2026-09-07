using Cake.Common;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Vintagestory.API.Common;

namespace CakeBuild;



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
		BuildConfiguration =
			context.Argument("configuration", "Release");

		SkipJsonValidation =
			context.Argument("skipJsonValidation", false);

		var modInfo =
			context.DeserializeJsonFromFile<ModInfo>(
				$"../{ProjectName}/modinfo.json");

		Version = modInfo.Version;
		Name = modInfo.ModID;
	}
}
