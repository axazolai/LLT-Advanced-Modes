using System.Reflection;
using LegionModes.Core.Contracts;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Settings;

namespace LegionModes.Plugin;

// Run once after start (and by the keeper's report): proves the plugin sees LLT the way it expects.
internal static class SelfTest
{
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static async Task<SelfTestReport> RunAsync()
    {
        // Failures: the plugin is not healthy in this LLT (not loaded, engine not built, a pipe not listening, display-state
        // registration failed, a member LLT no longer has, an apply step that threw). Warnings: results of the hardware and of
        // the files (a read-back mismatch, the DRR fallback, a refusal, a broken modes.json); they never roll an update back.
        var failures = new List<string>();
        var warnings = new List<string>();
        var rt = Runtime.Current;
        if (rt is null)
        {
            failures.Add("runtime is not started");
            return new SelfTestReport(false, failures, warnings);
        }

        // The wait for the first apply runs beside the other checks, so the report is not later than the slowest of them.
        var firstApply = rt.HasControl ? Finished(rt.FirstApply, TimeSpan.FromSeconds(30)) : Task.FromResult(true);

        if (rt.Status.ConfigError is { } ce) warnings.Add("modes.json: " + ce);
        if (rt.Status.ProgramsError is { } pe) warnings.Add("programs.json: " + pe);
        if (rt.Status.ControlError is { } cte) warnings.Add("control.json: " + cte);

        await Probe(failures, "GodModeController.GetStateAsync", () => IoCContainer.Resolve<GodModeController>().GetStateAsync());
        await Probe(failures, "PowerModeFeature.GetStateAsync", () => IoCContainer.Resolve<PowerModeFeature>().GetStateAsync());
        await Probe(failures, "BatteryFeature.GetStateAsync", () => IoCContainer.Resolve<BatteryFeature>().GetStateAsync());

        foreach (var c in ReflectionContracts.All)
        {
            var problem = Resolve(c);
            if (problem is not null) failures.Add($"reflection {c.TypeFullName}.{c.Member}: {problem}");
        }

        // The shell is placed after LLT's window has loaded (up to 60 s); the report waits for the outcome.
        try { await rt.ShellCompleted.WaitAsync(TimeSpan.FromSeconds(75)).ConfigureAwait(false); }
        catch (TimeoutException) { failures.Add("shell: injection did not finish"); }
        if (rt.Status.ShellError is { } se) failures.Add("shell: " + se);

        Guarded(failures, "settings", () =>
        {
            var freq = IoCContainer.Resolve<UpdateSettings>().Store.UpdateCheckFrequency;
            if (freq != UpdateCheckFrequency.Never) failures.Add($"UpdateCheckFrequency is {freq}, expected Never");
            if (!IoCContainer.Resolve<IntegrationsSettings>().Store.CLI) failures.Add("integrations CLI is off");
            if (rt.HasControl)
            {
                if (IoCContainer.Resolve<AutomationProcessor>().IsEnabled) failures.Add("automation is enabled under control");
                var mapping = IoCContainer.Resolve<ApplicationSettings>().Store.PowerModeMappingMode;
                if (mapping != PowerModeMappingMode.Disabled) failures.Add($"PowerModeMappingMode is {mapping} under control, expected Disabled");
            }
        });

        // The profile engine produces its first decision within seconds of the start.
        for (var i = 0; i < 100 && rt.Last is null; i++) await Task.Delay(100).ConfigureAwait(false);
        if (rt.Last is null) failures.Add("profile engine: no decision after 10 s");
        if (!rt.PluginPipeListening) failures.Add("plugin pipe: not listening");
        if (!rt.QuickPipeListening) failures.Add("quick pipe: not listening");
        if (!rt.DisplayStateRegistered) failures.Add("display state: registration failed" + (rt.DisplayStateError is { } de ? ": " + de : ""));

        // Under control the first apply is waited for (up to 30 s); only a step that threw fails the test.
        if (!await firstApply.ConfigureAwait(false)) warnings.Add("the first apply did not finish in 30 s");
        foreach (var (parameter, result) in rt.LastResults)
        {
            switch (result.Kind)
            {
                case ResultKind.Fault: failures.Add($"apply {parameter}: {result.Text}"); break;
                case ResultKind.Error or ResultKind.Note: warnings.Add($"apply {parameter}: {result.Text}"); break;
            }
        }
        return new SelfTestReport(failures.Count == 0, failures, warnings);
    }

    static async Task<bool> Finished(Task task, TimeSpan limit)
    {
        try { await task.WaitAsync(limit).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    static async Task Probe(List<string> failures, string what, Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (Exception e) { failures.Add($"{what}: {e.GetType().Name}: {e.Message}"); }
    }

    static void Guarded(List<string> failures, string what, Action check)
    {
        try { check(); }
        catch (Exception e) { failures.Add($"{what}: {e.GetType().Name}: {e.Message}"); }
    }

    // null = resolves; otherwise what is wrong.
    internal static string? Resolve(ReflectionContract c)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, c.Assembly, StringComparison.OrdinalIgnoreCase));
            if (asm is null) return $"assembly '{c.Assembly}' is not loaded";
            var type = asm.GetType(c.TypeFullName, throwOnError: false);
            if (type is null) return "type not found";
            string? actual;
            switch (c.Kind)
            {
                case MemberKind.Type:
                    return null;
                case MemberKind.Field:
                    if (type.GetField(c.Member, AnyMember) is not { } f) return "field not found";
                    actual = f.FieldType.FullName;
                    break;
                case MemberKind.Property:
                    if (type.GetProperty(c.Member, AnyMember) is not { } p) return "property not found";
                    actual = p.PropertyType.FullName;
                    break;
                default:
                    if (type.GetMethods(AnyMember).FirstOrDefault(m => m.Name == c.Member) is not { } m) return "method not found";
                    actual = m.ReturnType.FullName;
                    break;
            }
            return c.MemberTypeFullName is { } expected && expected != actual ? $"is {actual}, expected {expected}" : null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
