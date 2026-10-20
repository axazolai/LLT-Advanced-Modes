using LegionModes.Core.Model;
using LegionModes.Plugin.Pipelines;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;
using Newtonsoft.Json.Linq;

namespace LegionModes.Tests;

public sealed class PipelineKeyTests
{
    // Every property of `expected` is present in `actual` with the same value (LLT adds the members the stored JSON omits).
    static bool Contains(JToken expected, JToken? actual)
    {
        if (actual is null) return false;
        if (expected is JObject eo)
            return actual is JObject ao && eo.Properties().All(p => Contains(p.Value, ao[p.Name]));
        if (expected is JArray ea)
            return actual is JArray aa && ea.Count == aa.Count && ea.Zip(aa).All(z => Contains(z.First, z.Second));
        return JToken.DeepEquals(expected, actual);
    }

    [Fact(DisplayName = "@important the factory ollama pipeline survives a read and a write with every stored member intact")]
    public void FactoryOllamaPipelineRoundTrips()
    {
        var json = FactoryProfiles.Create().Profiles.SelectMany(p => p.Pipelines).Single(p => p.Key == PipelineKeys.Enter).Llt;
        var written = PipelineJson.Write(PipelineJson.Read(json));
        Assert.True(Contains(JObject.Parse(json), JObject.Parse(written)), written);
        Assert.DoesNotContain("\n", written);
        Assert.Equal(written, PipelineJson.Write(PipelineJson.Read(written)));
    }

    [Fact(DisplayName = "@important a pipeline with a power-mode trigger keeps its trigger and its type names through a write and a read")]
    public void TriggeredPipelineRoundTrips()
    {
        var pipeline = new AutomationPipeline(new PowerModeAutomationPipelineTrigger(PowerModeState.Quiet)) { Name = "quiet" };
        var json = PipelineJson.Write(pipeline);
        Assert.Contains("PowerModeAutomationPipelineTrigger, LenovoLegionToolkit.Lib.Automation", json);
        var back = PipelineJson.Read(json);
        var trigger = Assert.IsType<PowerModeAutomationPipelineTrigger>(back.Trigger);
        Assert.Equal(PowerModeState.Quiet, trigger.PowerModeState);
        Assert.Equal("quiet", back.Name);
        Assert.Equal(json, PipelineJson.Write(back));
    }

    [Fact(DisplayName = "@important the key of a trigger is equal for equal parameters and differs when a parameter differs")]
    public void KeyOfFollowsTheTriggerParameters()
    {
        var quietA = PipelineJson.KeyOf(new PowerModeAutomationPipelineTrigger(PowerModeState.Quiet));
        var quietB = PipelineJson.KeyOf(PipelineJson.Read(PipelineJson.Write(
            new AutomationPipeline(new PowerModeAutomationPipelineTrigger(PowerModeState.Quiet)))).Trigger!);
        var balance = PipelineJson.KeyOf(new PowerModeAutomationPipelineTrigger(PowerModeState.Balance));
        Assert.Equal(quietA, quietB);
        Assert.NotEqual(quietA, balance);
        Assert.StartsWith("llt:", quietA);
        Assert.NotEqual(quietA, PipelineJson.KeyOf(new ACAdapterConnectedAutomationPipelineTrigger()));
    }
}
