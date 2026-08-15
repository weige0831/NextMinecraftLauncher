using NML.Core.Modloaders;

namespace NML.Core.Tests;

/// <summary>
/// Verifies <see cref="OptiFineInstaller.ParseBmclVersions"/> against the REAL BMCLAPI
/// /optifine/{mc} response shape (verified live 2026-08):
/// <c>[{ "mcversion":"1.12.2", "patch":"C5", "type":"HD_U", "filename":"OptiFine_1.12.2_HD_U_C5.jar" }]</c>
/// — where <c>patch</c> is the short version, <c>type</c> is the product line, and
/// <c>filename</c> is the exact installer jar name used for the download URL.
/// </summary>
public class OptiFineInstallerTests
{
    private const string SampleJson = """
    [
      {"mcversion":"1.12.2","patch":"C5","type":"HD_U","filename":"OptiFine_1.12.2_HD_U_C5.jar"},
      {"mcversion":"1.12.2","patch":"C4","type":"HD_U","filename":"OptiFine_1.12.2_HD_U_C4.jar","forge":"Forge N/A"}
    ]
    """;

    [Fact]
    public void Parses_Version_List_With_Real_BMCLAPI_Shape()
    {
        var versions = OptiFineInstaller.ParseBmclVersions(SampleJson, "1.12.2");
        versions.Should().HaveCount(2);
        versions[0].GameVersion.Should().Be("1.12.2");
        versions[0].Type.Should().Be("C5", "patch is the short version used in profile ids");
        versions[0].Patch.Should().Be("HD_U", "type is the product line");
        versions[0].FileName.Should().Be("OptiFine_1.12.2_HD_U_C5.jar");
        versions[0].Display.Should().Contain("C5").And.Contain("HD_U");
        versions[1].Type.Should().Be("C4");
    }

    [Fact]
    public void Skips_Entries_With_Empty_Patch()
    {
        // patch is the distinguishing field — entries without it are unusable.
        string json = """
        [
          {"mcversion":"1.10.2","patch":"","type":"HD_U","filename":"x.jar"},
          {"mcversion":"1.8","patch":"E1","type":"HD_U","filename":"OptiFine_1.8_HD_U_E1.jar"}
        ]
        """;
        var versions = OptiFineInstaller.ParseBmclVersions(json, "1.8");
        versions.Should().HaveCount(1);
        versions[0].Type.Should().Be("E1");
    }

    [Fact]
    public void Missing_Filename_Field_Defaults_Empty()
    {
        string json = """[{"mcversion":"1.11.2","patch":"L1","type":"HD_U"}]""";
        var v = OptiFineInstaller.ParseBmclVersions(json, "1.11.2").Single();
        v.Type.Should().Be("L1");
        v.FileName.Should().BeEmpty("filename absent from the entry");
    }

    [Fact]
    public void Returns_Empty_On_Malformed_Json()
    {
        OptiFineInstaller.ParseBmclVersions("not json", "1.20.1").Should().BeEmpty();
        OptiFineInstaller.ParseBmclVersions("", "1.20.1").Should().BeEmpty();
    }

    [Fact]
    public void Returns_Empty_On_Empty_Array()
        => OptiFineInstaller.ParseBmclVersions("[]", "1.20.1").Should().BeEmpty();
}
