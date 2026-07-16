using System.Text.Json.Serialization;

namespace HrothgarScent.Scent;

/// <summary>
/// The on-disk shape of marks.json.
///
/// An ARRAY, not a dictionary keyed by <see cref="WatcherKey"/>. System.Text.Json only supports string and
/// primitive dictionary keys, and WatcherKey is a record struct with no TypeConverter — a dictionary would
/// fail, and the in-memory index is rebuilt from this array on load anyway.
///
/// Properties, not fields: System.Text.Json skips fields unless told otherwise, and a Version that silently
/// serialised as nothing would defeat the one member whose whole job is to be readable by a future build.
/// </summary>
internal sealed class MarkFileV1
{
  /// <summary>
  /// Schema version of this FILE, independent of <see cref="Configuration.Version"/>.
  ///
  /// Separate on purpose. The config's version tracks the config's own meaning, and tying a second schema to it
  /// would force a config migration every time the store's shape moved, and vice versa. See
  /// <see cref="MarkStore.CurrentFileVersion"/> for what a version from the future does.
  /// </summary>
  public int Version { get; set; }

  public MarkedPlayer[] Players { get; set; } = [];
}

/// <summary>
/// Compile-time serialisation for <see cref="MarkFileV1"/>. Source-generated rather than reflective: it costs
/// nothing at startup, and it needs no package — which keeps the plugin's zero-dependency stance intact. The
/// prior art here reaches for eight NuGet packages to store notes about people.
///
/// IncludeFields is load-bearing and not a style choice: <see cref="System.Numerics.Vector4"/> exposes X/Y/Z/W
/// as public FIELDS, and System.Text.Json ignores fields by default — so without this every colour would
/// round-trip through an empty object and come back as (0,0,0,0), i.e. every custom colour silently becoming
/// transparent black on the first restart. MarkedPlayer and MarkFileV1 declare no public fields of their own,
/// so this reaches nothing else.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(MarkFileV1))]
internal sealed partial class MarkJson : JsonSerializerContext;
