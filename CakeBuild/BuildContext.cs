using System;
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
	public string Bump { get; }

	public BuildContext(ICakeContext context)
		: base(context)
	{
		BuildConfiguration =
			context.Argument("configuration", "Release");

		SkipJsonValidation =
			context.Argument("skipJsonValidation", false);

		Bump =
			context.Argument("bump", string.Empty)
				.Trim()
				.ToLowerInvariant();

		var modInfo =
			context.DeserializeJsonFromFile<ModInfo>(
				$"../{ProjectName}/modinfo.json");

		Name = modInfo.ModID;
		Version = ApplyVersionBump(modInfo.Version, Bump);
	}

	private static string ApplyVersionBump(string version, string bump)
	{
		if (string.IsNullOrWhiteSpace(bump))
			return version;

		var parts = version.Split('.');

		if (parts.Length != 3 ||
		    !int.TryParse(parts[0], out var x) ||
		    !int.TryParse(parts[1], out var y) ||
		    !int.TryParse(parts[2], out var z))
		{
			throw new InvalidOperationException(
				$"Version '{version}' is not in x.y.z format.");
		}

		return bump switch
		{
			"x" => $"{x + 1}.0.0",
			"y" => $"{x}.{y + 1}.0",
			"z" => $"{x}.{y}.{z + 1}",

			_ => throw new ArgumentException(
				$"Invalid --bump value '{bump}'. Use x, y, z, or omit --bump.")
		};
	}
}

