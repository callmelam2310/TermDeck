using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TermDeck.Core;

namespace TermDeck.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One row in the history panel.</summary>
public sealed class RunItem : Observable
{
    static readonly Brush Ok = Frozen(0x13, 0xA1, 0x0E);
    static readonly Brush Fail = Frozen(0xC5, 0x0F, 0x1F);
    static readonly Brush Running = Frozen(0x1E, 0x73, 0xD8);
    static readonly Brush Unknown = Frozen(0x99, 0x99, 0x99);

    bool _isRunning;

    public RunItem(RunRecord record, bool isRunning)
    {
        Record = record;
        _isRunning = isRunning;
    }

    public RunRecord Record { get; }

    public bool IsRunning
    {
        get => _isRunning;
        set { if (Set(ref _isRunning, value)) Refresh(); }
    }

    public void Refresh()
    {
        Raise(nameof(StatusGlyph));
        Raise(nameof(StatusBrush));
        Raise(nameof(Meta));
    }

    public string Title => string.IsNullOrWhiteSpace(Record.Args) ? "(no arguments)" : Record.Args;

    // Segoe MDL2: Sync, CheckMark, Cancel, Help
    public string StatusGlyph => IsRunning ? ""
        : Record.ExitCode == 0 ? ""
        : Record.ExitCode.HasValue ? ""
        : "";

    public Brush StatusBrush => IsRunning ? Running
        : Record.ExitCode == 0 ? Ok
        : Record.ExitCode.HasValue ? Fail
        : Unknown;

    public string Meta
    {
        get
        {
            var when = Record.StartedAt.Date == DateTime.Today
                ? Record.StartedAt.ToString("HH:mm:ss")
                : Record.StartedAt.ToString("MMM dd HH:mm");
            if (IsRunning) return $"{when} · running";
            if (Record.EndedAt is { } end)
                return $"{when} · {RunSession.FormatDuration(end - Record.StartedAt)} · exit {RunSession.DescribeExit(Record.ExitCode)}";
            return $"{when} · interrupted";
        }
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A collection node in the Tools tree. Model == null is the built-in "Ungrouped" node.</summary>
public sealed class CollectionNode : Observable
{
    readonly Action<CollectionNode, bool>? _onExpandedChanged;
    bool _isExpanded;

    public CollectionNode(ToolCollection? model, bool isExpanded, Action<CollectionNode, bool>? onExpandedChanged)
    {
        Model = model;
        _isExpanded = isExpanded;
        _onExpandedChanged = onExpandedChanged;
        Tools.CollectionChanged += (_, _) => Raise(nameof(CountText));
    }

    public ToolCollection? Model { get; }
    public bool IsUngrouped => Model == null;
    public string Id => Model?.Id ?? "";
    public string Name => Model?.Name ?? "Ungrouped";
    public ObservableCollection<ToolDef> Tools { get; } = new();
    public string CountText => Tools.Count.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) _onExpandedChanged?.Invoke(this, value); }
    }
}

/// <summary>A tab in the terminal area (Home or a tool).</summary>
public sealed class DocTab : Observable
{
    bool _isRunning;
    string _title;

    public DocTab(string title, ToolTab? view)
    {
        _title = title;
        View = view;
    }

    public ToolTab? View { get; }
    public bool IsHome => View == null;
    public bool IsWsl => View?.Tool.Kind == ToolKind.Wsl;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }
}
