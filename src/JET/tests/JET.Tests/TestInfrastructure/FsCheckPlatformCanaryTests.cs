using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Xunit;

namespace JET.Tests.TestInfrastructure;

public sealed class FsCheckPlatformCanaryTests
{
    [Property(Replay = "12345,67891", MaxTest = 64)]
    public bool FixedSeedReplay_RunsThroughTheXunitV3Adapter(int value)
    {
        return unchecked(-unchecked(-value)) == value;
    }

    [Fact]
    public void CoreRunner_ObservesShrinkingWithoutTurningTheCanaryRed()
    {
        var runner = new RecordingRunner();
        var values = Arb.From(Gen.Constant(100), ShrinkTowardZero);
        var falsifiableProperty = Prop.ForAll(values, value => value <= 0);
        var config = Config.Quick
            .WithMaxTest(1)
            .WithRunner(runner);

        Check.One(config, falsifiableProperty);

        Assert.NotNull(runner.Result);
        Assert.True(runner.Result.IsFailed);
        Assert.True(runner.ShrinkCount > 0);
        Assert.Equal(1, Assert.IsType<int>(runner.LastSuccessfulShrink));
    }

    private static IEnumerable<int> ShrinkTowardZero(int value)
    {
        if (value <= 1)
        {
            yield break;
        }

        yield return value / 2;
        yield return 1;
        yield return 0;
    }

    private sealed class RecordingRunner : IRunner
    {
        internal int ShrinkCount { get; private set; }

        internal object? LastSuccessfulShrink { get; private set; }

        internal FsCheck.TestResult? Result { get; private set; }

        public void OnStartFixture(Type fixtureType)
        {
        }

        public void OnArguments(
            int testNumber,
            FSharpList<object> arguments,
            FSharpFunc<int, FSharpFunc<FSharpList<object>, string>> formatter)
        {
        }

        public void OnShrink(
            FSharpList<object> arguments,
            FSharpFunc<FSharpList<object>, string> formatter)
        {
            ShrinkCount++;
            LastSuccessfulShrink = arguments.Single();
        }

        public void OnFinished(string testName, FsCheck.TestResult result)
        {
            Result = result;
        }
    }
}
