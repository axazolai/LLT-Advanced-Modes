using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LegionModes.Plugin.Pipelines;

// LLT's own pipeline serialisation (AbstractSettings, with Formatting.None): a pipeline stored in modes.json is read and
// written exactly as LLT would.
internal static class PipelineJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.None,
        TypeNameHandling = TypeNameHandling.Auto,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Converters = { new StringEnumConverter() },
    };

    public static AutomationPipeline Read(string llt) => JsonConvert.DeserializeObject<AutomationPipeline>(llt, Settings)!;

    public static string Write(AutomationPipeline p) => JsonConvert.SerializeObject(p, Settings);

    // The key of a triggered pipeline: its trigger with the type name. "enter" / "exit" pipelines have Trigger == null.
    public static string KeyOf(IAutomationPipelineTrigger t) =>
        "llt:" + JsonConvert.SerializeObject(t, typeof(IAutomationPipelineTrigger), Settings);
}
