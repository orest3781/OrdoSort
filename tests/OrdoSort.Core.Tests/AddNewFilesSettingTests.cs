using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>add_new_files_to_session: on unless config.json says otherwise,
/// so every existing config keeps today's behaviour.</summary>
public class AddNewFilesSettingTests
{
    [Fact]
    public void ItIsOnByDefault() => Assert.True(new Config().AddNewFilesToSession);

    [Fact]
    public void AConfigWithoutTheKeyKeepsNewFilesJoining() =>
        Assert.True(JsonSerializer.Deserialize<Config>("""{"inbox":"C:/in"}""")!.AddNewFilesToSession);

    [Fact]
    public void TheKeyTurnsItOff() =>
        Assert.False(JsonSerializer.Deserialize<Config>("""{"add_new_files_to_session":false}""")!.AddNewFilesToSession);
}
