using Xunit;

namespace Tamp.Heft.V1.Tests;

public sealed class HeftBinaryResolverTests
{
    private const string Node = "/usr/bin/node";
    private const string GlobalHeft = "/usr/local/bin/heft";

    [Fact]
    public void Installed_Package_Runs_Through_Node_With_The_Bin_Script()
    {
        using var p = new TempProject();
        var script = p.InstallHeft(p.Root, version: "1.3.2");

        var r = HeftBinaryResolver.TryResolve(p.Root, Node, GlobalHeft);

        Assert.NotNull(r);
        Assert.Equal(HeftResolutionSource.ProjectPackage, r!.Source);
        Assert.Equal(Node, r.Executable);
        Assert.Equal(new[] { Path.GetFullPath(script) }, r.PrefixArguments);
        Assert.Equal("1.3.2", r.Version);
    }

    [Fact]
    public void String_Form_Bin_Field_Is_Understood()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root, binJson: "\"./bin/heft\"");
        Assert.Equal(HeftResolutionSource.ProjectPackage, HeftBinaryResolver.TryResolve(p.Root, Node, null)!.Source);
    }

    [Fact]
    public void Hoisted_Install_In_A_Parent_Directory_Is_Found()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root);
        var nested = p.Dir("apps", "web-parts");

        var r = HeftBinaryResolver.TryResolve(nested, Node, null);

        Assert.Equal(HeftResolutionSource.ProjectPackage, r!.Source);
    }

    [Fact]
    public void The_Nearest_Install_Wins()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root, version: "1.0.0");
        var app = p.Dir("app");
        p.InstallHeft(app, version: "1.3.2");

        Assert.Equal("1.3.2", HeftBinaryResolver.TryResolve(app, Node, null)!.Version);
    }

    [Fact]
    public void Without_Node_The_Shim_Is_Used()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root);
        var shim = p.InstallShim(p.Root);

        var r = HeftBinaryResolver.TryResolve(p.Root, node: null, GlobalHeft);

        Assert.Equal(HeftResolutionSource.ProjectShim, r!.Source);
        Assert.Equal(shim, r.Executable);
        Assert.Empty(r.PrefixArguments);
        Assert.Equal("1.3.2", r.Version);
    }

    [Fact]
    public void A_Shim_Without_The_Package_Is_Still_Used_Before_Global()
    {
        using var p = new TempProject();
        p.InstallShim(p.Root);

        Assert.Equal(HeftResolutionSource.ProjectShim, HeftBinaryResolver.TryResolve(p.Root, Node, GlobalHeft)!.Source);
    }

    [Fact]
    public void Global_Heft_Is_The_Last_Resort()
    {
        using var p = new TempProject();
        var r = HeftBinaryResolver.TryResolve(p.Root, Node, GlobalHeft);
        Assert.Equal(HeftResolutionSource.Global, r!.Source);
        Assert.Equal(GlobalHeft, r.Executable);
    }

    [Fact]
    public void Nothing_Installed_Resolves_To_Null()
    {
        using var p = new TempProject();
        Assert.Null(HeftBinaryResolver.TryResolve(p.Root, Node, globalHeft: null));
    }

    [Fact]
    public void A_Package_That_Is_Not_Heft_Is_Ignored()
    {
        using var p = new TempProject();
        var pkg = p.Dir("node_modules", "@rushstack", "heft");
        File.WriteAllText(Path.Combine(pkg, "package.json"), """{ "name": "evil", "bin": "./x.js" }""");
        File.WriteAllText(Path.Combine(pkg, "x.js"), "");

        Assert.Null(HeftBinaryResolver.TryResolve(p.Root, Node, null));
    }

    [Fact]
    public void A_Bin_Entry_Pointing_Outside_The_Package_Is_Refused()
    {
        using var p = new TempProject();
        File.WriteAllText(Path.Combine(p.Root, "outside.js"), "");
        p.InstallHeft(p.Root, binJson: "{ \"heft\": \"../../../outside.js\" }", writeScript: false);

        Assert.Null(HeftBinaryResolver.TryResolve(p.Root, Node, null));
    }

    [Fact]
    public void A_Missing_Bin_Script_Means_Not_Installed()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root, writeScript: false);
        Assert.Null(HeftBinaryResolver.TryResolve(p.Root, Node, null));
    }

    [Fact]
    public void Malformed_Package_Json_Is_Not_Fatal()
    {
        using var p = new TempProject();
        var pkg = p.Dir("node_modules", "@rushstack", "heft");
        File.WriteAllText(Path.Combine(pkg, "package.json"), "{ not json");
        Assert.Null(HeftBinaryResolver.TryResolve(p.Root, Node, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_Working_Directory_Throws(string wd) =>
        Assert.Throws<ArgumentException>(() => HeftBinaryResolver.TryResolve(wd, Node, null));

    [Fact]
    public void Public_Overload_Reads_Path_Without_Throwing()
    {
        using var p = new TempProject();
        p.InstallHeft(p.Root);
        // node may or may not be on the runner's PATH; either way the package is found one way or the other
        // only if node exists, so just exercise the public path.
        _ = HeftBinaryResolver.TryResolve(p.Root);
        _ = HeftBinaryResolver.IsAvailable(p.Root);
    }

    [Fact]
    public void FindOnPath_Returns_Null_For_A_Name_That_Does_Not_Exist() =>
        Assert.Null(HeftBinaryResolver.FindOnPath("definitely-not-a-real-tool-" + Guid.NewGuid().ToString("N")));
}
