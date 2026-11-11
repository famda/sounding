using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sounding.Shared;

public static class SoundingJson
{
    public static readonly JsonSerializerOptions Options = SoundingJsonContext.Default.Options;

    // How the agents store samples: without null fields, which readers take as null anyway.
    public static readonly SoundingJsonContext Stored = new(new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HostSnapshot))]
[JsonSerializable(typeof(ContainerDetail))]
[JsonSerializable(typeof(IReadOnlyList<ProcessUsage>))]
[JsonSerializable(typeof(List<ProcessUsage>))]
[JsonSerializable(typeof(NetworkActivity))]
[JsonSerializable(typeof(NetworkStats))]
[JsonSerializable(typeof(GpuStats))]
[JsonSerializable(typeof(IReadOnlyList<DiskVolume>))]
public sealed partial class SoundingJsonContext : JsonSerializerContext;
