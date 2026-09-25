using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SatisfactoryPlanner;

public partial class MainWindow : Window
{
    static readonly string SettingsPath = Path.Combine(GameData.AppDir, "settings.json");

    public ObservableCollection<ItemDef> ProducibleItems { get; } = new();
    readonly List<ResourceAccess> _resources = new();
    readonly ObservableCollection<TargetSpec> _targets = new();
    Settings _settings = new();
    bool _loading = true;
    readonly ObservableCollection<PlanTab> _plans = new();
    PlanTab _active = null!;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Loc.Instance.Language = _settings.Language ??= Loc.DefaultLanguage();
        InitPlans(); // after the language, so a migrated first tab gets a localized name
        GameData.Load(Loc.Instance.Language);
        LanguageCombo.ItemsSource = Loc.Languages.Select(l => Tuple.Create(l.code, l.name)).ToList();
        LanguageCombo.SelectedValue = Loc.Instance.Language;
        PopulateLists();
        ApplySettingsToUi();
        TargetsList.ItemsSource = _targets;
        _loading = false;
        Recalculate();
        // no game data yet (a build from source ships none: it's the wiki's / Coffee Stain's): fetch it from the wiki
        Loaded += async (_, _) => { if (GameData.Recipes.Count == 0) await UpdateFromWikiAsync(); else await FetchMissingIcons(); };
        Closing += (_, _) => SaveSettings();
    }

    void PopulateLists()
    {
        ProducibleItems.Clear();
        foreach (var i in GameData.Recipes.SelectMany(r => r.Out).Select(o => o.Item).Distinct()
                     .Select(GameData.Item).OrderBy(i => i.Name))
            ProducibleItems.Add(i);
        _resources.Clear();
        foreach (var r in GameData.RawResources.OrderBy(r => GameData.Item(r).Name))
            _resources.Add(new ResourceAccess { Item = r });
        ResourceAccessList.ItemsSource = null;
        ResourceAccessList.ItemsSource = _resources;
        RefreshBeltChoices();
        MinerCombo.ItemsSource = new[] { "Desc_MinerMk1_C", "Desc_MinerMk2_C", "Desc_MinerMk3_C" }
            .Select(c => new ItemDef { ClassName = c, Name = GameData.Buildings.TryGetValue(c, out var b) ? b.Name : c })
            .ToList();
    }

    /// <summary>Only offer belts that make sense at the current tier (best unlocked and one below).</summary>
    void RefreshBeltChoices()
    {
        var probe = new Settings { MaxTier = (int)TierSlider.Value };
        List<Tuple<int, string>> Choices((string cls, double rate)[] list, bool fluid)
        {
            var (min, max) = probe.AllowedTiers(fluid);
            return new[] { Tuple.Create(0, Loc.T("belt.auto")) }
                .Concat(list.Select((b, i) => Tuple.Create(i + 1, BeltName(b.cls, b.rate))).Where(t => t.Item1 >= min && t.Item1 <= max)).ToList();
        }
        bool was = _loading;
        _loading = true;
        var belt = BeltCombo.SelectedValue as int? ?? _settings.BeltTier;
        var pipe = PipeCombo.SelectedValue as int? ?? _settings.PipeTier;
        BeltCombo.ItemsSource = Choices(Settings.Belts, false);
        PipeCombo.ItemsSource = Choices(Settings.Pipes, true);
        // a choice that's no longer allowed falls back to Auto (the solver clamps anyway)
        BeltCombo.SelectedValue = ((List<Tuple<int, string>>)BeltCombo.ItemsSource).Any(t => t.Item1 == belt) ? belt : 0;
        PipeCombo.SelectedValue = ((List<Tuple<int, string>>)PipeCombo.ItemsSource).Any(t => t.Item1 == pipe) ? pipe : 0;
        _loading = was;
    }

    static string BuildingName(string cls) => GameData.Buildings.TryGetValue(cls, out var b) ? b.Name : cls;
    static string BeltName(string cls, double rate) => $"{BuildingName(cls)} · {rate:0}/min";

    void ApplySettingsToUi()
    {
        FloorsCombo.SelectedIndex = Math.Clamp(_settings.Floors, 1, 4) - 1;
        BeltCombo.SelectedValue = _settings.BeltTier;
        PipeCombo.SelectedValue = _settings.PipeTier;
        TierSlider.Value = _settings.MaxTier;
        AltCheck.IsChecked = _settings.IncludeAlternates;
        MamCheck.IsChecked = _settings.IncludeMam;
        BlueprintCombo.SelectedIndex = _settings.BlueprintTile switch { 5 => 1, 6 => 2, _ => 0 };
        HandCheck.IsChecked = _settings.HandPlaceAcrossTiles;
        HandCheck.IsEnabled = _settings.BlueprintTile > 0;
        TankCheck.IsChecked = _settings.IndustrialFluidBox;
        PolymerCheck.IsChecked = _settings.SupplyPolymers;
        MinerCombo.SelectedValue = _settings.Miner;
        ClockSlider.Value = _settings.ExtractorClock;
        ModeExact.IsChecked = _settings.Mode == RoundingMode.Exact;
        ModeSteady.IsChecked = _settings.Mode == RoundingMode.SteadyRate;
        ModeNoClog.IsChecked = _settings.Mode == RoundingMode.NoClog;
        ModeDrained.IsChecked = _settings.Mode == RoundingMode.Drained;
        CountExtractorsCheck.IsChecked = _settings.CountExtractors;
        TwoSplittersCheck.IsChecked = _settings.TwoSplittersInRow;
        BackUpCheck.IsChecked = _settings.BackUpIntermediates;
        EndSplitterCheck.IsChecked = _settings.EndSplitter;
        PlanManual.IsChecked = !_settings.Optimize;
        PlanOptimal.IsChecked = _settings.Optimize;
        foreach (var r in _resources)
        {
            r.Enabled = !_settings.DisabledResources.Contains(r.Item);
            r.Limit = _settings.ResourceLimits.TryGetValue(r.Item, out var l) ? l : null;
            var n = _settings.Nodes.GetValueOrDefault(r.Item);
            r.Impure = n?.Impure; r.Normal = n?.Normal; r.Pure = n?.Pure;
        }
        _targets.Clear();
        foreach (var t in _settings.Targets) _targets.Add(t);
        if (_targets.Count == 0) _targets.Add(new TargetSpec { Item = "Desc_ModularFrame_C", Rate = 5 });
    }

    void ReadUi()
    {
        _settings.MaxTier = (int)TierSlider.Value;
        _settings.IncludeAlternates = AltCheck.IsChecked == true;
        _settings.IncludeMam = MamCheck.IsChecked == true;
        _settings.SupplyPolymers = PolymerCheck.IsChecked == true;
        _settings.Miner = MinerCombo.SelectedValue as string ?? "Desc_MinerMk1_C";
        _settings.ExtractorClock = ClockSlider.Value;
        _settings.Mode = ModeSteady.IsChecked == true ? RoundingMode.SteadyRate
            : ModeNoClog.IsChecked == true ? RoundingMode.NoClog
            : ModeDrained.IsChecked == true ? RoundingMode.Drained : RoundingMode.Exact;
        _settings.Targets = _targets.ToList();
        _settings.Optimize = PlanOptimal.IsChecked == true;
        _settings.CountExtractors = CountExtractorsCheck.IsChecked == true;
        _settings.TwoSplittersInRow = TwoSplittersCheck.IsChecked == true;
        _settings.BackUpIntermediates = BackUpCheck.IsChecked == true;
        _settings.EndSplitter = EndSplitterCheck.IsChecked == true;
        _settings.BeltTier = BeltCombo.SelectedValue as int? ?? 0;
        _settings.PipeTier = PipeCombo.SelectedValue as int? ?? 0;
        _settings.DisabledResources = _resources.Where(r => !r.Enabled).Select(r => r.Item).ToList();
        _settings.ResourceLimits = _resources.Where(r => r.Limit > 0).ToDictionary(r => r.Item, r => r.Limit!.Value);
        _settings.Nodes = _resources.Where(r => r.Impure > 0 || r.Normal > 0 || r.Pure > 0)
            .ToDictionary(r => r.Item, r => new NodeCounts { Impure = r.Impure, Normal = r.Normal, Pure = r.Pure });
        foreach (var r in _resources) r.Tier = _settings.MaxTier;
    }

    void Recalculate()
    {
        if (_loading) return;
        RefreshBeltChoices(); // tier may have changed
        ReadUi();
        Plan plan;
        try { plan = Plan.Build(_settings); }
        catch (Exception ex) { WarningText.Text = Loc.T("error", ex.Message); return; }

        MachinesGrid.ItemsSource = plan.Machines;
        ResourcesGrid.ItemsSource = plan.Resources;
        OutputsGrid.ItemsSource = plan.Outputs;

        TotalsGrid.ItemsSource = plan.Totals;
        SplitGrid.ItemsSource = plan.Splits;
        GuardGrid.ItemsSource = plan.Guards;
        int requiredGuards = plan.Guards.Count(g => g.Level == GuardLevel.Required);
        GuardTab.Header = requiredGuards > 0 ? Loc.T("tab.guardsWarn", requiredGuards) : Loc.T("tab.guardsCount", plan.Guards.Count);
        SplitHint.Text = Loc.T(_settings.Mode == RoundingMode.Drained ? "split.hint" : "split.onlyDrained")
            + " " + Loc.T("belt.current", _settings.BeltLabel("Desc_IronPlate_C"), _settings.BeltLabel("Desc_Water_C"));
        _lastPlan = plan;
        _layoutDirty = true;
        if (MainTabs.SelectedItem == LayoutTab) RenderLayout();
        try { Tree.Render(plan); }
        catch (Exception ex) { Tree.Children.Clear(); plan.Warnings.Add(Loc.T("error", ex.Message)); }
        var scaleText = Math.Abs(plan.Scale - 1) < 1e-9 ? "" : Loc.T("summary.scale", plan.Scale);
        SummaryText.Text = (_settings.CountExtractors
            ? Loc.T("summary.withExtractors", plan.ProductionMachines + plan.Extractors, plan.Extractors, plan.Power)
            : Loc.T("summary", plan.ProductionMachines, plan.Power)) + scaleText
            + (plan.SplitterParts + plan.MergerParts + plan.JunctionParts > 0
                ? Loc.T("summary.logistics", plan.SplitterParts, plan.MergerParts, plan.JunctionParts) : "")
            + (plan.ValveParts > 0 ? Loc.T("summary.valves", plan.ValveParts) : "");
        _active.Summary = Loc.T("summary", plan.ProductionMachines + (_settings.CountExtractors ? plan.Extractors : 0), plan.Power).Replace("   ", " ");
        TierLabel.Text = Loc.T("tier", _settings.MaxTier);
        WarningText.Text = string.Join("\n", plan.Warnings.Distinct());
        UpdateSaveUi();
        _ = RefreshSuggestions();
    }

    CancellationTokenSource? _suggestCts;

    /// <summary>Runs the unlock analysis in the background; newer recalculations cancel older ones.</summary>
    async Task RefreshSuggestions()
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        var snapshot = _settings.Clone();
        UnlockSummary.Text = Loc.T("analysing");
        try
        {
            var (summary, notPossible, list) = await Task.Run(() => Unlocks.Suggest(snapshot, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            UnlockSummary.Text = summary;
            UnlockGrid.ItemsSource = list;
            UnlockTab.Header = list.Any(l => l.IsRequired) || notPossible
                ? Loc.T("tab.unlockWarn") : Loc.T("tab.unlockCount", list.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UnlockSummary.Text = Loc.T("analysisFailed", ex.Message); }
    }

    void UseSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseSave = UseSaveToggle.IsChecked == true;
        Recalculate();
    }

    void UpdateSaveUi()
    {
        UseSaveToggle.Visibility = _settings.Unlocked != null ? Visibility.Visible : Visibility.Collapsed;
        UseSaveToggle.IsChecked = _settings.UseSave;
        bool fromSave = _settings.SaveActive;
        SaveText.Opacity = fromSave ? 1 : 0.55;
        SaveText.Text = _settings.Unlocked != null ? Loc.T("save.label", _settings.SaveName!, _settings.Unlocked!.Count(r => r.Contains("Alternate"))) : "";
        ForgetSaveButton.Visibility = _settings.Unlocked != null ? Visibility.Visible : Visibility.Collapsed;
        AltCheck.IsEnabled = MamCheck.IsEnabled = !fromSave;
        AltCheck.ToolTip = MamCheck.ToolTip = fromSave ? Loc.T("save.tooltip") : null;
    }

    void LoadSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("save.dialogTitle"), Filter = Loc.T("save.filter"),
            InitialDirectory = Directory.Exists(SaveReader.DefaultFolder) ? SaveReader.DefaultFolder : null
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var info = SaveReader.Read(dlg.FileName);
            _settings.Unlocked = info.Recipes;
            _settings.SaveName = info.Name;
            _settings.UseSave = true;
            _loading = true;
            TierSlider.Value = info.Tier;
            _loading = false;
            StatusText.Text = Loc.T("save.loaded", info.Name, info.Recipes.Count, info.Tier);
            Recalculate();
        }
        catch (Exception ex) { MessageBox.Show(this, Loc.T("save.error", ex.Message), Loc.T("btn.loadSave")); }
        finally { Mouse.OverrideCursor = null; }
    }

    void ForgetSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Unlocked = null;
        _settings.SaveName = null;
        Recalculate();
    }

    /// <summary>
    /// Only react to a real user pick. When the grid is rebuilt, recycled combo boxes fire SelectionChanged
    /// with stale values — reacting to those caused an endless recalculate loop (the "stall").
    /// </summary>
    void Recipe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || !(cb.IsDropDownOpen || cb.IsKeyboardFocusWithin)) return;
        if (cb.DataContext is not MachineRow row || cb.SelectedItem is not RecipeDef r || r == row.Recipe) return;
        // pin by the recipe's main product so the manual solver and optimizer agree
        _settings.RecipeOverrides[row.Item] = r.ClassName;
        cb.IsDropDownOpen = false;
        Dispatcher.BeginInvoke(Recalculate);
    }

    // ---------- plan tabs ----------

    /// <summary>First run / older settings: turn the single plan into "Plan 1"; give old tabs their own full settings.</summary>
    void InitPlans()
    {
        if (_settings.Plans.Count == 0)
            _settings.Plans.Add(new PlanTab
            {
                Name = Loc.T("tabs.plan", 1), Targets = _settings.Targets, RecipeOverrides = _settings.RecipeOverrides,
                Optimize = _settings.Optimize, Mode = _settings.Mode
            });
        foreach (var p in _settings.Plans)
        {
            if (p.State == null)
            {
                // older file: world settings were shared, so start each tab from them plus its own plan
                var st = _settings.TabState();
                st.Targets = p.Targets.Select(x => new TargetSpec { Item = x.Item, Rate = x.Rate }).ToList();
                st.RecipeOverrides = new(p.RecipeOverrides);
                st.Optimize = p.Optimize;
                st.Mode = p.Mode;
                p.State = st;
            }
            _plans.Add(p);
        }
        _active = _plans[Math.Clamp(_settings.ActivePlan, 0, _plans.Count - 1)];
        _active.IsActive = true;
        CopyToSettings(_active);
        PlanTabsList.ItemsSource = _plans;
    }

    /// <summary>Make the tab's settings the working settings, keeping the global ones (save, language, tabs).</summary>
    void CopyToSettings(PlanTab t)
    {
        var st = (t.State ?? _settings.TabState()).Clone();
        st.Plans = _settings.Plans;
        st.ActivePlan = _settings.ActivePlan;
        st.Unlocked = _settings.Unlocked;
        st.SaveName = _settings.SaveName;
        st.UseSave = _settings.UseSave;
        st.Language = _settings.Language;
        st.CountExtractors = _settings.CountExtractors;
        st.Targets = st.Targets.Where(x => x.Item != null).Select(x => new TargetSpec { Item = x.Item, Rate = x.Rate }).ToList();
        _settings = st;
    }

    /// <summary>Write the open tab's left-panel state back into it (a copy, so rebinding can't touch it).</summary>
    void StoreActive()
    {
        ReadUi();
        var st = _settings.TabState();
        st.Targets = st.Targets.Where(x => x.Item != null).ToList();
        _active.State = st;
        // keep the legacy fields in step too (older versions of the app read these)
        _active.Targets = st.Targets.Select(x => new TargetSpec { Item = x.Item, Rate = x.Rate }).ToList();
        _active.RecipeOverrides = new(st.RecipeOverrides);
        _active.Optimize = st.Optimize;
        _active.Mode = st.Mode;
    }

    void SwitchTo(PlanTab t)
    {
        if (t == _active) return;
        StoreActive();
        _active.IsActive = false;
        _active = t;
        t.IsActive = true;
        CopyToSettings(t);
        _loading = true;
        ApplySettingsToUi(); // the whole left panel follows the tab
        RefreshBeltChoices();
        _loading = false;
        Recalculate();
    }

    string UniqueName(string name)
    {
        var n = name; int i = 2;
        while (_plans.Any(p => p.Name == n)) n = $"{name} {i++}";
        return n;
    }

    static PlanTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as PlanTab;

    void PlanTab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is not { } t) return;
        if (e.ClickCount == 2) { Rename(t); return; }
        SwitchTo(t);
    }

    void PlanTabNew_Click(object sender, RoutedEventArgs e)
    {
        StoreActive();
        int n = 1;
        while (_plans.Any(p => p.Name == Loc.T("tabs.plan", n))) n++;
        // a new factory starts from this tab's world settings (tier, nodes, belts…) with an empty plan
        var st = _settings.TabState();
        st.Targets = [new TargetSpec { Item = "Desc_IronPlate_C", Rate = 10 }];
        st.RecipeOverrides = new();
        st.Optimize = false;
        var t = new PlanTab { Name = Loc.T("tabs.plan", n), State = st, Targets = st.Targets };
        _plans.Add(t);
        SwitchTo(t);
    }

    void PlanTabDuplicate_Click(object sender, RoutedEventArgs e)
    {
        StoreActive();
        var src = TabOf(sender) ?? _active;
        var t = src.Copy(UniqueName(Loc.T("tabs.copy", src.Name)));
        _plans.Insert(_plans.IndexOf(src) + 1, t);
        SwitchTo(t);
    }

    void PlanTabClose_Click(object sender, RoutedEventArgs e)
    {
        var t = TabOf(sender) ?? _active;
        if (_plans.Count <= 1) return; // always keep one plan
        if (MessageBox.Show(this, Loc.T("tabs.confirmClose", t.Name), Loc.T("tabs.close"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        int i = _plans.IndexOf(t);
        if (t == _active) SwitchTo(_plans[i == _plans.Count - 1 ? i - 1 : i + 1]);
        _plans.Remove(t);
    }

    void PlanTabRename_Click(object sender, RoutedEventArgs e) => Rename(TabOf(sender) ?? _active);

    void Rename(PlanTab t)
    {
        var box = new TextBox { Text = t.Name, Margin = new Thickness(0, 0, 0, 10), MinWidth = 260 };
        var ok = new Button { Content = Loc.T("ok"), IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("cancel"), IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var dlg = new Window
        {
            Title = Loc.T("tabs.renameTitle"), Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(14), Children = { box, buttons } }
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text)) t.Name = box.Text.Trim();
    }

    void ClearPins_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecipeOverrides.Clear();
        Recalculate();
    }

    void Input_Changed(object sender, RoutedEventArgs e) => Recalculate();

    // ---------- factory layout ----------
    Plan? _lastPlan;
    Layout? _layout;
    bool _layoutDirty = true;

    /// <summary>The layout is only generated/drawn while its tab is open.</summary>
    void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs && MainTabs.SelectedItem == LayoutTab && _layoutDirty) RenderLayout();
    }

    int _layoutRun; // newest layout request: an older one finishing late is dropped

    /// <summary>Machines crossing a blueprint tile border (placed by hand when that's allowed).</summary>
    static int HandPlaced(Layout l, Settings s)
    {
        if (s.BlueprintTile <= 0 || !s.HandPlaceAcrossTiles) return 0;
        double t = s.BlueprintTile * Layout.Foundation;
        bool Across(double a, double z) => Math.Floor((a + 0.01) / t) != Math.Floor((z - 0.01) / t);
        return l.Buildings.Count(b => b.Kind == "machine" && (Across(b.X, b.X + b.W) || Across(b.Y, b.Y + b.H)));
    }

    /// <summary>Builds the layout on a background thread (place &amp; route can take up to ~30 s) with a spinner over the view.</summary>
    async void RenderLayout(bool force = false)
    {
        if (_lastPlan == null) return;
        int run = ++_layoutRun;
        var plan = _lastPlan;
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(_settings))!; // a snapshot: edits meanwhile don't touch the running build
        _layoutDirty = false;
        // an unchanged plan with the same layout options: the saved layout, no planning
        string planHash = LayoutCache.PlanHash(plan, settings), optKey = LayoutCache.Key(settings);
        string tabId = _active.Id, tabName = _active.Name;
        if (!force && LayoutCache.Load(planHash, optKey) is { } cached)
        {
            LayoutBusy.Visibility = Visibility.Collapsed; // (a build still running is superseded)
            ShowLayout(cached, settings, planHash);
            LayoutInfo.Text += "  ·  " + Loc.T("layout.fromCache");
            return;
        }
        LayoutBusy.Visibility = Visibility.Visible;
        LayoutStep.Text = ""; LayoutElapsed.Text = "";
        // the step the planner is on (from its thread) and the time so far, so a long layout doesn't look stuck
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        tick.Tick += (_, _) => { if (run == _layoutRun) LayoutElapsed.Text = Loc.T("status.elapsed", (int)watch.Elapsed.TotalSeconds); };
        tick.Start();
        void Step(string text) => Dispatcher.BeginInvoke(() => { if (run == _layoutRun) LayoutStep.Text = text; });
        try
        {
            var built = await System.Threading.Tasks.Task.Run(() => { Layout.Status = Step; try { return Layout.Build(plan, settings); } finally { Layout.Status = null; } });
            tick.Stop();
            // saved with the plan it came from (a different plan makes a different factory)
            LayoutCache.Save(new LayoutCache.Meta
            {
                PlanHash = planHash, TabId = tabId, Tab = tabName, Plan = settings.TabState(), PlanSummary = PlanSummary(plan, settings),
                Floors = settings.Floors, BlueprintTile = settings.BlueprintTile, HandPlace = settings.BlueprintTile > 0 && settings.HandPlaceAcrossTiles, IndustrialTank = settings.IndustrialFluidBox,
                Created = DateTime.Now, Blueprints = settings.BlueprintTile > 0 ? (int)Layout.BlueprintCount(built, settings) : 0, Belt = built.BeltLength,
                BuildSeconds = watch.Elapsed.TotalSeconds,
            }, built);
            if (run != _layoutRun) return; // superseded
            ShowLayout(built, settings, planHash);
        }
        catch (Exception ex) { if (run == _layoutRun) { LayoutCanvas.Children.Clear(); LayoutInfo.Text = Loc.T("error", ex.Message); } }
        finally { tick.Stop(); if (run == _layoutRun) LayoutBusy.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Draws a layout (built or from the cache) and lists this plan's cached layouts.</summary>
    void ShowLayout(Layout built, Settings settings, string planHash)
    {
        {
            _layout = built;
            // floor picker: "all" plus one entry per floor used
            int keep = FloorViewCombo.SelectedIndex;
            bool was = _loading; _loading = true;
            FloorViewCombo.Items.Clear();
            FloorViewCombo.Items.Add(Loc.T("floor.all"));
            for (int f = 0; f < _layout.Floors; f++) FloorViewCombo.Items.Add($"F{f} · {_layout.FloorElevation[f]:0} m");
            FloorViewCombo.SelectedIndex = keep >= 0 && keep < FloorViewCombo.Items.Count ? keep : (_layout.Floors > 1 ? 1 : 0);
            FloorViewCombo.IsEnabled = _layout.Floors > 1;
            _loading = was;
            LayoutCanvas.Render(_layout, FloorViewCombo.SelectedIndex - 1);
            LayoutInfo.Text = Loc.T("layout.info", _layout.Width / Layout.Foundation, _layout.Height / Layout.Foundation, _layout.Width, _layout.Height, _layout.Rows, _layout.Lanes)
                              + Loc.T("layout.belts", _layout.BeltLength, _layout.RowLength)
                              + (_layout.Copies > 1 ? "  ·  " + Loc.T("layout.copies", _layout.Copies) : "")
                              + (HandPlaced(_layout, settings) is var nh && nh > 0 ? "  ·  " + Loc.T("layout.byHand", nh) : "");
        }
        FillCacheList(planHash, LayoutCache.Key(settings));
    }

    // ---------- cached layouts of this tab (every plan it laid out, every option set) ----------
    string? _cachePlan;
    List<LayoutCache.Meta> _cacheList = new();

    static string PlanSummary(Plan plan, Settings s) =>
        string.Join(", ", s.Targets.Where(t => t.Item != null).Select(t => $"{GameData.Item(t.Item).Name} {t.Rate:0.##}"))
        + " · " + Loc.T("cache.machines", plan.Nodes.Where(n => n.Kind == NodeKind.Machine).Sum(n => n.Machines));

    void FillCacheList(string planHash, string current)
    {
        _cachePlan = planHash;
        _cacheList = LayoutCache.ForTab(_active.Id);
        bool was = _loading; _loading = true;
        CacheCombo.Items.Clear();
        foreach (var e in _cacheList)
        {
            string bp = e.BlueprintTile switch { 5 => Loc.T("bp.mk2"), 6 => Loc.T("bp.mk3"), _ => Loc.T("bp.off") };
            string size = e.BlueprintTile > 0 ? Loc.T("cache.blueprints", e.Blueprints) : "";
            var item = new ComboBoxItem { ToolTip = e.PlanSummary };
            item.Content = new TextBlock
            {
                // this plan's layouts in full, other plans (restored with their plan when picked) dimmed with their targets
                Text = (e.PlanHash == planHash ? "" : "↩ " + e.PlanSummary + "  —  ") + Loc.T("cache.entry", e.Floors, bp, e.HandPlace ? "  ✋" : "", size, e.Belt, e.Created),
                Foreground = e.PlanHash == planHash ? SystemColors.ControlTextBrush : System.Windows.Media.Brushes.Gray,
            };
            if (e.IndustrialTank) ((TextBlock)item.Content).Text += "  ·  " + Loc.T("cache.bigTank");
            CacheCombo.Items.Add(item);
        }
        CacheCombo.SelectedIndex = _cacheList.FindIndex(e => e.PlanHash == planHash && e.OptionsKey == current);
        CacheCombo.IsEnabled = _cacheList.Count > 0;
        _loading = was;
    }

    /// <summary>A cached layout picked: its layout options — and, for another plan, that plan — come back; the layout
    /// is then shown from the cache.</summary>
    void CacheCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CacheCombo.SelectedIndex < 0 || CacheCombo.SelectedIndex >= _cacheList.Count) return;
        var c = _cacheList[CacheCombo.SelectedIndex];
        if (c.PlanHash == _cachePlan && c.OptionsKey == LayoutCache.Key(_settings)) return;
        if (c.PlanHash != _cachePlan && c.Plan != null)
        {
            // another plan of this tab: the whole left panel goes back to it
            _active.State = c.Plan.Clone();
            CopyToSettings(_active);
            _loading = true;
            ApplySettingsToUi();
            RefreshBeltChoices();
            _loading = false;
            _layoutDirty = true;
            Recalculate();
            return;
        }
        bool was = _loading; _loading = true;
        _settings.Floors = c.Floors; _settings.BlueprintTile = c.BlueprintTile; _settings.HandPlaceAcrossTiles = c.HandPlace; _settings.IndustrialFluidBox = c.IndustrialTank;
        TankCheck.IsChecked = c.IndustrialTank;
        FloorsCombo.SelectedIndex = Math.Clamp(c.Floors, 1, 4) - 1;
        BlueprintCombo.SelectedIndex = c.BlueprintTile switch { 5 => 1, 6 => 2, _ => 0 };
        HandCheck.IsChecked = c.HandPlace; HandCheck.IsEnabled = c.BlueprintTile > 0;
        _loading = was;
        _layoutDirty = true;
        RenderLayout();
    }

    /// <summary>Lay out again, ignoring the saved layout (it's replaced).</summary>
    void Relayout_Click(object sender, RoutedEventArgs e) => RenderLayout(force: true);

    void FloorsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FloorsCombo.SelectedIndex < 0) return;
        _settings.Floors = FloorsCombo.SelectedIndex + 1;
        _layoutDirty = true;
        if (MainTabs.SelectedItem == LayoutTab) RenderLayout();
    }

    void BlueprintCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BlueprintCombo.SelectedIndex < 0) return;
        _settings.BlueprintTile = BlueprintCombo.SelectedIndex switch { 1 => 5, 2 => 6, _ => 0 };
        HandCheck.IsEnabled = _settings.BlueprintTile > 0;
        _layoutDirty = true;
        if (MainTabs.SelectedItem == LayoutTab) RenderLayout();
    }

    void TankCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.IndustrialFluidBox = TankCheck.IsChecked == true;
        _layoutDirty = true;
        if (MainTabs.SelectedItem == LayoutTab) RenderLayout();
    }

    void HandCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // turning it on: what it costs, confirmed once per click (off: no prompt)
        if (HandCheck.IsChecked == true && MessageBox.Show(this, Loc.T("hand.warn"), Loc.T("chk.byHand"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            HandCheck.IsChecked = false;
            return;
        }
        _settings.HandPlaceAcrossTiles = HandCheck.IsChecked == true;
        _layoutDirty = true;
        if (MainTabs.SelectedItem == LayoutTab) RenderLayout();
    }

    void FloorView_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _layout == null || FloorViewCombo.SelectedIndex < 0) return;
        LayoutCanvas.Render(_layout, FloorViewCombo.SelectedIndex - 1);
    }

    void LayoutScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        LayoutZoom.Value = Math.Clamp(LayoutZoom.Value * (e.Delta > 0 ? 1.1 : 1 / 1.1), LayoutZoom.Minimum, LayoutZoom.Maximum);
        e.Handled = true;
    }

    void FitLayout_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutCanvas.Width is not > 0 || LayoutCanvas.Height is not > 0) return;
        LayoutZoom.Value = Math.Clamp(Math.Min((LayoutScroll.ViewportWidth - 4) / LayoutCanvas.Width, (LayoutScroll.ViewportHeight - 4) / LayoutCanvas.Height), LayoutZoom.Minimum, LayoutZoom.Maximum);
    }

    void ExportLayoutPng_Click(object sender, RoutedEventArgs e)
    {
        if (_layout == null || LayoutCanvas.Width is not > 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = $"{_active.Name} layout.png" };
        if (dlg.ShowDialog(this) != true) return;
        // render at full scale, independent of the on-screen zoom (capped so huge layouts stay a sane file size)
        double scale = Math.Min(1, 12000 / Math.Max(LayoutCanvas.Width, LayoutCanvas.Height));
        var view = new LayoutView();
        view.Render(_layout, FloorViewCombo.SelectedIndex - 1);
        view.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(view.DesiredSize.Width), (int)Math.Ceiling(view.DesiredSize.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(view);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(dlg.FileName);
        enc.Save(fs);
        StatusText.Text = Loc.T("layout.saved", dlg.FileName);
    }

    void ExportLayoutCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_layout == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"{_active.Name} layout.csv" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, _layout.ToCsv(), new System.Text.UTF8Encoding(true));
        StatusText.Text = Loc.T("layout.saved", dlg.FileName);
    }

    /// <summary>
    /// Writes the layout's blueprints (one per tile, "&lt;tab&gt; r1c1" …) into a folder the player picks — normally
    /// the session's folder under SaveGames\blueprints. The writer is bundled in the exe (bp\write.js with its parser
    /// library) and runs on Node.js, which must be installed. A blueprint made in game in that folder supplies the
    /// session / player header the game checks. Also writes "&lt;tab&gt; wiring.txt": the joints to bridge by hand.
    /// </summary>
    async void ExportBlueprints_Click(object sender, RoutedEventArgs e)
    {
        if (_layout == null) return;
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(_settings))!;
        if (settings.BlueprintTile <= 0) { MessageBox.Show(this, Loc.T("bp.needTile"), Loc.T("btn.exportBp")); return; }
        // Node.js first: without it nothing can be written — the full build carries its own (bp\node\node.exe), the
        // standard one uses an installed Node.js
        // (off the UI thread: node's first start can take seconds, e.g. while it's being virus-scanned)
        string bundledBp = Path.Combine(AppContext.BaseDirectory, "bp");
        string bundledNode = Path.Combine(bundledBp, "node", "node.exe");
        string node = File.Exists(bundledNode) ? bundledNode : "node";
        ExportTitle.Text = Loc.T("bp.checkNode"); ExportCount.Text = ""; ExportBar.IsIndeterminate = true; ExportBusy.Visibility = Visibility.Visible;
        string? nodeVersion = await System.Threading.Tasks.Task.Run(() => RunTool(node, "--version", null).output);
        ExportBusy.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(nodeVersion))
        {
            if (MessageBox.Show(this, Loc.T("bp.noNode"), Loc.T("btn.exportBp"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://nodejs.org/") { UseShellExecute = true }); } catch { }
            return;
        }
        // the writer: bundled with its library (full build), else a copy in the app's data folder whose library npm
        // installs once (standard build: needs npm and the network the first time)
        string bpDir = bundledBp;
        if (!Directory.Exists(Path.Combine(bundledBp, "node_modules", "@etothepii", "satisfactory-file-parser")))
        {
            bpDir = Path.Combine(GameData.AppDir, "bp");
            try
            {
                Directory.CreateDirectory(bpDir);
                foreach (var f in new[] { "write.js", "templates.json", "package.json", "package-lock.json" })
                    if (File.Exists(Path.Combine(bundledBp, f))) File.Copy(Path.Combine(bundledBp, f), Path.Combine(bpDir, f), overwrite: true);
            }
            catch (Exception ex) { MessageBox.Show(this, Loc.T("error", ex.Message), Loc.T("btn.exportBp")); return; }
            if (!Directory.Exists(Path.Combine(bpDir, "node_modules", "@etothepii", "satisfactory-file-parser")))
            {
                ExportTitle.Text = Loc.T("bp.installing"); ExportCount.Text = ""; ExportBar.IsIndeterminate = true; ExportBusy.Visibility = Visibility.Visible;
                var npm = await System.Threading.Tasks.Task.Run(() => RunTool("cmd.exe", "/c npm ci --no-audit --no-fund", bpDir, 300000));
                ExportBusy.Visibility = Visibility.Collapsed;
                if (!Directory.Exists(Path.Combine(bpDir, "node_modules", "@etothepii", "satisfactory-file-parser")))
                {
                    MessageBox.Show(this, Loc.T("bp.installFailed", bpDir) + "\n\n" + (npm.error.Length > 600 ? npm.error[^600..] : npm.error), Loc.T("btn.exportBp"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        string writer = Path.Combine(bpDir, "write.js"), templates = Path.Combine(bpDir, "templates.json");
        if (!File.Exists(writer) || !File.Exists(templates)) { MessageBox.Show(this, Loc.T("bp.noWriter", writer), Loc.T("btn.exportBp")); return; }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames", "blueprints");
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("bp.pickFolder") };
        if (Directory.Exists(root)) dlg.InitialDirectory = root;
        if (dlg.ShowDialog(this) != true) return;
        string dir = dlg.FolderName;
        // the header comes from a blueprint of that session: one made in game, in this folder (or a folder above, up to the session's)
        string? baseBp = null;
        for (var d = new DirectoryInfo(dir); d != null && baseBp == null; d = d.Parent)
        {
            baseBp = d.GetFiles("*.sbp").Where(f => File.Exists(f.FullName + "cfg")).OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).FirstOrDefault();
            if (string.Equals(d.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase) || string.Equals(d.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (baseBp == null) { MessageBox.Show(this, Loc.T("bp.noBase", dir), Loc.T("btn.exportBp")); return; }
        string name = string.Concat(_active.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        if (name.Length == 0) name = "Factory";
        var layout = _layout;
        StatusText.Text = Loc.T("bp.writing");
        ExportBpButton.IsEnabled = false;
        ExportTitle.Text = Loc.T("bp.writing"); ExportCount.Text = ""; ExportBar.IsIndeterminate = true; ExportBusy.Visibility = Visibility.Visible;
        // progress: blueprints written of the total (reported from the writer's thread)
        var progress = new Progress<(int done, int total)>(q =>
        {
            ExportBar.IsIndeterminate = false; ExportBar.Maximum = Math.Max(1, q.total); ExportBar.Value = q.done;
            ExportCount.Text = Loc.T("bp.progress", q.done, q.total);
        });
        IProgress<(int, int)> report = progress;
        try
        {
            var (written, errors, warnings, joints) = await System.Threading.Tasks.Task.Run(() =>
            {
                var warn = new List<string>(); var jts = new List<BlueprintExport.Joint>();
                var specs = BlueprintExport.FromLayout(layout, settings, name, settings.BlueprintTile, baseBp[..^4], warn, jts);
                var tmp = Directory.CreateTempSubdirectory("sfplanner-bp");
                int ok = 0; var errs = new List<string>();
                report.Report((0, specs.Count));
                try
                {
                    foreach (var spec in specs)
                    {
                        var specPath = Path.Combine(tmp.FullName, $"spec{ok + errs.Count}.json");
                        File.WriteAllText(specPath, BlueprintExport.ToJson(spec));
                        var psi = new System.Diagnostics.ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(writer)! };
                        foreach (var a in new[] { writer, specPath, templates, dir }) psi.ArgumentList.Add(a);
                        using var p = System.Diagnostics.Process.Start(psi)!;
                        var outTask = p.StandardOutput.ReadToEndAsync(); var errText = p.StandardError.ReadToEnd();
                        p.WaitForExit(60000);
                        if (p.ExitCode == 0 && outTask.Result.Contains("\"ok\":true")) ok++;
                        else errs.Add($"{spec.name}: {errText.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "failed"}");
                        report.Report((ok + errs.Count, specs.Count));
                    }
                }
                finally { try { tmp.Delete(true); } catch { } }
                return (ok, errs, warn, jts);
            });
            // the joints to bridge by hand after placing (belts / pipes stop short of every tile border)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Loc.T("bp.wiringHead", name));
            int k = 0;
            foreach (var j in joints.OrderBy(j => j.z).ThenBy(j => -j.y).ThenBy(j => j.x))
                sb.AppendLine($"{++k,3}. {(j.item.Contains("Pipeline") ? "pipe" : "belt")} {j.tileA} -> {j.tileB}  ({j.x * Layout.Foundation:0} m, {j.y * Layout.Foundation:0} m, {j.z / 100:0} m)");
            foreach (var w in warnings) sb.AppendLine("! " + w);
            File.WriteAllText(Path.Combine(dir, $"{name} wiring.txt"), sb.ToString(), new System.Text.UTF8Encoding(true));
            StatusText.Text = Loc.T("bp.done", written, dir);
            MessageBox.Show(this, Loc.T("bp.summary", written, dir, joints.Count, Path.GetFileName(baseBp)) + (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(8)) : ""),
                Loc.T("btn.exportBp"), MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { StatusText.Text = Loc.T("error", ex.Message); }
        finally { ExportBpButton.IsEnabled = true; ExportBusy.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Runs a tool (hidden window), returning its output and error text ("" if it can't start).</summary>
    static (string output, string error) RunTool(string exe, string args, string? dir, int timeoutMs = 20000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (dir != null) psi.WorkingDirectory = dir;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return ("", "");
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (outp.Trim(), err.Result.Trim());
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    /// <summary>Ctrl + mouse wheel zooms the tree diagram.</summary>
    void TreeScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * (e.Delta > 0 ? 1.1 : 1 / 1.1), ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    void FitTree_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.Width is not > 0 || Tree.Height is not > 0) return;
        var fit = Math.Min((TreeScroll.ViewportWidth - 4) / Tree.Width, (TreeScroll.ViewportHeight - 4) / Tree.Height);
        ZoomSlider.Value = Math.Clamp(fit, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    /// <summary>Switch UI text (live via bindings) and reload game names from that language's wiki data.</summary>
    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedValue is not string lang || lang == Loc.Instance.Language) return;
        _settings.Language = Loc.Instance.Language = lang;
        GameData.Load(lang);
        RefreshIcons(); // rebuilds item lists with the new names and recalculates
    }

    void Rate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Recalculate();
    }

    void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        _targets.Add(new TargetSpec { Item = "Desc_IronPlate_C", Rate = 10 });
        Recalculate();
    }

    void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TargetSpec t) _targets.Remove(t);
        Recalculate();
    }

    async void UpdateFromWiki_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try { await UpdateFromWikiAsync(); }
        finally { btn.IsEnabled = true; }
    }

    /// <summary>Recipe / item data and icons from satisfactory.wiki.gg (into the user's data folder), then reloaded.</summary>
    async Task UpdateFromWikiAsync()
    {
        try
        {
            StatusText.Text = Loc.T("status.downloadingData");
            await WikiSync.DownloadDataAsync();
            GameData.Load(Loc.Instance.Language);
            _loading = true;
            PopulateLists();
            ApplySettingsToUi();
            _loading = false;
            StatusText.Text = Loc.T("status.downloadingIcons", "");
            var n = await WikiSync.DownloadImagesAsync(new Progress<string>(s => StatusText.Text = s), force: true);
            StatusText.Text = Loc.T("status.updated", GameData.Recipes.Count, n);
            RefreshIcons();
            Recalculate();
        }
        catch (Exception ex) { StatusText.Text = Loc.T("status.updateFailed", ex.Message); }
    }

    async Task FetchMissingIcons()
    {
        try
        {
            var n = await WikiSync.DownloadImagesAsync(new Progress<string>(s => StatusText.Text = s));
            StatusText.Text = n > 0 ? Loc.T("status.iconsDownloaded", n) : "";
            if (n > 0) RefreshIcons();
        }
        catch (Exception ex) { StatusText.Text = Loc.T("status.iconsFailed", ex.Message); }
    }

    void RefreshIcons()
    {
        _loading = true;
        // snapshot values: rebuilding the item list makes the target combo boxes write null back into their TargetSpec
        var targets = _targets.Select(t => new TargetSpec { Item = t.Item, Rate = t.Rate }).ToList();
        _targets.Clear();
        PopulateLists();
        MinerCombo.SelectedValue = _settings.Miner;
        BeltCombo.SelectedValue = _settings.BeltTier;
        PipeCombo.SelectedValue = _settings.PipeTier;
        foreach (var r in _resources)
        {
            r.Enabled = !_settings.DisabledResources.Contains(r.Item);
            r.Limit = _settings.ResourceLimits.TryGetValue(r.Item, out var l) ? l : null;
            var n = _settings.Nodes.GetValueOrDefault(r.Item);
            r.Impure = n?.Impure; r.Normal = n?.Normal; r.Pure = n?.Pure;
        }
        _targets.Clear();
        foreach (var t in targets) _targets.Add(t);
        _loading = false;
        Recalculate();
    }

    void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception) { _settings = new(); }
    }

    void SaveSettings()
    {
        try
        {
            StoreActive();
            _settings.Plans = _plans.ToList();
            _settings.ActivePlan = _plans.IndexOf(_active);
            Directory.CreateDirectory(GameData.AppDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}

public class ResourceAccess : System.ComponentModel.INotifyPropertyChanged
{
    public required string Item { get; init; }
    public System.Windows.Media.ImageSource? Icon => ImageCache.Get(Item);
    bool _enabled = true;
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); Raise(nameof(PurityEnabled)); } }
    public double? Limit { get; set; }
    int? _impure, _normal, _pure;
    public int? Impure { get => _impure; set { _impure = value; Raise(nameof(Impure)); } }
    public int? Normal { get => _normal; set { _normal = value; Raise(nameof(Normal)); } }
    public int? Pure { get => _pure; set { _pure = value; Raise(nameof(Pure)); } }
    public bool PurityEnabled => Enabled && Settings.HasPurity(Item);
    public Visibility PurityVisibility => Settings.HasPurity(Item) ? Visibility.Visible : Visibility.Hidden;
    int _tier = 9;
    public int Tier { set { _tier = value; Raise(nameof(Label)); } }
    public string Label => GameData.Item(Item).Name +
        (Settings.ResourceTier(Item) > _tier ? Loc.T("needsTier", Settings.ResourceTier(Item)) : "");
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
