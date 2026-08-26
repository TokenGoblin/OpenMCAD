using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Api;
using OpenMCAD.App.Plugins;

using Xunit;

namespace OpenMCAD.App.Tests;

/// <summary>
/// The plugin loader (P2-T15, ADR-0012), against real assemblies.
/// </summary>
/// <remarks>
/// Every fixture here is a separately compiled assembly rather than a type declared in this file.
/// That is the whole point: a stub defined alongside the test shares the host's types by
/// construction, so it would pass whether or not the loader's shared-contract handling worked at
/// all — which is the one thing most likely to be wrong.
/// </remarks>
public sealed class PluginLoaderTests
{
    private static string FixturePath(string assemblyName)
    {
        // The fixtures build alongside the test binaries, under the shared artifacts root.
        string here = Path.GetDirectoryName(typeof(PluginLoaderTests).Assembly.Location)!;
        string configuration = new DirectoryInfo(here).Name;
        string binRoot = new DirectoryInfo(here).Parent!.Parent!.FullName;

        return Path.Combine(binRoot, assemblyName, configuration, assemblyName + ".dll");
    }

    private static TestHost Host() => new();

    [Fact]
    public void TheFixturesExistWhereTheTestsExpectThem()
    {
        // A path that has silently gone wrong would make every rejection test below pass for the
        // wrong reason -- "not an assembly" is indistinguishable from "no such file" unless this
        // is checked first.
        foreach (string name in new[]
        {
            "OpenMCAD.SamplePlugin",
            "OpenMCAD.SamplePlugin.Incompatible",
            "OpenMCAD.SamplePlugin.Faulty",
            "OpenMCAD.SamplePlugin.Ambiguous",
        })
        {
            File.Exists(FixturePath(name)).Should().BeTrue("{0} should have been built", name);
        }
    }

    [Fact]
    public void AGoodPluginLoadsAndIsInitialised()
    {
        PluginLoader loader = new();
        TestHost host = new();

        PluginLoadResult result = loader.Load(FixturePath("OpenMCAD.SamplePlugin"), host);

        result.Loaded.Should().BeTrue(result.Detail);
        result.Name.Should().Be("Sample");
        host.Initialised.Should().BeTrue("the host must actually reach Initialize");
    }

    [Fact]
    public void ThePluginSeesTheHostsInterfaceRatherThanItsOwn()
    {
        // The failure this guards is the notorious one: if the plugin's context loaded its own
        // copy of OpenMCAD.Api, its IPlugin would be a different type with the same name and the
        // cast would fail with "unable to cast object of type IPlugin to type IPlugin".
        PluginLoader loader = new();

        PluginLoadResult result = loader.Load(FixturePath("OpenMCAD.SamplePlugin"), Host());

        result.Plugin.Should().BeAssignableTo<IPlugin>();
        result.Plugin!.GetType().Assembly.Should().NotBeSameAs(
            typeof(IPlugin).Assembly, "the plugin itself must come from its own context");

        // Same interface type object, from the host's assembly, despite the plugin being loaded
        // into an isolated context.
        typeof(IPlugin).IsInstanceOfType(result.Plugin).Should().BeTrue();
    }

    [Fact]
    public void APluginBuiltAgainstAnotherApiVersionIsRefusedBeforeItRuns()
    {
        PluginLoader loader = new();

        PluginLoadResult result = loader.Load(FixturePath("OpenMCAD.SamplePlugin.Incompatible"), Host());

        result.Loaded.Should().BeFalse();
        result.Rejection.Should().Be(PluginRejection.IncompatibleApiVersion);

        // Refused *before* Initialize, so it never runs against a surface it was not built for.
        // The alternative is a MissingMethodException at some later, unrelated moment.
        result.Detail.Should().Contain("built against API");
    }

    [Fact]
    public void APluginThatThrowsIsReportedRatherThanFatal()
    {
        // A third-party assembly must not be able to stop the application from starting. The
        // user's only remedy would be to find and delete a file they may not know exists.
        PluginLoader loader = new();

        PluginLoadResult result = loader.Load(FixturePath("OpenMCAD.SamplePlugin.Faulty"), Host());

        result.Loaded.Should().BeFalse();
        result.Rejection.Should().Be(PluginRejection.Faulted);
        result.Detail.Should().Contain("broken on purpose");
    }

    [Fact]
    public void TwoEntryPointsInOneAssemblyIsRefusedRatherThanGuessed()
    {
        PluginLoader loader = new();

        PluginLoadResult result = loader.Load(FixturePath("OpenMCAD.SamplePlugin.Ambiguous"), Host());

        result.Loaded.Should().BeFalse();
        result.Rejection.Should().Be(PluginRejection.AmbiguousEntryPoint);
    }

    [Fact]
    public void AnAssemblyWithNoPluginIsSkippedQuietly()
    {
        // A plugin directory contains support libraries too, and they are not failures.
        PluginLoader loader = new();

        string ordinary = typeof(PluginLoader).Assembly.Location;
        PluginLoadResult result = loader.Load(ordinary, Host());

        result.Rejection.Should().Be(PluginRejection.NoEntryPoint);
    }

    [Fact]
    public void AFileThatIsNotAnAssemblyIsSkipped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"not-a-plugin-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "this is not a PE file");

        try
        {
            PluginLoader loader = new();
            loader.Load(path, Host()).Rejection.Should().Be(PluginRejection.NotAnAssembly);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingDirectoryYieldsNothingRatherThanThrowing()
    {
        PluginLoader loader = new();

        loader.LoadFrom(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Host())
            .Should().BeEmpty();
    }

    [Fact]
    public void ADirectoryOfPluginsLoadsInAStableOrder()
    {
        // Directory enumeration order is unspecified. A load order that varies between machines
        // turns an interaction between two plugins into a bug that reproduces for one user only.
        string directory = Path.Combine(Path.GetTempPath(), $"plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach (string name in new[] { "OpenMCAD.SamplePlugin", "OpenMCAD.SamplePlugin.Faulty" })
            {
                File.Copy(FixturePath(name), Path.Combine(directory, name + ".dll"));
            }

            PluginLoader loader = new();
            IReadOnlyList<PluginLoadResult> results = loader.LoadFrom(directory, Host());

            results.Should().HaveCount(2);
            results.Select(r => Path.GetFileName(r.Path))
                .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);

            // One good, one broken: the broken one must not prevent the other from loading.
            results.Count(r => r.Loaded).Should().Be(1);
        }
        finally
        {
            // Best effort. A loaded plugin's file is locked for the life of the process -- see the
            // remarks on PluginLoader -- so this directory cannot be removed here, and failing the
            // test over its own tidying would hide the assertions above.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void AGoodPluginContributesItsRibbonCommand()
    {
        // P2-T15's actual deliverable. The loader working is not the same as a plugin being able
        // to do anything, and a plugin that loads and contributes nothing is indistinguishable
        // from one that failed silently.
        PluginLoader loader = new();
        TestHost host = new();

        loader.Load(FixturePath("OpenMCAD.SamplePlugin"), host).Loaded.Should().BeTrue();

        host.Registry.Commands.Should().ContainSingle();

        ContributedCommand contributed = host.Registry.Commands[0];
        contributed.Command.Id.Should().Be("openmcad.sample.hello");
        contributed.Command.Label.Should().Be("Say Hello");
        contributed.Command.Group.Should().Be("Sample");

        // And it runs. A command whose action never fires looks perfectly correct in a ribbon.
        contributed.Command.Execute();
    }

    [Fact]
    public void TwoPluginsCannotClaimTheSameCommandId()
    {
        // Silently letting the second overwrite the first would make one plugin's buttons vanish
        // depending on the order a directory happened to enumerate in.
        CommandRegistry registry = new();

        registry.For("first").Add(Command("shared.id"));

        Action act = () => registry.For("second").Add(Command("shared.id"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
        registry.Commands.Should().ContainSingle();
    }

    [Fact]
    public void CommandsCannotBeAddedAfterLoadingFinishes()
    {
        // A plugin that held the registry and added a button months later, from a thread nobody
        // expected, would produce a ribbon the user cannot learn.
        CommandRegistry registry = new();
        ICommandRegistry scoped = registry.For("late");

        registry.Close();

        Action act = () => scoped.Add(Command("too.late"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*after loading finished*");
    }

    [Fact]
    public void ACommandWithoutAnActionIsRefusedWhenItIsBuilt()
    {
        // Not when the button is pressed. A command with no action looks correct in a ribbon and
        // does nothing, which a user reports as the application being broken.
        Action act = () => _ = new PluginCommand("id", "Label", "Description", "Group", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ACommandKeepsItsContributorSoAFailureIsAttributable()
    {
        CommandRegistry registry = new();
        registry.For("Acme Exporter").Add(Command("acme.export"));

        registry.Commands[0].PluginName.Should().Be("Acme Exporter");
    }

    private static PluginCommand Command(string id)
        => new(id, "Label", "Description", "Group", static () => { });

    [Fact]
    public void TheHostApiVersionMatchesTheDeclaredSurface()
    {
        PluginLoader.HostApiVersion.Major.Should().Be(ApiVersion.Major);
        PluginLoader.HostApiVersion.Minor.Should().Be(ApiVersion.Minor);
    }

    private sealed class TestHost : IPluginHost
    {
        public bool Initialised { get; private set; }

        /// <summary>The real registry, so contributions are exercised rather than swallowed.</summary>
        public CommandRegistry Registry { get; } = new();

        public Version ApiVersion => PluginLoader.HostApiVersion;

        public ICommandRegistry Commands => Registry.For("test");

        public ILogger Logger => new RecordingLogger(() => Initialised = true);

        private sealed class RecordingLogger(Action onLog) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => onLog();
        }
    }
}
