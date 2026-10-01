using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>
/// Autorun: when a run of tool A finishes, every enabled rule whose source is A reads the output (or a file),
/// filters it and queues runs of the next tool. Queued runs start as soon as the rule's parallel limit allows.
/// Runs started by a rule carry a chain depth, so a loop (A → B → A…) stops at <see cref="Autorun.MaxDepth"/>.
/// </summary>
public partial class MainWindow
{
    sealed record AutoJob(AutoRule Rule, ToolDef Tool, string Args, string Origin, int Depth);

    readonly List<AutoJob> _autoQueue = new();
    /// <summary>Runs started by autorun that have not finished yet: rule and chain depth.</summary>
    readonly Dictionary<long, (string RuleId, int Depth)> _autoRuns = new();
    /// <summary>Autorun runs the user force-stopped, so finishing them does not trigger the next tool in the chain.</summary>
    readonly HashSet<long> _autoCancelled = new();

    void Autorun_Click(object sender, RoutedEventArgs e) => OpenAutorun();

    void OpenAutorun(string? select = null, (ToolDef, RunRecord)? newFrom = null)
    {
        var w = new AutorunWindow(_config.AutoRules, _tools, _store, select, newFrom) { Owner = this };
        if (w.ShowDialog() != true) return;
        _config.AutoRules = w.Rules;
        SaveConfig();
        UpdateAutorunStatus();
    }

    void AutorunEnabled_Click(object sender, RoutedEventArgs e) => SetAutorunEnabled(AutorunEnabledMenu.IsChecked);

    void SetAutorunEnabled(bool on)
    {
        _config.AutorunEnabled = on;
        SaveConfig();
        UpdateAutorunStatus();
    }

    void StatusAutorun_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = StatusAutorun, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(Item($"⛔ Stop autorun now ({_autoRuns.Count} running, {_autoQueue.Count} queued)", StopAutorun,
            enabled: AutorunBusy, color: "#C50F1F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Autorun enabled", () => SetAutorunEnabled(!_config.AutorunEnabled), isChecked: _config.AutorunEnabled));
        menu.Items.Add(Item("Rules...", () => OpenAutorun(), color: "#C8930C"));
        menu.IsOpen = true;
        e.Handled = true;
    }

    bool AutorunBusy => _autoRuns.Count > 0 || _autoQueue.Count > 0;

    /// <summary>Stops the whole in-flight chain: drop the pending queue and Ctrl+C/kill every run autorun started.</summary>
    void StopAutorun()
    {
        var queued = _autoQueue.Count;
        _autoQueue.Clear();
        var running = _autoRuns.Keys.ToList();
        foreach (var id in running)
        {
            _autoCancelled.Add(id);
            foreach (var t in Views.OfType<ToolTab>()) if (t.StopRun(id)) break;
        }
        UpdateAutorunStatus();
        ShowStatus($"⚡ Autorun stopped — cancelled {queued} queued, stopping {running.Count} running");
    }

    void UpdateAutorunStatus()
    {
        AutorunEnabledMenu.IsChecked = _config.AutorunEnabled;
        var rules = _config.AutoRules.Count(r => r.Enabled);
        string text, color;
        if (AutorunBusy)
        {
            text = $"⚡ Autorun running · {_autoRuns.Count} running";
            if (_autoQueue.Count > 0) text += $" · {_autoQueue.Count} queued";
            text += " — click to stop";
            color = "#C8930C";
        }
        else
        {
            text = _config.AutorunEnabled ? $"⚡ Autorun on · {rules} rule(s)" : "⚡ Autorun off";
            color = _config.AutorunEnabled ? "TextSecondary" : "TextMuted";
        }
        StatusAutorun.Text = text;
        StatusAutorun.ToolTip = AutorunBusy
            ? "A tool chain is running. Click to stop it (cancels the queue and stops running tools)."
            : "Autorun — click to turn on/off, edit rules, or stop a running chain.";
        if (color.StartsWith('#'))
            StatusAutorun.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        else
            StatusAutorun.SetResourceReference(TextBlock.ForegroundProperty, color);

        // Also surface it on the ribbon button (much more visible than the status bar): relabel + blink while running.
        AutorunRibbonLabel.Text = AutorunBusy ? $"Running {_autoRuns.Count}" : "Autorun";
        AutorunRibbonButton.ToolTip = AutorunBusy
            ? $"Autorun is running ({_autoRuns.Count} running, {_autoQueue.Count} queued). Click the status bar to stop."
            : "Autorun: when a tool finishes, filter its output and run the next tool with it";
        SetAutorunBlink(AutorunBusy);
    }

    bool _autorunBlinking;

    void SetAutorunBlink(bool on)
    {
        if (on == _autorunBlinking) return;
        _autorunBlinking = on;
        if (on)
        {
            var blink = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            AutorunRibbonIcon.BeginAnimation(UIElement.OpacityProperty, blink);
        }
        else
        {
            AutorunRibbonIcon.BeginAnimation(UIElement.OpacityProperty, null);
            AutorunRibbonIcon.Opacity = 1;
        }
    }

    /// <summary>Project switch: queued runs belong to the old project.</summary>
    void ResetAutorun()
    {
        _autoQueue.Clear();
        _autoRuns.Clear();
        _autoCancelled.Clear();
        UpdateAutorunStatus();
    }

    void OnToolRunFinished(ToolTab tab, RunRecord run)
    {
        var wasAuto = _autoRuns.Remove(run.Id, out var info);
        var cancelled = _autoCancelled.Remove(run.Id);
        PumpAutorun();
        if (cancelled) { UpdateAutorunStatus(); return; } // user stopped this run; don't chain onward
        if (!_config.AutorunEnabled || _store == null) return;
        var depth = wasAuto ? info.Depth : 0;
        foreach (var rule in _config.AutoRules.Where(r => r.Enabled && r.FromToolId == tab.Tool.Id && Autorun.Matches(r, run)).ToList())
            _ = FireAsync(rule, run, depth + 1, manual: false);
    }

    /// <summary>History menu of a tool tab: run a rule on that run by hand, or create a rule from it.</summary>
    void OnAutorunRequested(ToolTab tab, RunRecord run, AutoRule? rule)
    {
        if (rule == null) OpenAutorun(newFrom: (tab.Tool, run));
        else _ = FireAsync(rule, run, 1, manual: true);
    }

    async Task FireAsync(AutoRule rule, RunRecord run, int depth, bool manual)
    {
        var store = _store;
        if (store == null) return;
        if (depth > Autorun.MaxDepth)
        {
            ShowStatus($"⚡ {rule.Name}: stopped — chain is deeper than {Autorun.MaxDepth} runs (a loop?)");
            return;
        }
        var src = _tools.FirstOrDefault(t => t.Id == rule.FromToolId);
        var dst = _tools.FirstOrDefault(t => t.Id == rule.ToToolId);
        if (dst == null)
        {
            Report($"⚡ {rule.Name}: the tool to run next no longer exists", manual);
            return;
        }

        AutoPlan plan;
        try { plan = await Task.Run(() => Autorun.Plan(rule, run, store, src, dst, writeListFile: true)); }
        catch (AutorunException ex)
        {
            Report($"⚡ {rule.Name}: " + (ex.Line > 0 ? $"filter line {ex.Line}: {ex.Message}" : ex.Message), manual);
            return;
        }
        catch (Exception ex)
        {
            Report($"⚡ {rule.Name}: {ex.Message}", manual);
            return;
        }
        if (store != _store) return; // the project changed meanwhile
        if (plan.Skipped != null)
        {
            Report($"⚡ {rule.Name}: {plan.Skipped} — {dst.Name} not started", manual);
            return;
        }

        if (rule.Confirm || (manual && plan.ArgsList.Count > 1))
        {
            var sample = string.Join("\n", plan.ArgsList.Take(5).Select(a => "$ " + CommandBuilder.Build(dst, a, store.ProjectDir).Display));
            if (plan.ArgsList.Count > 5) sample += $"\n… {plan.ArgsList.Count - 5} more";
            var times = plan.ArgsList.Count == 1 ? "once" : $"{plan.ArgsList.Count} times";
            var msg = $"Rule \"{rule.Name}\" found {plan.Values.Count} value(s) in {run.ToolName} #{run.Id}.\n" +
                      $"Run {dst.Name} {times}?\n\n{sample}" +
                      (plan.Dropped > 0 ? $"\n\n{plan.Dropped} value(s) over the limit were dropped." : "");
            if (MessageBox.Show(this, msg, "Autorun", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }

        var origin = $"{rule.Name} ← {src?.Name ?? run.ToolName} #{run.Id}";
        foreach (var args in plan.ArgsList) _autoQueue.Add(new AutoJob(rule, dst, args, origin, depth));
        ShowStatus($"⚡ {rule.Name}: {plan.Values.Count} value(s) → {dst.Name} ×{plan.ArgsList.Count}"
                   + (plan.Dropped > 0 ? $" ({plan.Dropped} dropped)" : ""));
        PumpAutorun();
    }

    /// <summary>Starts queued runs while each rule is under its parallel limit (per-value rules; others start at once).</summary>
    void PumpAutorun()
    {
        if (_store == null)
        {
            _autoQueue.Clear();
            UpdateAutorunStatus();
            return;
        }
        for (var i = 0; i < _autoQueue.Count;)
        {
            var job = _autoQueue[i];
            var max = Autorun.IsPerLine(job.Rule) ? Math.Max(1, job.Rule.MaxParallel) : int.MaxValue;
            if (_autoRuns.Values.Count(v => v.RuleId == job.Rule.Id) >= max)
            {
                i++;
                continue;
            }
            _autoQueue.RemoveAt(i);
            // Opens (or brings to front) the next tool's tab, so the user sees it start.
            var session = OpenToolTab(job.Tool)?.Launch(job.Args, job.Origin);
            // A run that failed to start still finishes (asynchronously), which removes it again.
            if (session != null) _autoRuns[session.Record.Id] = (job.Rule.Id, job.Depth);
        }
        UpdateAutorunStatus();
    }

    /// <summary>Errors of a rule fired by hand get a message box; automatic ones only the status bar.</summary>
    void Report(string message, bool manual)
    {
        if (manual) MessageBox.Show(this, message, "Autorun", MessageBoxButton.OK, MessageBoxImage.Information);
        else ShowStatus(message);
    }
}
