using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;

namespace LegionModes.Plugin.Pipelines;

// Runs the winning pipelines of the profile in force. One queue: runs never overlap and keep their arrival order. LLT's
// triggers do their own matching (IsMatchingEvent); the runner only picks the pipelines and feeds the events.
internal sealed class PipelineRunner(Func<bool> active, Func<AutomationPipeline, Task> run, Action<string?> report)
{
    readonly object _g = new();
    readonly Dictionary<Guid, string> _failed = [];   // under _g
    Task _tail = Task.CompletedTask;                    // under _g

    // The «выход» pipelines are awaited at most this long in total; the apply follows regardless.
    internal static readonly TimeSpan ExitCap = TimeSpan.FromSeconds(10);

    // «выход» of From (awaited up to ExitCap), then `apply`, then «вход» of To (queued, not awaited). `apply` runs outside the queue.
    public async Task OnTransitionAsync(ModesConfig c, Transition t, Func<Task> apply)
    {
        if (t.From is { } from)
        {
            var exit = Enqueue(() => RunKeyAsync(c, from, PipelineKeys.Exit));
            if (await Task.WhenAny(exit, Task.Delay(ExitCap)).ConfigureAwait(false) != exit)
                Log.Trace($"exit pipelines of «{from.Name}» still run after {ExitCap.TotalSeconds:0} s: the apply goes ahead");
        }
        await apply().ConfigureAwait(false);
        _ = Enqueue(() => RunKeyAsync(c, t.To, PipelineKeys.Enter));   // Chain catches everything
    }

    // Queued; the caller does not have to await it.
    public Task OnEventAsync(ModesConfig c, Profile inForce, IAutomationEvent e) =>
        Enqueue(() => RunEventAsync(c, inForce, e));

    // StartupAutomationEvent, once with the first apply under control.
    public Task OnStartAsync(ModesConfig c, Profile inForce) => OnEventAsync(c, inForce, new StartupAutomationEvent());

    Task Enqueue(Func<Task> work)
    {
        lock (_g) return _tail = Chain(_tail, work);
    }

    static async Task Chain(Task previous, Func<Task> work)
    {
        try { await previous.ConfigureAwait(false); } catch { /* reported by its own link */ }
        try { await work().ConfigureAwait(false); }
        catch (Exception e) { Log.Error("pipeline queue", e); }
    }

    async Task RunKeyAsync(ModesConfig c, Profile profile, string key)
    {
        foreach (var ep in Resolver.Pipelines(c, profile).Where(p => p.State == PipelineState.Wins && p.Pipeline.Key == key))
        {
            if (!active()) return;
            await RunOneAsync(ep, null).ConfigureAwait(false);
        }
    }

    async Task RunEventAsync(ModesConfig c, Profile inForce, IAutomationEvent e)
    {
        foreach (var ep in Resolver.Pipelines(c, inForce).Where(p => p.State == PipelineState.Wins
                     && p.Pipeline.Key != PipelineKeys.Enter && p.Pipeline.Key != PipelineKeys.Exit))
        {
            if (!active()) return;
            await RunOneAsync(ep, e).ConfigureAwait(false);
        }
    }

    // e == null: «вход» / «выход» (no trigger to match).
    async Task RunOneAsync(EffectivePipeline ep, IAutomationEvent? e)
    {
        string? name = null;
        try
        {
            var pipeline = PipelineJson.Read(ep.Pipeline.Llt);
            name = pipeline.Name;
            if (e is not null)
            {
                if (pipeline.Trigger is not { } trigger) return;
                // At LLT's start only an «on startup» trigger runs: the profile's «вход» covers the start, so the
                // pipeline's own Run-on-startup box is hidden and not read (owner 2026-10-20).
                if (e is StartupAutomationEvent && trigger is not OnStartupAutomationPipelineTrigger) return;
                if (!await trigger.IsMatchingEvent(e).ConfigureAwait(false)) return;
            }
            await run(pipeline).ConfigureAwait(false);
            Settle(ep.Pipeline.Id, null);
        }
        catch (Exception ex)
        {
            var detail = ex is AggregateException ae ? string.Join("; ", ae.InnerExceptions.Select(i => i.Message)) : ex.Message;
            var label = name ?? ep.Pipeline.Id.ToString();
            Log.Error($"pipeline {label} of profile {ep.Source.Name}", ex);
            Settle(ep.Pipeline.Id, Tr.F("{0} (profile \"{1}\"): {2}", label, ProfileNames.Display(ep.Source), detail));
        }
    }

    // The Status line lists the pipelines whose last run failed.
    void Settle(Guid id, string? error)
    {
        string? text;
        lock (_g)
        {
            if (error is null) { if (!_failed.Remove(id)) return; }
            else _failed[id] = error;
            text = _failed.Count == 0 ? null : string.Join(" | ", _failed.Values);
        }
        try { report(text); } catch (Exception ex) { Log.Error("pipeline status", ex); }
    }
}
