using STranslate.Plugin.Translate.HyMt.View;
using STranslate.Plugin.Translate.HyMt.ViewModel;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.HyMt;

public class Main : TranslatePluginBase
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    private const string ChatUrl = "https://tokenhub.tencentmaas.com/v1/chat/completions";
    private const string TranslationUrl = "https://tokenhub.tencentmaas.com/v1/api/translations";

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    /// <summary>
    ///     https://cloud.tencent.com/document/product/1823/132252#14735a54e0rwb
    /// </summary>
    /// <param name="lang"></param>
    /// <returns></returns>
    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto", // 自动检测
        LangEnum.ChineseSimplified => "zh",
        LangEnum.ChineseTraditional => "zh-TR",
        LangEnum.Cantonese => "Cantonese", // 粤语
        LangEnum.English => "en", LangEnum.Japanese => "ja", LangEnum.Korean => "ko",
        LangEnum.French => "fr", LangEnum.Spanish => "es", LangEnum.Russian => "ru",
        LangEnum.German => "de", LangEnum.Italian => "it", LangEnum.Turkish => "tr",
        LangEnum.PortuguesePortugal => "pt", LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi", LangEnum.Indonesian => "id", LangEnum.Thai => "th",
        LangEnum.Malay => "ms", LangEnum.Arabic => "ar", LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null, // 不支持（蒙古语-西里尔）
        LangEnum.MongolianTraditional => null, // 不支持（蒙古语-蒙文）
        LangEnum.Khmer => "km", LangEnum.NorwegianBokmal => "nb", LangEnum.NorwegianNynorsk => "nn",
        LangEnum.Persian => "fa", LangEnum.Swedish => "sv", LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl", LangEnum.Ukrainian => "uk",
        _ => null
    };

    /// <summary>
    ///     https://cloud.tencent.com/document/product/1823/132252#14735a54e0rwb
    /// </summary>
    /// <param name="lang"></param>
    /// <returns></returns>
    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto", // 自动检测
        LangEnum.ChineseSimplified => "zh", LangEnum.ChineseTraditional => "zh-TR",
        LangEnum.Cantonese => "Cantonese", // 粤语
        LangEnum.English => "en", LangEnum.Japanese => "ja", LangEnum.Korean => "ko", LangEnum.French => "fr",
        LangEnum.Spanish => "es", LangEnum.Russian => "ru", LangEnum.German => "de", LangEnum.Italian => "it",
        LangEnum.Turkish => "tr", LangEnum.PortuguesePortugal => "pt", LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi", LangEnum.Indonesian => "id", LangEnum.Thai => "th", LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar", LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null, // 不支持（蒙古语-西里尔）
        LangEnum.MongolianTraditional => null, // 不支持（蒙古语-蒙文）
        LangEnum.Khmer => "km", LangEnum.NorwegianBokmal => "nb", LangEnum.NorwegianNynorsk => "nn", LangEnum.Persian => "fa",
        LangEnum.Swedish => "sv", LangEnum.Polish => "pl", LangEnum.Dutch => "nl", LangEnum.Ukrainian => "uk",
        _ => null
    };

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
    }

    public override void Dispose() => _viewModel?.Dispose();

    public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
    {
        if (GetSourceLanguage(request.SourceLang) is not string sourceStr)
        {
            result.Fail(Context.GetTranslation("UnsupportedSourceLang"));
            return;
        }
        if (GetTargetLanguage(request.TargetLang) is not string targetStr)
        {
            result.Fail(Context.GetTranslation("UnsupportedTargetLang"));
            return;
        }

        var model = string.IsNullOrWhiteSpace(Settings.Model) ? "hy-mt2-plus" : Settings.Model.Trim();

        var options = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bearer {Settings.ApiKey}" }
            }
        };

        var glossaryIds = Settings.IsEnableGlossary
            ? (Settings.Glossaries.Count > 0
                ? Settings.Glossaries.Where(g => g.IsEnabled).Select(g => g.Id)
                : Settings.GlossaryIds.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(id => !string.IsNullOrWhiteSpace(id)).Take(10).ToArray()
            : [];
        object content;
        string url;
        if (glossaryIds.Length > 0)
        {
            url = TranslationUrl;
            var referenceTerms = Enumerable.Empty<Term>();
            if (Settings.IsEnableTerms) referenceTerms = referenceTerms.Concat(Settings.Terms);
            referenceTerms = referenceTerms.Concat(Settings.GlossaryTerms);
            object[] localTerms = referenceTerms
                .Where(t => !string.IsNullOrWhiteSpace(t.SourceText) && !string.IsNullOrWhiteSpace(t.TargetText))
                .Select(t => (object)new { source = t.SourceText, target = t.TargetText })
                .Take(10)
                .ToArray();
            var context = Settings.IsEnableStyle && !string.IsNullOrWhiteSpace(Settings.Style) ? $"译文风格：{Settings.Style}" : null;
            content = new { model, text = request.Text, source = sourceStr == "auto" ? null : sourceStr, target = targetStr, glossary_ids = glossaryIds, references = localTerms, context };
        }
        else
        {
            url = ChatUrl;
            var prompt = $"请将以下文本翻译为 {targetStr}。注意只需要输出翻译后的结果，不要额外解释：\n";
            if (Settings.IsEnableStyle && !string.IsNullOrWhiteSpace(Settings.Style)) prompt = $"请将以下文本翻译为 {targetStr}。注意翻译的风格要严格符合【{Settings.Style}】\n";
            if (Settings.IsEnableTerms) prompt = string.Join("\n", Settings.Terms.Where(t => !string.IsNullOrWhiteSpace(t.SourceText)).Select(t => $"{t.SourceText} 翻译成 {t.TargetText}")) + "\n" + prompt;
            if (Settings.IsEnableDomains && !string.IsNullOrWhiteSpace(Settings.Domains)) prompt = $"领域：{Settings.Domains}\n" + prompt;
            content = new { model, messages = new[] { new { role = "user", content = prompt + request.Text } } };
        }
        var response = await Context.HttpService.PostAsync(url, content, options, cancellationToken);
        var parsedData = JsonNode.Parse(response);
        var choicesNode = parsedData?["choices"] as JsonArray;
        var firstChoice = choicesNode?.FirstOrDefault();
        var data = firstChoice?["message"]?["content"]?.ToString() ?? throw new Exception($"No result.\nRaw: {response}");

        result.Success(data);
    }
}
