using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace STranslate.Plugin.Translate.HyMt;

public class Settings
{
    public string ApiKey { get; set; } = string.Empty;

    public string SecretId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "ap-guangzhou";

    public string Model { get; set; } = "hy-mt2-plus";

    public List<string> Models { get; set; } =
    [
        "hy-mt2-pro",
        "hy-mt2-plus",
        "hy-mt2-lite"
    ];

    public bool IsEnableTerms { get; set; }

    public bool IsEnableStyle { get; set; }

    public string Style { get; set; } = string.Empty;

    public bool IsEnableGlossary { get; set; }

    /// <summary>HY-MT 持久化术语库 ID，多个 ID 使用逗号、分号或换行分隔。</summary>
    public string GlossaryIds { get; set; } = string.Empty;

    public List<Glossary> Glossaries { get; set; } = [];

    /// <summary>
    ///     术语列表
    /// </summary>
    public List<Term> Terms { get; set; } = [];

}

public partial class Term : ObservableObject
{
    [JsonIgnore]
    [ObservableProperty] public partial string EntryId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty] public partial string TargetText { get; set; } = string.Empty;
}

public partial class Glossary : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Source { get; set; } = "zh";
    [ObservableProperty] public partial string Target { get; set; } = "en";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    public List<Term> Terms { get; set; } = [];
}
