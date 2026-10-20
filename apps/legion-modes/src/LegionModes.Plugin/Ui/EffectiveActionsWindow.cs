using System.Windows;
using System.Windows.Controls;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Plugin.Pipelines;
using LenovoLegionToolkit.WPF.Controls;
using LenovoLegionToolkit.WPF.Windows;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;

namespace LegionModes.Plugin.Ui;

// «Итоговые действия» of a profile, read-only: the pipelines that run in it (its own and inherited), each with its
// event, steps and source profile; switched-off ones folded below. Matches Resolver.Pipelines.
public sealed class EffectiveActionsWindow : BaseWindow
{
    EffectiveActionsWindow(ModesConfig config, Profile p)
    {
        Title = Tr.T("Итоговые действия");
        Width = 760;
        Height = 720;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(SharedResources.Instance);

        var all = Resolver.Pipelines(config, p);
        var list = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        var wins = all.Where(e => e.State == PipelineState.Wins).ToList();
        if (wins.Count == 0)
            list.Children.Add(Card(SymbolRegular.Flowchart24, "PalettePurpleBrush", Tr.T("Действий нет"), Text(Tr.T("В этом профиле не выполняется ни одно действие LLT."), "SubLine")));
        foreach (var e in wins) list.Children.Add(PipelineCard(e, p));

        var others = all.Where(e => e.State != PipelineState.Wins).ToList();
        if (others.Count > 0)
        {
            var folded = new StackPanel();
            foreach (var e in others) folded.Children.Add(PipelineCard(e, p));
            list.Children.Add(new LenovoLegionToolkit.WPF.Controls.Custom.CardExpander
            {
                Header = new CardHeaderControl { Title = Tr.F("Выключенные ({0})", others.Count), Subtitle = Tr.T("Не выполняются в этом профиле") },
                Icon = SymbolRegular.EyeOff24,
                Content = folded,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        var title = new TextBlock { Text = Tr.F("Итоговые действия · {0} · {1}", config.IndexOf(p), ProfileNames.Display(p)) };
        Content = Frame(SymbolRegular.Flowchart24, "PalettePurpleBrush", title,
            new Wpf.Ui.Controls.DynamicScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, null);
    }

    public static void Open(Window? owner, ModesConfig config, Profile p) =>
        new EffectiveActionsWindow(config, p) { Owner = owner }.ShowDialog();

    static UIElement PipelineCard(EffectivePipeline e, Profile inForce)
    {
        var body = new StackPanel();
        var (when, steps) = Describe(e.Pipeline);
        body.Children.Add(Row(SymbolRegular.Flash24, Tr.T("Когда"), Text(when, "RowValue")));
        body.Children.Add(Row(SymbolRegular.TextBulletListLtr24, Tr.T("Шаги"), Text(steps, "RowValue")));
        body.Children.Add(Row(SymbolRegular.Layer24, Tr.T("Из профиля"), Text(e.Source.Id == inForce.Id ? Tr.F("«{0}» (свой)", ProfileNames.Display(e.Source)) : $"«{ProfileNames.Display(e.Source)}»", "RowValue")));
        if (e.State != PipelineState.Wins)
            body.Children.Add(Text(NotRunningText(e), "SubLine"));
        return Card(SymbolRegular.Flowchart24, e.State == PipelineState.Wins ? "PalettePurpleBrush" : "TextFillColorTertiaryBrush",
            ProfileEditorWindow.PipelineName(e.Pipeline), body);
    }

    // Why a pipeline does not run here.
    internal static string NotRunningText(EffectivePipeline e) =>
        e.State == PipelineState.Blocked ? Tr.F("выключен в «{0}»", e.BlockedBy?.Name is { } bn ? Tr.T(bn) : "") : Tr.T("выключен");

    // A step as LLT's add-step list names it (its control's title, in LLT's language), else its type name.
    static string StepTitle(LenovoLegionToolkit.Lib.Automation.Steps.IAutomationStep step)
    {
        var name = step.GetType().Name.Replace("AutomationStep", "", StringComparison.Ordinal);
        try
        {
            var resources = LenovoLegionToolkit.WPF.Resources.Resource.ResourceManager;
            if (resources.GetString(name + "AutomationStepControl_Title", LenovoLegionToolkit.WPF.Resources.Resource.Culture) is { Length: > 0 } title)
                return title;
        }
        catch (Exception e) { Log.Trace("step title: " + e.Message); }
        return name;
    }

    // The event in LLT's own words (the trigger's DisplayName) and the steps by their type names.
    static (string When, string Steps) Describe(Pipeline p)
    {
        var when = ProfileEditorWindow.KeyText(p.Key);
        var steps = "—";
        try
        {
            var llt = PipelineJson.Read(p.Llt);
            if (llt.Trigger is { } t) when = t.DisplayName;
            if (llt.Steps.Count > 0)
                steps = string.Join(", ", llt.Steps.Select(StepTitle));
        }
        catch (Exception ex) { Log.Error("pipeline describe", ex); }
        return (when, steps);
    }
}
