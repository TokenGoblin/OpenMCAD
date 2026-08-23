using FluentAssertions;

using OpenMCAD.Api;

using Xunit;

namespace OpenMCAD.Api.Tests;

/// <summary>
/// The compatibility rule plugins are loaded against (P2-T14, PLAN.md 5.12).
/// </summary>
/// <remarks>
/// Worth testing despite being a few comparisons: it decides whether a plugin loads, and the
/// alternative to getting it right is a <c>MissingMethodException</c> at some later moment, blamed
/// on whatever the user happened to be doing.
/// </remarks>
public sealed class ApiVersionTests
{
    [Fact]
    public void TheSurfaceIsStillDeclaredUnstable()
    {
        // A guard on the promise rather than on the number. Moving to 1.0 says the surface is
        // stable and removals now cost a major version -- that should be a deliberate act that
        // fails this test and makes someone justify it, not a quiet edit.
        ApiVersion.Major.Should().Be(
            0,
            "almost nothing PLAN.md 5.12 describes exists yet, so compatibility cannot be promised");
    }

    [Fact]
    public void TheVersionReadsAsSemver()
    {
        ApiVersion.Value.Should().Be($"{ApiVersion.Major}.{ApiVersion.Minor}.{ApiVersion.Patch}");
    }

    [Fact]
    public void APluginBuiltAgainstThisExactVersionIsSupported()
    {
        ApiVersion.Supports(new Version(ApiVersion.Major, ApiVersion.Minor)).Should().BeTrue();
    }

    [Fact]
    public void ADifferentMajorIsNeverSupported()
    {
        ApiVersion.Supports(new Version(ApiVersion.Major + 1, 0)).Should().BeFalse();
    }

    [Fact]
    public void WhileTheMajorIsZeroEachMinorIsItsOwnBreakingChange()
    {
        // What 0.x means. Once the major reaches 1 this expectation inverts, and the test below
        // covers that arithmetic directly so the rule is pinned either way.
        if (ApiVersion.Major != 0)
        {
            return;
        }

        ApiVersion.Supports(new Version(0, ApiVersion.Minor + 1)).Should().BeFalse();
        ApiVersion.Supports(new Version(0, ApiVersion.Minor - 1)).Should().BeFalse(
            "a 0.x surface makes no backward promise, so an older plugin is not assumed to work");
    }

    [Theory]

    // Same major, older or equal minor: everything the plugin uses is still present.
    [InlineData(1, 2, 1, 0, true)]
    [InlineData(1, 2, 1, 2, true)]

    // Same major, newer minor: the plugin may use something this host does not have.
    [InlineData(1, 2, 1, 3, false)]

    // Different major: no promise at all, in either direction.
    [InlineData(1, 2, 2, 0, false)]
    [InlineData(2, 0, 1, 9, false)]
    public void TheStableRuleIsMajorEqualAndMinorAtLeast(
        int hostMajor, int hostMinor, int pluginMajor, int pluginMinor, bool expected)
    {
        // ApiVersion.Supports reads the host version from static members, so the rule itself is
        // restated here against explicit numbers. That pins the intended behaviour now, while the
        // real one is still 0.x and cannot exercise these cases.
        static bool Rule(int hostMajor, int hostMinor, Version required)
            => required.Major == hostMajor
                && (hostMajor == 0 ? required.Minor == hostMinor : required.Minor <= hostMinor);

        Rule(hostMajor, hostMinor, new Version(pluginMajor, pluginMinor)).Should().Be(expected);
    }

    [Fact]
    public void SupportsRejectsNull()
    {
        FluentActions.Invoking(() => ApiVersion.Supports(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
