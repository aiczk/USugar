using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class RealWorldTests
{
    static string FindProjectRoot()
    {
        var dir = System.AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "Packages"))
                && Directory.Exists(Path.Combine(dir, "Assets")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new System.Exception("Unity project root not found");
    }

    static readonly string ProjectRoot = FindProjectRoot();

    static string ReadFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            new[] { ProjectRoot }.Concat(pathParts).ToArray()));

    // --- VRChat SDK Utility Scripts ---

    [Fact]
    public void RealWorld_GlobalToggleObject()
        => TestHelper.CompileToUasm(
            ReadFile("Assets", "UdonSharp", "UtilityScripts", "Synced", "GlobalToggleObject.cs"),
            "GlobalToggleObject");

    [Fact]
    public void RealWorld_MasterToggleObject()
        => TestHelper.CompileToUasm(
            ReadFile("Assets", "UdonSharp", "UtilityScripts", "Synced", "MasterToggleObject.cs"),
            "MasterToggleObject");

    [Fact]
    public void RealWorld_PlayerModSetter()
        => TestHelper.CompileToUasm(
            ReadFile("Assets", "UdonSharp", "UtilityScripts", "PlayerModSetter.cs"),
            "PlayerModSetter");

    [Fact]
    public void RealWorld_WorldAudioSettings()
        => TestHelper.CompileToUasm(
            ReadFile("Assets", "UdonSharp", "UtilityScripts", "WorldAudioSettings.cs"),
            "WorldAudioSettings");

    [Fact]
    public void RealWorld_BoneFollower()
        => TestHelper.CompileToUasm(
            ReadFile("Assets", "UdonSharp", "UtilityScripts", "BoneFollower.cs"),
            "BoneFollower");
}
