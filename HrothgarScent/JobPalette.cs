using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;

namespace HrothgarScent;

public enum JobRole
{
  Other,
  Tank,
  Healer,
  MeleeDps,
  RangedDps,
}

/// <summary>One job as offered by the per-job colour grid.</summary>
public readonly record struct JobInfo(uint JobId, string Abbreviation, JobRole Role);

/// <summary>
/// Maps a ClassJob row id to a role bucket and a colour, with no Excel lookups in Draw.
/// </summary>
public static class JobPalette
{
  /// <summary>
  /// Table-driven rather than read from the ClassJob sheet's Role byte, because that byte does not split
  /// melee from ranged DPS — which is the split users actually colour by.
  ///
  /// The row ids are well known but were not verified against the live sheet in this pass. That is safe by
  /// construction: <see cref="RoleOf"/> falls back to <see cref="JobRole.Other"/> for anything it does not
  /// recognise, so a wrong id is a wrong colour and never a crash. BLU is Other on purpose — it is a limited
  /// job and does not sit in a party role bucket.
  /// </summary>
  private static readonly JobInfo[] JobTable =
  [
    new(19, "PLD", JobRole.Tank),
    new(21, "WAR", JobRole.Tank),
    new(32, "DRK", JobRole.Tank),
    new(37, "GNB", JobRole.Tank),

    new(24, "WHM", JobRole.Healer),
    new(28, "SCH", JobRole.Healer),
    new(33, "AST", JobRole.Healer),
    new(40, "SGE", JobRole.Healer),

    new(20, "MNK", JobRole.MeleeDps),
    new(22, "DRG", JobRole.MeleeDps),
    new(30, "NIN", JobRole.MeleeDps),
    new(34, "SAM", JobRole.MeleeDps),
    new(39, "RPR", JobRole.MeleeDps),
    new(41, "VPR", JobRole.MeleeDps),

    new(23, "BRD", JobRole.RangedDps),
    new(31, "MCH", JobRole.RangedDps),
    new(38, "DNC", JobRole.RangedDps),
    new(25, "BLM", JobRole.RangedDps),
    new(27, "SMN", JobRole.RangedDps),
    new(35, "RDM", JobRole.RangedDps),
    new(42, "PCT", JobRole.RangedDps),

    new(36, "BLU", JobRole.Other),
  ];

  private static readonly FrozenDictionary<uint, JobRole> RolesById =
    JobTable.ToFrozenDictionary(job => job.JobId, job => job.Role);

  /// <summary>
  /// Every job the per-job colour grid offers, in role order. Exposed so the config window can build that
  /// grid without reaching for the ClassJob sheet from Draw.
  /// </summary>
  public static IReadOnlyList<JobInfo> Jobs => JobTable;

  /// <summary>Role for a ClassJob row id. Base classes, limited jobs and id 0 all land in Other.</summary>
  public static JobRole RoleOf(uint jobId)
    => RolesById.TryGetValue(jobId, out var role) ? role : JobRole.Other;

  /// <summary>The configured colour for a role bucket.</summary>
  public static Vector4 RoleColor(JobRole role)
  {
    var config = Plugin.Configuration;
    return role switch
    {
      JobRole.Tank => config.RoleColorTank,
      JobRole.Healer => config.RoleColorHealer,
      JobRole.MeleeDps => config.RoleColorMelee,
      JobRole.RangedDps => config.RoleColorRanged,
      _ => config.RoleColorOther,
    };
  }

  /// <summary>
  /// Resolved row colour honouring the configured mode. A per-job override the user never set falls back to
  /// the role colour rather than to a hardcoded default, so switching to per-job mode changes nothing until
  /// they actually pick a colour.
  /// </summary>
  public static Vector4 JobColor(uint jobId)
  {
    var config = Plugin.Configuration;
    if (config.JobColorMode == JobColorMode.Job && config.JobColors.TryGetValue(jobId, out var custom))
      return custom;

    return RoleColor(RoleOf(jobId));
  }
}
