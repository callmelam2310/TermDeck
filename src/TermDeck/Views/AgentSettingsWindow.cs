using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TermDeck.Core.Ai;

namespace TermDeck.Views;

/// <summary>
/// Manages the AI provider/model profiles (like Pentest-Assistant's AI profiles): pick one from the list, edit its
/// base URL / token / model / extra env, save, and choose which is active. The active profile's model + env are
/// injected only into the Claude CLI the agent spawns. Edits a copy; OK (Use) or Save persists to disk.
/// </summary>
public sealed class AgentSettingsWindow : Window
{
    readonly AgentProfilesData _data;
    readonly ComboBox _list = new() { Width = 220 };
    readonly TextBox _name = Tb();
    readonly TextBox _baseUrl = Tb(mono: true);
    readonly PasswordBox _token = new() { Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 8) };
    readonly TextBox _model = Tb();
    readonly TextBox _extra = Tb(mono: true, multiline: true);
    readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8) };

    AgentProfile? _current;
    bool _loading;

    /// <summary>The name of the profile the user chose as active (set when OK/Use is pressed).</summary>
    public string? ChosenActive { get; private set; }

    public AgentSettingsWindow(AgentProfilesData data)
    {
        _data = data;
        Title = "AI settings — provider profiles";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinWidth = 460;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _hint.Text = "Leave everything blank to use your logged-in Claude subscription (claude /login). " +
                     "Extra = one KEY=VALUE per line, e.g. ANTHROPIC_MODEL=deepseek-chat. Values are injected only into the Claude CLI.";

        _list.SelectionChanged += (_, _) => { if (!_loading) SwitchTo(_list.SelectedItem as string); };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(Small("New", NewProfile));
        btns.Children.Add(Small("Delete", DeleteProfile));
        DockPanel.SetDock(btns, Dock.Right);
        top.Children.Add(btns);
        top.Children.Add(new TextBlock { Text = "Profile", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_list);

        var form = new StackPanel();
        form.Children.Add(Label("Name"));
        form.Children.Add(_name);
        form.Children.Add(Label("Base URL (ANTHROPIC_BASE_URL) — blank for Anthropic / your subscription"));
        form.Children.Add(_baseUrl);
        form.Children.Add(Label("Auth token (ANTHROPIC_AUTH_TOKEN) — blank for your subscription"));
        form.Children.Add(_token);
        form.Children.Add(Label("Model (passed as --model) — blank for the CLI default"));
        form.Children.Add(_model);
        form.Children.Add(Label("Extra env (KEY=VALUE per line)"));
        _extra.Height = 120;
        form.Children.Add(_extra);
        form.Children.Add(_hint);

        var save = new Button { Content = "Save", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => { Commit(); Persist(); Status("Saved."); };
        var use = new Button { Content = "Use this profile", Width = 130, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        use.Click += (_, _) =>
        {
            Commit();
            _data.Active = _current?.Name ?? "";
            ChosenActive = _data.Active;
            Persist();
            DialogResult = true;
        };
        var close = new Button { Content = "Close", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(use);
        buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(form);
        root.Children.Add(buttons);
        Content = root;

        Reload(_data.Active);
    }

    readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0) };
    void Status(string s) { _status.Text = s; }

    void Reload(string? select)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var p in _data.Profiles) _list.Items.Add(p.Name);
        _loading = false;
        var pick = _data.Profiles.FirstOrDefault(p => p.Name == select) ?? _data.Profiles.FirstOrDefault();
        if (pick != null) { _list.SelectedItem = pick.Name; SwitchTo(pick.Name); }
        else SwitchTo(null);
    }

    void SwitchTo(string? name)
    {
        // Save the fields being left into the previous profile first.
        if (_current != null && !_loading) Commit();
        _current = _data.Profiles.FirstOrDefault(p => p.Name == name);
        _loading = true;
        _name.Text = _current?.Name ?? "";
        _baseUrl.Text = _current?.BaseUrl ?? "";
        _token.Password = _current?.AuthToken ?? "";
        _model.Text = _current?.Model ?? "";
        _extra.Text = _current?.Extra ?? "";
        _loading = false;
        var enabled = _current != null;
        foreach (var c in new Control[] { _name, _baseUrl, _token, _model, _extra }) c.IsEnabled = enabled;
    }

    /// <summary>Writes the form fields into the current profile object (handles rename).</summary>
    void Commit()
    {
        if (_current == null) return;
        var newName = _name.Text.Trim();
        if (newName.Length == 0) newName = _current.Name.Length > 0 ? _current.Name : "profile";
        // Keep names unique.
        if (_data.Profiles.Any(p => p != _current && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            newName += " " + DateTime.Now.ToString("HHmmss");
        var wasActive = _data.Active == _current.Name;
        _current.Name = newName;
        _current.BaseUrl = _baseUrl.Text.Trim();
        _current.AuthToken = _token.Password;
        _current.Model = _model.Text.Trim();
        _current.Extra = _extra.Text;
        if (wasActive) _data.Active = newName;
        _loading = true;
        var idx = _list.Items.IndexOf(_list.SelectedItem);
        _list.Items.Clear();
        foreach (var p in _data.Profiles) _list.Items.Add(p.Name);
        if (idx >= 0 && idx < _list.Items.Count) _list.SelectedIndex = idx;
        _loading = false;
    }

    void NewProfile()
    {
        if (_current != null) Commit();
        var p = new AgentProfile { Name = UniqueName("New profile") };
        _data.Profiles.Add(p);
        Reload(p.Name);
        _name.Focus();
        _name.SelectAll();
    }

    void DeleteProfile()
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"Delete profile \"{_current.Name}\"?", "AI settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var wasActive = _data.Active == _current.Name;
        _data.Profiles.Remove(_current);
        if (wasActive) _data.Active = _data.Profiles.FirstOrDefault()?.Name ?? "";
        _current = null;
        Reload(_data.Active);
        Persist();
    }

    string UniqueName(string baseName)
    {
        var n = baseName;
        var i = 2;
        while (_data.Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n = $"{baseName} {i++}";
        return n;
    }

    void Persist()
    {
        try { AgentProfiles.Save(_data); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AI settings"); }
    }

    static TextBox Tb(bool mono = false, bool multiline = false)
    {
        var b = new TextBox { Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 8), VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) b.SetResourceReference(FontFamilyProperty, "MonoFont");
        if (multiline)
        {
            b.AcceptsReturn = true;
            b.TextWrapping = TextWrapping.NoWrap;
            b.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            b.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            b.VerticalContentAlignment = VerticalAlignment.Top;
            b.FontSize = 12;
        }
        return b;
    }

    static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 2, 0, 0) };

    static Button Small(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
