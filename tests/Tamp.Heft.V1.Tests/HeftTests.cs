using Tamp;
using Xunit;

namespace Tamp.Heft.V1.Tests;

public sealed class HeftTests
{
    private static readonly HeftBinaryResolution Node = new()
    {
        Executable = "/usr/bin/node",
        PrefixArguments = new[] { "/w/node_modules/@rushstack/heft/bin/heft" },
        Source = HeftResolutionSource.Explicit,
    };

    // The argv after the bin script.
    private static string[] Heft(CommandPlan plan) => plan.Arguments.Skip(1).ToArray();

    [Fact]
    public void Build_Runs_Node_With_The_Bin_Script_First()
    {
        var plan = Tamp.Heft.V1.Heft.Build(s => s.SetBinary(Node).SetWorkingDirectory("/w"));

        Assert.Equal("/usr/bin/node", plan.Executable);
        Assert.Equal("/w/node_modules/@rushstack/heft/bin/heft", plan.Arguments[0]);
        Assert.Equal(new[] { "build" }, Heft(plan));
        Assert.Equal("/w", plan.WorkingDirectory);
        Assert.Equal(CapabilityTier.Safe, plan.RequiredCapability);
    }

    [Fact]
    public void Build_Production_Clean_Verbose_Locales()
    {
        var plan = Tamp.Heft.V1.Heft.Build(s => s.SetBinary(Node).SetVerbose().SetProduction().SetClean().AddLocale("en-us").AddLocale("de-de"));
        Assert.Equal(new[] { "build", "-v", "--production", "--clean", "--locales", "en-us", "--locales", "de-de" }, Heft(plan));
    }

    [Fact]
    public void Global_Flags_Precede_The_Command()
    {
        var plan = Tamp.Heft.V1.Heft.Build(s => s.SetBinary(Node).SetDebug().SetUnmanaged().SetProduction());
        Assert.Equal(new[] { "--debug", "--unmanaged", "build", "--production" }, Heft(plan));
    }

    [Fact]
    public void Extra_Arguments_Come_Last()
    {
        var plan = Tamp.Heft.V1.Heft.Build(s => s.SetBinary(Node).AddArgument("--emit-stats").SetProduction());
        Assert.Equal(new[] { "build", "--production", "--emit-stats" }, Heft(plan));
    }

    [Fact]
    public void Environment_And_Node_Options_Flow_Through()
    {
        var plan = Tamp.Heft.V1.Heft.Build(s => s.SetBinary(Node).SetNodeOptions("--max-old-space-size=8192").SetEnvironmentVariable("CI", "true"));
        Assert.Equal("--max-old-space-size=8192", plan.Environment["NODE_OPTIONS"]);
        Assert.Equal("true", plan.Environment["CI"]);
    }

    [Fact]
    public void Test_Renders_Every_Jest_Flag()
    {
        var plan = Tamp.Heft.V1.Heft.Test(s => s
            .SetBinary(Node)
            .SetProduction()
            .SetJestConfig("config/jest.ci.json")
            .SetDisableCodeCoverage()
            .SetDetectOpenHandles()
            .AddFindRelatedTests("src/a.ts")
            .SetLogHeapUsage()
            .SetMaxWorkers("50%")
            .SetSilent()
            .SetTestNamePattern("adds")
            .SetTestPathPattern("webparts/")
            .AddTestPathIgnorePattern("/fixtures/")
            .SetTestTimeoutMs(10000)
            .SetUpdateSnapshots());

        Assert.Equal(new[]
        {
            "test", "--production",
            "--config", "config/jest.ci.json",
            "--disable-code-coverage",
            "--detect-open-handles",
            "--find-related-tests", "src/a.ts",
            "--log-heap-usage",
            "--max-workers", "50%",
            "--silent",
            "--test-name-pattern", "adds",
            "--test-path-pattern", "webparts/",
            "--test-path-ignore-patterns", "/fixtures/",
            "--test-timeout-ms", "10000",
            "--update-snapshots",
        }, Heft(plan));
    }

    [Fact]
    public void Test_Defaults_Are_Just_The_Command()
    {
        Assert.Equal(new[] { "test" }, Heft(Tamp.Heft.V1.Heft.Test(s => s.SetBinary(Node))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Test_Rejects_A_Non_Positive_Timeout(int ms) =>
        Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Test(s => s.SetBinary(Node).SetTestTimeoutMs(ms)));

    [Fact]
    public void Clean_With_Scoping()
    {
        var plan = Tamp.Heft.V1.Heft.Clean(s => s.SetBinary(Node).AddTo("build").AddOnly("test").AddToExcept("lint").SetVerbose());
        Assert.Equal(new[] { "clean", "--to", "build", "--to-except", "lint", "--only", "test", "-v" }, Heft(plan));
    }

    [Fact]
    public void Run_Puts_Scoped_Arguments_After_The_Separator()
    {
        var plan = Tamp.Heft.V1.Heft.Run(s => s.SetBinary(Node).AddTo("test").AddScopedArgument("--production"));
        Assert.Equal(new[] { "run", "--to", "test", "--", "--production" }, Heft(plan));
    }

    [Fact]
    public void Run_Without_Scoped_Arguments_Has_No_Separator()
    {
        var plan = Tamp.Heft.V1.Heft.Run(s => s.SetBinary(Node).AddOnly("build"));
        Assert.Equal(new[] { "run", "--only", "build" }, Heft(plan));
    }

    [Fact]
    public void Run_Requires_A_Phase_Selection() =>
        Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Run(s => s.SetBinary(Node)));

    [Fact]
    public void Phase_Runs_A_Rig_Defined_Action()
    {
        var plan = Tamp.Heft.V1.Heft.Phase("package-solution", s => s.SetBinary(Node).SetProduction().AddArgument("--pretty-print-xml"));
        Assert.Equal(new[] { "package-solution", "--production", "--pretty-print-xml" }, Heft(plan));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("build-watch")]
    [InlineData("package-solution-watch")]
    public void Watch_Actions_Are_Refused(string action)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Phase(action, s => s.SetBinary(Node)));
        Assert.Contains("watch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--production")]
    [InlineData("Build")]
    [InlineData("build; rm -rf /")]
    public void Malformed_Action_Names_Are_Refused(string action) =>
        Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Phase(action, s => s.SetBinary(Node)));

    [Fact]
    public void Object_Init_Overloads_Match_The_Fluent_Ones()
    {
        var fluent = Tamp.Heft.V1.Heft.Test(s => s.SetBinary(Node).SetProduction().SetSilent());
        var init = Tamp.Heft.V1.Heft.Test(new HeftTestSettings { Binary = Node, Production = true, Silent = true });
        Assert.Equal(fluent.Arguments, init.Arguments);

        Assert.Equal(new[] { "build" }, Heft(Tamp.Heft.V1.Heft.Build(new HeftBuildSettings { Binary = Node })));
        Assert.Equal(new[] { "clean" }, Heft(Tamp.Heft.V1.Heft.Clean(new HeftCleanSettings { Binary = Node })));
        var run = new HeftRunSettings { Binary = Node };
        run.Only.Add("build");
        Assert.Equal(new[] { "run", "--only", "build" }, Heft(Tamp.Heft.V1.Heft.Run(run)));
    }

    [Fact]
    public void Null_Configure_And_Settings_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Tamp.Heft.V1.Heft.Build((Action<HeftBuildSettings>)null!));
        Assert.Throws<ArgumentNullException>(() => Tamp.Heft.V1.Heft.Test((HeftTestSettings)null!));
        Assert.Throws<ArgumentNullException>(() => Tamp.Heft.V1.Heft.Raw("/w", null!));
    }

    [Fact]
    public void Null_Extra_Argument_Is_Refused()
    {
        var s = new HeftBuildSettings { Binary = Node };
        s.ExtraArguments.Add(null!);
        Assert.Throws<InvalidOperationException>(() => s.ToCommandPlan());
    }

    [Fact]
    public void Missing_Heft_Gives_An_Actionable_Error()
    {
        using var p = new TempProject();
        var ex = Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Build(s => s.SetWorkingDirectory(p.Root)));
        Assert.Contains("npm ci", ex.Message);
    }

    [Fact]
    public void Raw_Passes_Arguments_Verbatim_With_Resolution()
    {
        using var p = new TempProject();
        var shim = p.InstallShim(p.Root);
        var plan = Tamp.Heft.V1.Heft.Raw(p.Root, "build-watch", "--serve");
        Assert.Equal(shim, plan.Executable);
        Assert.Equal(new[] { "build-watch", "--serve" }, plan.Arguments);
    }

    [Fact]
    public void Raw_Without_Heft_Throws()
    {
        using var p = new TempProject();
        Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Raw(p.Root, "build"));
    }
}
