using System.Text.Json.Serialization;
using NetSonar.Avalonia.Network;

namespace NetSonar.Avalonia.Settings;

public sealed class SpeedTestsFile : RootCollectionFile<SpeedTestsFile, SpeedTestResult>
{
    [JsonIgnore]
    public override string FileName => "speedtests.json";
}