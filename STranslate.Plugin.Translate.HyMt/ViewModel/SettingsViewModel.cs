using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ObservableCollections;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace STranslate.Plugin.Translate.HyMt.ViewModel;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string GlossaryApiUrl = "https://tokenhub.tencentmaas.com/v1/api/glossaries";
    private static readonly SemaphoreSlim GlossaryRateLimiter = new(1, 1);
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private bool _isUpdating = false;

    public SettingsViewModel(IPluginContext context, Settings settings)
    {
        _context = context;
        _settings = settings;

        ApiKey = settings.ApiKey;
        Model = settings.Model;
        Models = [.. settings.Models];
        IsEnableTerms = settings.IsEnableTerms;
        IsEnableDomains = settings.IsEnableDomains;
        Domains = settings.Domains;
        IsEnableStyle = settings.IsEnableStyle;
        Style = settings.Style;
        IsEnableGlossary = settings.IsEnableGlossary;
        GlossaryIds = settings.GlossaryIds;
        _glossaryItems = [.. settings.Glossaries];
        Glossaries = _glossaryItems.ToNotifyCollectionChanged();
        _items = [.. settings.Terms];
        Terms = _items.ToNotifyCollectionChanged();
        _glossaryTerms = [.. settings.GlossaryTerms];
        GlossaryTerms = _glossaryTerms.ToNotifyCollectionChanged();

        PropertyChanged += OnPropertyChanged;
        Models.CollectionChanged += OnModelsCollectionChanged;
        _items.CollectionChanged += OnTermsCollectionChanged;
        _glossaryTerms.CollectionChanged += OnGlossaryTermsCollectionChanged;
        _glossaryItems.CollectionChanged += OnGlossariesCollectionChanged;

        foreach (var item in _items)
        {
            item.PropertyChanged += OnTermPropertyChanged;
        }
        foreach (var item in _glossaryTerms) item.PropertyChanged += OnGlossaryTermPropertyChanged;
        foreach (var item in _glossaryItems)
        {
            item.PropertyChanged += OnGlossaryPropertyChanged;
        }
    }

    private void OnTermPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 当 Term 的属性发生变化时保存设置
        _settings.Terms = [.. _items];
        _context.SaveSettingStorage<Settings>();
    }

    private void OnTermsCollectionChanged(in NotifyCollectionChangedEventArgs<Term> e)
    {
        e.NewItem?.PropertyChanged += OnTermPropertyChanged;
        e.OldItem?.PropertyChanged -= OnTermPropertyChanged;
        foreach (var item in e.NewItems)
        {
            item.PropertyChanged += OnTermPropertyChanged;
        }
        foreach (var item in e.OldItems)
        {
            item.PropertyChanged -= OnTermPropertyChanged;
        }
        _settings.Terms = [.. _items];
        _context.SaveSettingStorage<Settings>();
    }

    private void OnModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or
                       NotifyCollectionChangedAction.Remove or
                       NotifyCollectionChangedAction.Replace)
        {
            _settings.Models = [.. Models];
            _context.SaveSettingStorage<Settings>();
        }
    }

    private void OnGlossariesCollectionChanged(in NotifyCollectionChangedEventArgs<Glossary> e)
    {
        foreach (var item in e.NewItems) item.PropertyChanged += OnGlossaryPropertyChanged;
        foreach (var item in e.OldItems) item.PropertyChanged -= OnGlossaryPropertyChanged;
        SaveGlossaries();
    }

    private void OnGlossaryPropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveGlossaries();

    private void OnGlossaryTermPropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveGlossaryTerms();

    private void OnGlossaryTermsCollectionChanged(in NotifyCollectionChangedEventArgs<Term> e)
    {
        foreach (var item in e.NewItems) item.PropertyChanged += OnGlossaryTermPropertyChanged;
        foreach (var item in e.OldItems) item.PropertyChanged -= OnGlossaryTermPropertyChanged;
        SaveGlossaryTerms();
    }

    private void SaveGlossaryTerms()
    {
        _settings.GlossaryTerms = [.. _glossaryTerms];
        _context.SaveSettingStorage<Settings>();
    }

    private void SaveGlossaries()
    {
        _settings.Glossaries = [.. _glossaryItems];
        _settings.GlossaryIds = string.Join(",", _glossaryItems.Where(g => g.IsEnabled && !string.IsNullOrWhiteSpace(g.Id)).Select(g => g.Id));
        GlossaryIds = _settings.GlossaryIds;
        _context.SaveSettingStorage<Settings>();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApiKey):
                _settings.ApiKey = ApiKey;
                break;
            case nameof(Model):
                _settings.Model = Model ?? string.Empty;
                break;
            case nameof(IsEnableTerms):
                _settings.IsEnableTerms = IsEnableTerms;
                break;
            case nameof(IsEnableDomains):
                _settings.IsEnableDomains = IsEnableDomains;
                break;
            case nameof(Domains):
                _settings.Domains = Domains;
                break;
            case nameof(IsEnableStyle): _settings.IsEnableStyle = IsEnableStyle; break;
            case nameof(Style): _settings.Style = Style; break;
            case nameof(IsEnableGlossary): _settings.IsEnableGlossary = IsEnableGlossary; break;
            case nameof(GlossaryIds): _settings.GlossaryIds = GlossaryIds; break;
            default:
                return;
        }
        _context.SaveSettingStorage<Settings>();
    }

    [RelayCommand]
    private void AddModel(string model)
    {
        if (_isUpdating || string.IsNullOrWhiteSpace(model) || Models.Contains(model))
            return;

        using var _ = new UpdateGuard(this);

        Models.Add(model);
        Model = model;
    }

    [RelayCommand]
    private void DeleteModel(string model)
    {
        if (_isUpdating || !Models.Contains(model))
            return;

        using var _ = new UpdateGuard(this);

        if (Model == model)
            Model = Models.Count > 1 ? Models.First(m => m != model) : string.Empty;

        Models.Remove(model);
    }

    [RelayCommand]
    private void TermsAdd()
    {
        _items.Add(new Term
        {
            SourceText = string.Empty,
            TargetText = string.Empty
        });
    }

    [RelayCommand]
    private void TermsDelete(IList list)
    {
        if (list.Count == 0)
            return;

        var tmp = list.Cast<Term>().ToList();

        foreach (var item in tmp)
        {
            _items.Remove(item);
        }
    }

    [RelayCommand]
    private void TermsClear()
    {
        if (_items.Count == 0)
            return;

        _items.Clear();
    }

    [RelayCommand]
    private void GlossaryAdd() => _glossaryItems.Add(new Glossary { Name = "新术语库" });

    [RelayCommand]
    private void GlossaryDelete(IList list)
    {
        foreach (var item in list.Cast<Glossary>().ToList()) _glossaryItems.Remove(item);
    }

    [RelayCommand]
    private void GlossaryExport()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = "hymt_glossaries.json",
                DefaultExt = "json"
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_glossaryItems, options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex, $"Failed to export glossaries: {ex.Message}");
        }
    }

    [RelayCommand]
    private void GlossaryImport()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            var imported = JsonSerializer.Deserialize<IEnumerable<Glossary>>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            if (imported == null) return;
            _glossaryItems.Clear();
            _glossaryItems.AddRange(imported);
        }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex, $"Failed to import glossaries: {ex.Message}");
        }
    }

    [RelayCommand]
    private void GlossaryTermAdd() => _glossaryTerms.Add(new Term());

    [RelayCommand]
    private void GlossaryTermDelete(IList list)
    {
        foreach (var item in list.Cast<Term>().ToList()) _glossaryTerms.Remove(item);
    }

    [RelayCommand]
    private void GlossaryTermsExport()
    {
        var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", FileName = "hymt_glossary_terms.json", DefaultExt = "json" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_glossaryTerms, options), Encoding.UTF8);
    }

    [RelayCommand]
    private void GlossaryTermsImport()
    {
        var dialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        var imported = JsonSerializer.Deserialize<IEnumerable<Term>>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
        if (imported == null) return;
        _glossaryTerms.Clear();
        _glossaryTerms.AddRange(imported);
    }

    [RelayCommand]
    private async Task SaveGlossariesToBackend()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            _context.Logger.LogWarning("Cannot save glossary without API key.");
            return;
        }

        var entries = _glossaryItems.Where(g => !string.IsNullOrWhiteSpace(g.Id)).ToList();
        var terms = _glossaryTerms.Where(t => !string.IsNullOrWhiteSpace(t.SourceText) && !string.IsNullOrWhiteSpace(t.TargetText))
            .Select(t => new { source = t.SourceText, target = t.TargetText }).ToArray();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        foreach (var glossary in entries)
        {
            await GlossaryRateLimiter.WaitAsync();
            try
            {
                var payload = new { name = glossary.Name, terms };
                using var response = await client.PutAsJsonAsync($"{GlossaryApiUrl}/{Uri.EscapeDataString(glossary.Id)}", payload);
                response.EnsureSuccessStatusCode();
            }
            finally
            {
                _ = Task.Delay(50).ContinueWith(_ => GlossaryRateLimiter.Release());
            }
        }
    }

    [RelayCommand]
    private void TermsExport()
    {
        try
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = "hymt_terms.json",
                DefaultExt = "json"
            };

            if (saveFileDialog.ShowDialog() != true)
                return;

            var json = JsonSerializer.Serialize(_items, options);

            File.WriteAllText(saveFileDialog.FileName, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex, $"Failed to export terms: {ex.Message}");
        }
    }

    [RelayCommand]
    private void TermsImport()
    {
        try
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            var json = File.ReadAllText(openFileDialog.FileName, Encoding.UTF8);
            var terms = JsonSerializer.Deserialize<IEnumerable<Term>>(json);

            if (terms != null)
            {
                _items.Clear();
                _items.AddRange(terms);
            }
        }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex, $"Failed to import terms: {ex.Message}");
        }
    }

    public void Dispose()
    {
        PropertyChanged -= OnPropertyChanged;
        Models.CollectionChanged -= OnModelsCollectionChanged;
        _items.CollectionChanged -= OnTermsCollectionChanged;
        _glossaryTerms.CollectionChanged -= OnGlossaryTermsCollectionChanged;
        _glossaryItems.CollectionChanged -= OnGlossariesCollectionChanged;

        foreach (var item in _items)
        {
            item.PropertyChanged -= OnTermPropertyChanged;
        }
        foreach (var item in _glossaryTerms) item.PropertyChanged -= OnGlossaryTermPropertyChanged;
        foreach (var item in _glossaryItems)
        {
            item.PropertyChanged -= OnGlossaryPropertyChanged;
        }
    }

    private static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly struct UpdateGuard : IDisposable
    {
        private readonly SettingsViewModel _viewModel;

        public UpdateGuard(SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel._isUpdating = true;
        }

        public void Dispose() => _viewModel._isUpdating = false;
    }

    [ObservableProperty] public partial string ApiKey { get; set; }

    [ObservableProperty] public partial string Model { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<string> Models { get; set; }

    [ObservableProperty] public partial bool IsEnableTerms { get; set; }

    [ObservableProperty] public partial bool IsEnableDomains { get; set; }

    [ObservableProperty] public partial bool IsEnableStyle { get; set; }

    [ObservableProperty] public partial string Style { get; set; }

    [ObservableProperty] public partial bool IsEnableGlossary { get; set; }

    [ObservableProperty] public partial string GlossaryIds { get; set; }

    /// <summary>
    ///     术语列表
    /// </summary>
    private readonly ObservableList<Term> _items;

    private readonly ObservableList<Glossary> _glossaryItems;

    private readonly ObservableList<Term> _glossaryTerms;

    public INotifyCollectionChangedSynchronizedViewList<Term> Terms { get; }

    public INotifyCollectionChangedSynchronizedViewList<Glossary> Glossaries { get; }

    public INotifyCollectionChangedSynchronizedViewList<Term> GlossaryTerms { get; }

    /// <summary>
    ///     领域提示
    /// </summary>
    [ObservableProperty] public partial string Domains { get; set; }
}
