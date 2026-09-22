/*
 * Loads the server's YAML configuration and creates a documented first-start template at firstgearbank.yaml.
 * The flat dotted keys are ordinary YAML and match Config Lib's optional asset-driven editor without linking its
 * assembly.  A nested mapping using the same dotted path is also accepted for administrators editing the file.
 *
 * Individual invalid fields fall back independently, while incompatible economic combinations retain the previous
 * complete economic configuration.  Values distinguish six-decimal money, real-second cooldowns, and ordinary
 * world-day branch timers.  The server host owns applying a snapshot and scheduling economics at a financial boundary.
 */

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using FirstGearBank.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FirstGearBank.Server;

/// Validated adapter settings accompanying the core's economics and presentation options.
/// Branch intervals use ordinary world days, independent of the selected banking interest clock.
internal sealed record ServerSettings(EconomicSettings Economics, CoreOptions Core, InterestTimeBasis TimeBasis,
    string RecipientMode, double CharterMinimumDays, double CharterMaximumDays, double ReplacementMinimumDays,
    double ReplacementMaximumDays, int MinimumBranchSpacing, double NaturalBranchProbability, bool BackfillTraders);

/// YAML reader with field-specific diagnostics and last-known-good handling for dependent economic settings.
/// Loading creates only the missing first-start file and never rewrites an existing administrator document.
internal sealed class ServerConfiguration
{
    private readonly string path;
    private readonly ServerDiagnostics log;
    private YamlMappingNode root = new();

    private const string Template = """
        # First Gear Bank server configuration.  Save changes, then run /bankadmin reload.
        # Interest follows the world calendar, including sleep; ServerRuntime excludes sleep acceleration.
        InterestTimeBasis: InGame
        RiskFreeRateModel: CIR
        # Annual effective target: 3 means 300%.
        TargetAnnualizedRiskFreeRate: 3
        CIR.MeanReversionSpeed: 1
        CIR.Volatility: 0.12
        # Display only; ledger precision remains six decimals.
        Currency.RustyGearDisplayPrecision: 3
        CD.MinimumRustyGearPrincipal: 0.25
        # Comma-separated financial-month choices.
        CD.TenorsMonths: '1, 3, 6, 12'
        CD.BaseSpread.Scale: 0.30
        CD.BaseSpread.Exponent: 0.395
        CD.StochasticSpread.LevelShock.var: 0.05
        CD.StochasticSpread.SlopeShock.var: 0.04
        CD.BankLiquidity.TargetFundingRatio: 0.25
        CD.BankLiquidity.MaximumScarcityBoost: 0.15
        CD.BankLiquidity.MaximumExcessFundingReduction: 0.10
        CD.BankLiquidity.AdjustmentHalfLifeMonths: 1
        CD.BankLiquidity.ResponseWidth: 0.10
        RecipientSelectionMode: ExactName
        TransferCooldownSeconds: 1
        # Lifecycle intervals always use ordinary calendar days, not banking months.
        Charter.ArrivalMinimumDays: 2
        Charter.ArrivalMaximumDays: 5
        BankerReplacement.MinimumDays: 3
        BankerReplacement.MaximumDays: 7
        MinimumCharterBranchSpacingBlocks: 32
        NaturalBranches.TraderCompanionProbability: 0.15
        NaturalBranches.BackfillExistingTraderLocations: false
        Statements.RecentTransactionsOnPrintedStatement: 10
        """;



    //// Selects the game's YAML modconfig file and borrowed logger; neither dependency is owned by the reader.
    ////
    public ServerConfiguration(string dataDirectory, ServerDiagnostics log)
    {
        path = Path.Combine(dataDirectory, "ModConfig", "firstgearbank.yaml");
        this.log = log;
    }



    //// Reads one bounded YAML mapping and returns a fully validated settings snapshot.
    //// Syntax or I/O failures retain previous settings, or first-start defaults, without rewriting existing bytes.
    ////
    public ServerSettings Load(ServerSettings? previous = null, double? carriedRate = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, Template);
            if (new FileInfo(path).Length > 131_072) throw new InvalidDataException("Configuration is too large.");
            using var reader = File.OpenText(path);
            var document = new YamlStream();
            document.Load(reader);
            if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode mapping)
                throw new YamlException("Configuration root must be one mapping.");
            root = mapping;
            return ReadSettings(previous, carriedRate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            YamlException)
        {
            log.Write("WARN", "configuration",
                $"Cannot load firstgearbank.yaml ({error.GetType().Name}); retained defaults or previous settings.");
            return previous ?? Defaults();
        }
    }



    //// Converts the documented YAML fields to typed settings and validates economic interactions as one unit.
    ////
    private ServerSettings ReadSettings(ServerSettings? previous, double? carriedRate)
    {
        var economics = new EconomicSettings
        {
            Model = Enum.Parse<RateModel>(Choice("RiskFreeRateModel", "CIR", "Constant")),
            TargetAnnualizedRiskFreeRate = Number("TargetAnnualizedRiskFreeRate", 3),
            MeanReversionSpeed = Number("CIR.MeanReversionSpeed", 1, double.Epsilon),
            Volatility = Number("CIR.Volatility", .12, double.Epsilon),
            MinimumCdPrincipalUnits = Principal(),
            TenorsMonths = Tenors(),
            BaseSpreadScale = Number("CD.BaseSpread.Scale", .30),
            BaseSpreadExponent = Number("CD.BaseSpread.Exponent", .395, double.Epsilon, 1),
            LevelVariation = Shock("LevelShock", .05),
            SlopeVariation = Shock("SlopeShock", .04),
            TargetFundingRatio = Number("CD.BankLiquidity.TargetFundingRatio", .25, 0, 1),
            MaximumScarcityBoost = Number("CD.BankLiquidity.MaximumScarcityBoost", .15),
            MaximumExcessFundingReduction = Number("CD.BankLiquidity.MaximumExcessFundingReduction", .10),
            AdjustmentHalfLifeMonths = Number("CD.BankLiquidity.AdjustmentHalfLifeMonths", 1, double.Epsilon),
            ResponseWidth = Number("CD.BankLiquidity.ResponseWidth", .10, double.Epsilon)
        };
        try { economics.Validate(carriedRate); }
        catch (Exception error) when (error is BankException or OverflowException)
        {
            log.Write("WARN", "configuration",
                "Economic combination cannot price supported CDs; retaining last-known-good economics.");
            economics = previous?.Economics ?? new EconomicSettings();
        }

        var charterMin = Number("Charter.ArrivalMinimumDays", 2, double.Epsilon);
        var charterMax = Number("Charter.ArrivalMaximumDays", 5, double.Epsilon);
        if (charterMax < charterMin)
        {
            Invalid("Charter arrival interval");
            charterMin = 2;
            charterMax = 5;
        }
        var replacementMin = Number("BankerReplacement.MinimumDays", 3, double.Epsilon);
        var replacementMax = Number("BankerReplacement.MaximumDays", 7, double.Epsilon);
        if (replacementMax < replacementMin)
        {
            Invalid("BankerReplacement interval");
            replacementMin = 3;
            replacementMax = 7;
        }
        return new(economics, new((int)Number("Currency.RustyGearDisplayPrecision", 3, 0, 6, true),
                (decimal)Number("TransferCooldownSeconds", 1, 0, 86_400),
                (int)Number("Statements.RecentTransactionsOnPrintedStatement", 10, 0, 100, true)),
            Enum.Parse<InterestTimeBasis>(Choice("InterestTimeBasis", "InGame", "ServerRuntime")),
            Choice("RecipientSelectionMode", "ExactName", "KnownPlayerListing"), charterMin, charterMax,
            replacementMin, replacementMax, (int)Number("MinimumCharterBranchSpacingBlocks", 32, 0, int.MaxValue, true),
            Number("NaturalBranches.TraderCompanionProbability", .15, 0, 1),
            Boolean("NaturalBranches.BackfillExistingTraderLocations"));
    }



    //// Returns the first-start configuration when no usable document exists; this performs no file I/O.
    ////
    private static ServerSettings Defaults()
    {
        return new(new(), new(), InterestTimeBasis.InGame, "ExactName", 2, 5, 3, 7, 32, .15, false);
    }



    //// Resolves either Config Lib's flat dotted key or an equivalent nested YAML mapping.
    ////
    private YamlNode? Find(string field)
    {
        if (root.Children.TryGetValue(new YamlScalarNode(field), out var exact)) return exact;
        YamlNode value = root;
        foreach (var part in field.Split('.'))
        {
            if (value is not YamlMappingNode mapping ||
                !mapping.Children.TryGetValue(new YamlScalarNode(part), out value!)) return null;
        }
        return value;
    }



    //// Reads a finite scalar number with its own range and optional integer constraint.
    //// Missing fields use defaults quietly; malformed explicit values receive a field-specific diagnostic.
    ////
    private double Number(string field, double fallback, double minimum = 0, double maximum = 1e12,
        bool integer = false)
    {
        var value = Find(field);
        if (value is null) return fallback;
        if (value is YamlScalarNode scalar && double.TryParse(scalar.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) && result >= minimum &&
            result <= maximum && (!integer || double.IsInteger(result))) return result;
        Invalid(field);
        return fallback;
    }



    //// Accepts only the documented case-sensitive scalar choices, defaulting invalid explicit values independently.
    ////
    private string Choice(string field, string fallback, string? alternative = null)
    {
        var value = Find(field);
        if (value is null) return fallback;
        if (value is YamlScalarNode scalar && (scalar.Value == fallback || scalar.Value == alternative))
            return scalar.Value!;
        Invalid(field);
        return fallback;
    }



    //// Reads an optional false-by-default feature flag without interpreting numbers as booleans.
    ////
    private bool Boolean(string field)
    {
        var value = Find(field);
        if (value is null) return false;
        if (value is YamlScalarNode scalar && bool.TryParse(scalar.Value, out var result)) return result;
        Invalid(field);
        return false;
    }



    //// Validates a sequence or comma-separated tenor menu before pricing loops; duplicates reject the whole menu.
    ////
    private ImmutableArray<int> Tenors()
    {
        var value = Find("CD.TenorsMonths");
        if (value is null) return [1, 3, 6, 12];
        var tokens = value switch
        {
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value),
            YamlScalarNode scalar => scalar.Value?.Split(',', StringSplitOptions.TrimEntries),
            _ => null
        };
        if (tokens is not null)
        {
            var materialized = tokens.ToArray();
            var result = ImmutableArray.CreateBuilder<int>();
            foreach (var token in materialized)
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var tenor) || tenor <= 0)
                    break;
                result.Add(tenor);
            }
            if (result.Count is > 0 and <= 64 && result.Count == materialized.Length &&
                result.Distinct().Count() == result.Count) return result.ToImmutable();
        }
        Invalid("CD.TenorsMonths");
        return [1, 3, 6, 12];
    }



    //// Reads minimum principal as decimal, rejecting excess precision without rounding through binary floats.
    ////
    private long Principal()
    {
        const string field = "CD.MinimumRustyGearPrincipal";
        var value = Find(field);
        if (value is null) return 250_000;
        if (value is YamlScalarNode scalar && decimal.TryParse(scalar.Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var principal) && principal is > 0 and <= int.MaxValue &&
            decimal.Round(principal, 6) == principal) return Money.FromGears(principal).Units;
        Invalid(field);
        return 250_000;
    }



    //// Reads bounded-shock variance; the core fixes both supported shocks to a zero-mean Gaussian distribution.
    ////
    private double Shock(string name, double fallback)
    {
        return Number("CD.StochasticSpread." + name + ".var", fallback);
    }



    //// Reports a schema field, never the administrator's raw value, when using its documented default.
    ////
    private void Invalid(string field)
    {
        log.Write("WARN", "configuration", $"Invalid {field}; using its documented default.");
    }



}
