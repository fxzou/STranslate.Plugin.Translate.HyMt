using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ObservableCollections;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Security.Cryptography;

namespace STranslate.Plugin.Translate.HyMt.ViewModel;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string OpenApiHost = "tokenhub.tencentcloudapi.com";
    private const string OpenApiService = "tokenhub";
    private const string OpenApiVersion = "2026-03-22";
    private static readonly SemaphoreSlim GlossaryRateLimiter = new(1, 1);
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private int _updateDepth;
    private bool IsUpdating => _updateDepth > 0;

    public SettingsViewModel(IPluginContext context, Settings settings)
    {
        _context = context;
        _settings = settings;

        ApiKey = settings.ApiKey;
        SecretId = settings.SecretId;
        SecretKey = settings.SecretKey;
        Region = settings.Region;
        Model = settings.Model;
        Models = [.. settings.Models];
        IsEnableTerms = settings.IsEnableTerms;
        IsEnableStyle = settings.IsEnableStyle;
        Style = settings.Style;
        ChineseSourceStyle = settings.ChineseSourceStyle;
        ChineseTargetStyle = settings.ChineseTargetStyle;
        IsEnableGlossary = settings.IsEnableGlossary;
        GlossaryIds = settings.GlossaryIds;
        _glossaryItems = [.. settings.Glossaries];
        Glossaries = _glossaryItems.ToNotifyCollectionChanged();
        _items = [.. settings.Terms];
        Terms = _items.ToNotifyCollectionChanged();
        _glossaryTerms = [];
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
        foreach (var item in _glossaryItems)
        {
            item.PropertyChanged += OnGlossaryPropertyChanged;
        }
        SelectedGlossary = _glossaryItems.FirstOrDefault();
        _ = RefreshGlossariesFromBackend();
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
        if (IsUpdating) return;
        SaveGlossaries();
    }

    private void OnGlossaryPropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveGlossaries();

    private void OnGlossaryTermPropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveGlossaryTerms();

    private void OnGlossaryTermsCollectionChanged(in NotifyCollectionChangedEventArgs<Term> e)
    {
        foreach (var item in e.NewItems) item.PropertyChanged += OnGlossaryTermPropertyChanged;
        foreach (var item in e.OldItems) item.PropertyChanged -= OnGlossaryTermPropertyChanged;
        if (IsUpdating) return;
        SaveGlossaryTerms();
    }

    private void SaveGlossaryTerms()
    {
        if (SelectedGlossary == null) return;
        SelectedGlossary.Terms = [.. _glossaryTerms];
        SaveGlossaries();
    }

    partial void OnSelectedGlossaryChanged(Glossary? value)
    {
        using var guard = new UpdateGuard(this);
        foreach (var item in _glossaryTerms)
            item.PropertyChanged -= OnGlossaryTermPropertyChanged;
        _glossaryTerms.Clear();
        if (value != null)
        {
            _glossaryTerms.AddRange(value.Terms ?? []);
        }
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
            case nameof(SecretId): _settings.SecretId = SecretId; break;
            case nameof(SecretKey): _settings.SecretKey = SecretKey; break;
            case nameof(Region): _settings.Region = Region; break;
            case nameof(Model):
                _settings.Model = Model ?? string.Empty;
                break;
            case nameof(IsEnableTerms):
                _settings.IsEnableTerms = IsEnableTerms;
                break;
            case nameof(IsEnableStyle): _settings.IsEnableStyle = IsEnableStyle; break;
            case nameof(Style): _settings.Style = Style; break;
            case nameof(ChineseSourceStyle): _settings.ChineseSourceStyle = ChineseSourceStyle; break;
            case nameof(ChineseTargetStyle): _settings.ChineseTargetStyle = ChineseTargetStyle; break;
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
        if (IsUpdating || string.IsNullOrWhiteSpace(model) || Models.Contains(model))
            return;

        using var _ = new UpdateGuard(this);

        Models.Add(model);
        Model = model;
    }

    [RelayCommand]
    private void DeleteModel(string model)
    {
        if (IsUpdating || !Models.Contains(model))
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
    private void GlossaryAdd()
    {
        var glossary = new Glossary { Name = "新术语库", Source = "zh", Target = "en" };
        _glossaryItems.Add(glossary);
        SelectedGlossary = glossary;
    }

    [RelayCommand]
    private void GlossaryDelete(IList list)
    {
        var deleted = list.Cast<Glossary>().ToList();
        foreach (var item in deleted) _glossaryItems.Remove(item);
        if (deleted.Contains(SelectedGlossary))
            SelectedGlossary = _glossaryItems.FirstOrDefault();
        SaveGlossaries();
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
            Debug.WriteLine($"Failed to export glossaries: {ex}");
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
            Debug.WriteLine($"Failed to import glossaries: {ex}");
        }
    }

    [RelayCommand]
    private void GlossaryTermAdd()
    {
        if (SelectedGlossary == null) { GlossarySaveStatus = "请先添加或选择一个术语库。"; return; }
        _glossaryTerms.Add(new Term());
    }

    [RelayCommand]
    private void GlossaryTermDelete(IList list)
    {
        foreach (var item in list.Cast<Term>().ToList()) _glossaryTerms.Remove(item);
    }

    [RelayCommand]
    private void GlossaryTermsExport()
    {
        if (SelectedGlossary == null) { GlossarySaveStatus = "请先选择一个术语库。"; return; }
        var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", FileName = $"{SelectedGlossary.Name}_terms.json", DefaultExt = "json" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_glossaryTerms, options), Encoding.UTF8);
    }

    [RelayCommand]
    private void GlossaryTermsImport()
    {
        var dialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Multiselect = false };
        if (SelectedGlossary == null) { GlossarySaveStatus = "请先选择一个术语库。"; return; }
        if (dialog.ShowDialog() != true) return;
        var imported = JsonSerializer.Deserialize<IEnumerable<Term>>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
        if (imported == null) return;
        _glossaryTerms.Clear();
        _glossaryTerms.AddRange(imported);
    }

    [RelayCommand]
    private async Task SaveGlossariesToBackend()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SecretId) || string.IsNullOrWhiteSpace(SecretKey))
            {
                GlossarySaveStatus = "请先填写腾讯云 SecretId 和 SecretKey。";
                return;
            }
            var glossary = SelectedGlossary;
            if (glossary == null)
            {
                GlossarySaveStatus = "请先选择一个术语库。";
                return;
            }
            GlossarySaveStatus = $"正在保存“{glossary.Name}”...";
            var terms = _glossaryTerms.Where(t => !string.IsNullOrWhiteSpace(t.SourceText) && !string.IsNullOrWhiteSpace(t.TargetText)).ToList();
            if (string.IsNullOrWhiteSpace(glossary.Name)) throw new InvalidOperationException("术语库名称不能为空。");
            if (string.IsNullOrWhiteSpace(glossary.Id))
            {
                var created = await Throttled(() => OpenApiRequest("CreateGlossary", new
                {
                    Name = glossary.Name,
                    Source = string.IsNullOrWhiteSpace(glossary.Source) ? "zh" : glossary.Source,
                    Target = string.IsNullOrWhiteSpace(glossary.Target) ? "en" : glossary.Target,
                    Description = "Created by STranslate HY-MT plugin"
                }));
                glossary.Id = created?["Response"]?["GlossaryId"]?.ToString() ?? throw new InvalidOperationException("创建术语库后未收到 GlossaryId。");
            }
            var existing = await DescribeAllGlossaryEntries(glossary.Id);
            foreach (var batch in existing.Select(t => t.EntryId).Where(id => !string.IsNullOrWhiteSpace(id)).Chunk(100))
                await Throttled(() => OpenApiRequest("DeleteGlossaryEntries", new { GlossaryId = glossary.Id, Entries = batch.Select(EntryId => new { EntryId }) }));
            foreach (var batch in terms.Chunk(100))
                await Throttled(() => OpenApiRequest("CreateGlossaryEntries", new { GlossaryId = glossary.Id, Entries = batch.Select(t => new { SourceTerm = t.SourceText, TargetTerm = t.TargetText }) }));
            glossary.Terms = [.. terms];
            SaveGlossaries();
            GlossarySaveStatus = $"保存成功：“{glossary.Name}”已同步 {terms.Count} 条术语。";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save glossaries: {ex}");
            GlossarySaveStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshGlossariesFromBackend()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SecretId) || string.IsNullOrWhiteSpace(SecretKey))
            {
                GlossarySaveStatus = "请先填写腾讯云 SecretId 和 SecretKey。";
                return;
            }
            GlossarySaveStatus = "正在刷新云端术语库...";
            var remoteItems = await DescribeAllGlossaries();
            var previous = _glossaryItems
                .Where(g => !string.IsNullOrWhiteSpace(g.Id))
                .GroupBy(g => g.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var selectedId = SelectedGlossary?.Id;
            using (var guard = new UpdateGuard(this))
            {
                _glossaryItems.Clear();
            foreach (var item in remoteItems)
                {
                    var id = item?["GlossaryId"]?.ToString() ?? string.Empty;
                    previous.TryGetValue(id, out var oldGlossary);
                    _glossaryItems.Add(new Glossary
                    {
                        Id = id,
                        Name = item?["Name"]?.ToString() ?? id,
                        Source = item?["Source"]?.ToString() ?? "zh",
                        Target = item?["Target"]?.ToString() ?? "en",
                        IsEnabled = oldGlossary?.IsEnabled ?? true,
                        Terms = oldGlossary?.Terms ?? []
                    });
                }
                SelectedGlossary = _glossaryItems.FirstOrDefault(g => g.Id == selectedId) ?? _glossaryItems.FirstOrDefault();
            }
            SaveGlossaries();
            GlossarySaveStatus = $"刷新成功：云端共有 {_glossaryItems.Count} 个术语库。";
            if (SelectedGlossary != null)
                await RefreshGlossaryTermsFromBackend();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh glossaries: {ex}");
            GlossarySaveStatus = $"刷新失败：{ex.Message}";
        }
    }

    private async Task<List<JsonNode?>> DescribeAllGlossaries()
    {
        const int limit = 100;
        var offset = 0;
        var results = new List<JsonNode?>();
        while (true)
        {
            var response = await Throttled(() => OpenApiRequest("DescribeGlossaries", new { Limit = limit, Offset = offset }));
            var items = response?["Response"]?["Items"]?.AsArray() ?? [];
            results.AddRange(items);
            var totalText = response?["Response"]?["TotalCount"]?.ToString();
            var total = int.TryParse(totalText, out var parsedTotal) ? parsedTotal : 0;
            if (items.Count == 0 || items.Count < limit || (total > 0 && results.Count >= total))
                break;
            offset += items.Count;
        }
        return results;
    }

    [RelayCommand]
    private async Task RefreshGlossaryTermsFromBackend()
    {
        try
        {
            var glossary = SelectedGlossary;
            if (glossary == null || string.IsNullOrWhiteSpace(glossary.Id))
            {
                GlossarySaveStatus = "请先在列表中选择一个术语库。";
                return;
            }
            GlossarySaveStatus = $"正在刷新“{glossary.Name}”的术语...";
            var remoteTerms = await DescribeAllGlossaryEntries(glossary.Id);
            using var guard = new UpdateGuard(this);
            _glossaryTerms.Clear();
            _glossaryTerms.AddRange(remoteTerms);
            SaveGlossaryTerms();
            GlossarySaveStatus = $"刷新成功：已载入 {_glossaryTerms.Count} 条术语。";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh glossary terms: {ex}");
            GlossarySaveStatus = $"刷新失败：{ex.Message}";
        }
    }

    private async Task<List<Term>> DescribeAllGlossaryEntries(string glossaryId)
    {
        const int pageSize = 200;
        var page = 1;
        var results = new List<Term>();
        while (true)
        {
            var response = await Throttled(() => OpenApiRequest("DescribeGlossaryEntries", new
            {
                GlossaryId = glossaryId,
                Page = page,
                PageSize = pageSize
            }));
            var entries = response?["Response"]?["Entries"]?.AsArray() ?? [];
            foreach (var item in entries)
            {
                results.Add(new Term
                {
                    EntryId = item?["EntryId"]?.ToString() ?? string.Empty,
                    SourceText = item?["SourceTerm"]?.ToString() ?? string.Empty,
                    TargetText = item?["TargetTerm"]?.ToString() ?? string.Empty
                });
            }
            var totalText = response?["Response"]?["Total"]?.ToString();
            var total = int.TryParse(totalText, out var parsedTotal) ? parsedTotal : 0;
            if (entries.Count == 0 || entries.Count < pageSize || (total > 0 && results.Count >= total))
                break;
            page++;
        }
        return results;
    }

    private async Task<JsonNode?> OpenApiRequest(string action, object body)
    {
        using var client = new HttpClient();
        var payload = JsonSerializer.Serialize(body, options);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var payloadHash = Sha256(payload);
        var canonicalHeaders = $"content-type:application/json\nhost:{OpenApiHost}\n";
        var signedHeaders = "content-type;host";
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var credentialScope = $"{date}/{OpenApiService}/tc3_request";
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{Sha256(canonicalRequest)}";
        var secretDate = Hmac($"TC3{SecretKey}", date);
        var secretService = Hmac(secretDate, OpenApiService);
        var secretSigning = Hmac(secretService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(secretSigning, stringToSign)).ToLowerInvariant();
        var authorization = $"TC3-HMAC-SHA256 Credential={SecretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{OpenApiHost}/");
        request.Content = new StringContent(payload, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-TC-Action", action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", OpenApiVersion);
        request.Headers.TryAddWithoutValidation("X-TC-Region", Region);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(text);
        var error = json?["Response"]?["Error"];
        if (error != null) throw new InvalidOperationException($"{error["Code"]}: {error["Message"]}");
        return json;
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static byte[] Hmac(string key, string value) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static async Task<T> Throttled<T>(Func<Task<T>> action)
    {
        await GlossaryRateLimiter.WaitAsync();
        try { return await action(); }
        finally { await Task.Delay(50); GlossaryRateLimiter.Release(); }
    }

    private static async Task Throttled(Func<Task> action)
    {
        await GlossaryRateLimiter.WaitAsync();
        try { await action(); }
        finally { await Task.Delay(50); GlossaryRateLimiter.Release(); }
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
            Debug.WriteLine($"Failed to export terms: {ex}");
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
            Debug.WriteLine($"Failed to import terms: {ex}");
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
            _viewModel._updateDepth++;
        }

        public void Dispose() => _viewModel._updateDepth--;
    }

    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string SecretId { get; set; }
    [ObservableProperty] public partial string SecretKey { get; set; }
    [ObservableProperty] public partial string Region { get; set; }

    [ObservableProperty] public partial string Model { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<string> Models { get; set; }

    [ObservableProperty] public partial bool IsEnableTerms { get; set; }

    [ObservableProperty] public partial bool IsEnableStyle { get; set; }

    [ObservableProperty] public partial string Style { get; set; }

    [ObservableProperty] public partial string ChineseSourceStyle { get; set; }

    [ObservableProperty] public partial string ChineseTargetStyle { get; set; }

    [ObservableProperty] public partial bool IsEnableGlossary { get; set; }

    [ObservableProperty] public partial string GlossaryIds { get; set; }

    [ObservableProperty] public partial string GlossarySaveStatus { get; set; } = string.Empty;

    [ObservableProperty] public partial Glossary? SelectedGlossary { get; set; }

    /// <summary>
    ///     术语列表
    /// </summary>
    private readonly ObservableList<Term> _items;

    private readonly ObservableList<Glossary> _glossaryItems;

    private readonly ObservableList<Term> _glossaryTerms;

    public INotifyCollectionChangedSynchronizedViewList<Term> Terms { get; }

    public INotifyCollectionChangedSynchronizedViewList<Glossary> Glossaries { get; }

    public INotifyCollectionChangedSynchronizedViewList<Term> GlossaryTerms { get; }

}
