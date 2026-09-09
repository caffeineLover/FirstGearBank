/*
 * Loads the specification's JSONC configuration without rewriting an administrator's comments or formatting.
 * Missing files receive a documented template; existing files are read with comments and trailing commas enabled.
 * Individual invalid fields fall back independently, while incompatible economic combinations retain the previous
 * complete economic configuration.  Core validation remains the final authority for representable CD payoffs.
 *
 * The returned immutable settings separate six-decimal monetary units, real-second request cooldowns, and ordinary
 * world-day branch timers.  Applying settings and queuing economics at a financial boundary belong to the server host.
 * Optional configuration UIs can call that same reload operation; no optional mod is a required assembly dependency.
 */

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using FirstGearBank.Core;

namespace FirstGearBank.Server;

/// Validated adapter settings accompanying the core's economics and presentation options.
/// Branch intervals use ordinary world days, independent of the selected banking interest clock.
internal sealed record ServerSettings(EconomicSettings Economics, CoreOptions Core, InterestTimeBasis TimeBasis,
    string RecipientMode, double CharterMinimumDays, double CharterMaximumDays, double ReplacementMinimumDays,
    double ReplacementMaximumDays, int MinimumBranchSpacing, double NaturalBranchProbability, bool BackfillTraders);

/// JSONC reader with field-specific diagnostics and last-known-good handling for dependent economic settings.
/// Loading never edits an existing configuration, including an invalid or legacy file.
internal sealed class ServerConfiguration
{
    private readonly string path;
    private readonly ServerDiagnostics log;
    private JsonElement root;

    private const string Template = """
        {
          // Interest follows the world calendar, including sleep; ServerRuntime excludes sleep acceleration.
          "InterestTimeBasis": "InGame",
          "RiskFreeRateModel": "CIR", // CIR or Constant; annual effective target 3 means 300%.
          "TargetAnnualizedRiskFreeRate": 3,
          "CIR": { "MeanReversionSpeed": 1, "Volatility": 0.12 },
          "Currency": { "RustyGearDisplayPrecision": 3 }, // Display only; ledger precision remains six decimals.
          "CD": {
            "MinimumRustyGearPrincipal": 0.25,
            "TenorsMonths": [1, 3, 6, 12],
            "BaseSpread": { "Scale": 0.30, "Exponent": 0.395 },
            "StochasticSpread": {
              "LevelShock": { "dist": "gaussian", "avg": 0, "var": 0.05 },
              "SlopeShock": { "dist": "gaussian", "avg": 0, "var": 0.04 }
            },
            "BankLiquidity": {
              "TargetFundingRatio": 0.25,
              "MaximumScarcityBoost": 0.15,
              "MaximumExcessFundingReduction": 0.10,
              "AdjustmentHalfLifeMonths": 1,
              "ResponseWidth": 0.10
            }
          },
          "RecipientSelectionMode": "ExactName", // Or KnownPlayerListing; never includes unobserved player data.
          "TransferCooldownSeconds": 1,
          // These lifecycle intervals always use ordinary calendar days, not banking months.
          "Charter": { "ArrivalMinimumDays": 2, "ArrivalMaximumDays": 5 },
          "BankerReplacement": { "MinimumDays": 3, "MaximumDays": 7 },
          "MinimumCharterBranchSpacingBlocks": 32,
          "NaturalBranches": { "TraderCompanionProbability": 0.15, "BackfillExistingTraderLocations": false },
          "Statements": { "RecentTransactionsOnPrintedStatement": 10 }
        }
        """;



    //// Selects the game's modconfig file and borrowed logger; neither dependency is owned by the reader.
    ////
    public ServerConfiguration(string dataDirectory, ServerDiagnostics log)
    {
        path = Path.Combine(dataDirectory, "ModConfig", "firstgearbank.jsonc");
        this.log = log;
    }



    //// Reads one bounded document and returns a fully validated settings snapshot.
    //// Syntax or I/O failures retain previous settings, or first-start defaults, without rewriting existing bytes.
    ////
    public ServerSettings Load(ServerSettings? previous = null, double? carriedRate = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, Template);
            if (new FileInfo(path).Length > 131_072) throw new JsonException();
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            root = document.RootElement;
            return ReadSettings(previous, carriedRate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write("WARN", "configuration", $"Cannot load firstgearbank.jsonc ({error.GetType().Name}); retained defaults or previous settings.");
            return previous ?? Defaults();
        }
    }



    //// Converts the specification's nested fields to typed settings and validates economic interactions as one unit.
    //// A legacy spread is considered only when its replacement is absent; obsolete refresh cadence is never applied.
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
            log.Write("WARN", "configuration", "Economic combination cannot price supported CDs; retaining last-known-good economics.");
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
        if (Find("StochasticCDSpread.RefreshMonths").ValueKind != JsonValueKind.Undefined)
            log.Write("WARN", "configuration", "StochasticCDSpread.RefreshMonths is obsolete and ignored.");
        // Retain old JSONC bytes and comments, but retire the approval gate now that checkpoint scans are approved.
        if (Find("AllowExactLiquidityScans").ValueKind != JsonValueKind.Undefined)
            log.Write("WARN", "configuration", "AllowExactLiquidityScans is obsolete and ignored; liquidity selection is automatic.");
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



    //// Resolves a dotted schema path without accepting scalar values as nested configuration objects.
    ////
    private JsonElement Find(string field)
    {
        var value = root;
        foreach (var part in field.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return default;
        return value;
    }



    //// Reads a finite numeric field with its own range and optional integer constraint.
    //// Missing fields use defaults quietly; malformed explicit values receive a field-specific diagnostic.
    ////
    private double Number(string field, double fallback, double minimum = 0, double maximum = 1e12,
        bool integer = false)
    {
        var value = Find(field);
        if (value.ValueKind == JsonValueKind.Undefined) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) &&
            double.IsFinite(result) && result >= minimum && result <= maximum &&
            (!integer || Math.Truncate(result) == result)) return result;
        Invalid(field);
        return fallback;
    }



    //// Accepts only the documented case-sensitive choices, defaulting invalid explicit strings independently.
    ////
    private string Choice(string field, string fallback, string? alternative = null)
    {
        var value = Find(field);
        if (value.ValueKind == JsonValueKind.Undefined) return fallback;
        if (value.ValueKind == JsonValueKind.String &&
            (value.GetString() == fallback || value.GetString() == alternative)) return value.GetString()!;
        Invalid(field);
        return fallback;
    }



    //// Reads an optional false-by-default feature flag without interpreting strings or numbers as booleans.
    ////
    private bool Boolean(string field)
    {
        var value = Find(field);
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind is not (JsonValueKind.False or JsonValueKind.Undefined)) Invalid(field);
        return false;
    }



    //// Validates the bounded tenor menu before any pricing loops; duplicates and nonpositive months reject the menu.
    ////
    private ImmutableArray<int> Tenors()
    {
        var value = Find("CD.TenorsMonths");
        if (value.ValueKind == JsonValueKind.Undefined) return [1, 3, 6, 12];
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 64)
        {
            var result = ImmutableArray.CreateBuilder<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var tenor) || tenor <= 0)
                    break;
                result.Add(tenor);
            }
            if (result.Count == value.GetArrayLength() && result.Distinct().Count() == result.Count)
                return result.ToImmutable();
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
        if (value.ValueKind == JsonValueKind.Undefined) return 250_000;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var principal) &&
            principal is > 0 and <= int.MaxValue && decimal.Round(principal, 6) == principal)
            return Money.FromGears(principal).Units;
        Invalid(field);
        return 250_000;
    }



    //// Reads bounded-shock settings, accepting only the fixed distribution and zero mean supported by the core.
    //// Legacy fields are read in memory only; administrator comments and obsolete keys remain untouched on disk.
    ////
    private double Shock(string name, double fallback)
    {
        var prefix = "CD.StochasticSpread." + name;
        if (Find("CD.StochasticSpread").ValueKind == JsonValueKind.Undefined &&
            Find("StochasticCDSpread").ValueKind != JsonValueKind.Undefined)
            prefix = "StochasticCDSpread." + name;
        Choice(prefix + ".dist", "gaussian");
        Number(prefix + ".avg", 0, 0, 0);
        return Number(prefix + ".var", fallback);
    }



    //// Reports a schema field, never the administrator's raw value, when using its documented default.
    ////
    private void Invalid(string field)
    {
        log.Write("WARN", "configuration", $"Invalid {field}; using its documented default.");
    }



}
