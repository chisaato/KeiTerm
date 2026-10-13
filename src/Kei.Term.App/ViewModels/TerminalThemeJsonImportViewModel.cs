using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// JSON 始终保留为用户草稿；只有当前内容校验成功后才允许提交配色。
public sealed partial class TerminalThemeJsonImportViewModel : ViewModelBase
{
    private static class Keys
    {
        public const string Valid = "TerminalThemeJsonImport.Validation.Valid";
        public const string Invalid = "TerminalThemeJsonImport.Validation.Invalid";
        public const string Location = "TerminalThemeJsonImport.Validation.Location";
        public const string Copied = "TerminalThemeJsonImport.Prompt.Copied";
        public const string CopyFailed = "TerminalThemeJsonImport.Prompt.CopyFailed";
        public const string EmptyInput = "TerminalThemeJsonImport.Error.EmptyInput";
        public const string InputTooLarge = "TerminalThemeJsonImport.Error.InputTooLarge";
        public const string InvalidJson = "TerminalThemeJsonImport.Error.InvalidJson";
        public const string ExpectedObject = "TerminalThemeJsonImport.Error.ExpectedObject";
        public const string DuplicateProperty = "TerminalThemeJsonImport.Error.DuplicateProperty";
        public const string UnknownProperty = "TerminalThemeJsonImport.Error.UnknownProperty";
        public const string MissingProperty = "TerminalThemeJsonImport.Error.MissingProperty";
        public const string UnsupportedSchemaVersion = "TerminalThemeJsonImport.Error.UnsupportedSchemaVersion";
        public const string InvalidName = "TerminalThemeJsonImport.Error.InvalidName";
        public const string InvalidColor = "TerminalThemeJsonImport.Error.InvalidColor";
        public const string InvalidAnsiColors = "TerminalThemeJsonImport.Error.InvalidAnsiColors";
    }

    public static TerminalThemeJsonImportViewModel FromProfile(TerminalProfile profile)
    {
        TerminalThemeJsonImportViewModel draft = new()
        {
            Name = profile.Name,
            JsonText = TerminalThemeJsonParser.Export(profile)
        };
        draft.Validate();
        return draft;
    }

    public ObservableCollection<TerminalThemeJsonIssueViewModel> Issues { get; } = [];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FormatCommand))]
    private string _jsonText = string.Empty;

    [ObservableProperty]
    private TerminalProfile? _previewProfile;

    [ObservableProperty]
    private bool _canImport;

    [ObservableProperty]
    private bool _hasErrors;

    [ObservableProperty]
    private bool _hasValidation;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClipboardFeedback))]
    [NotifyPropertyChangedFor(nameof(ClipboardSucceeded))]
    private string _clipboardFeedback = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipboardSucceeded))]
    private bool _clipboardFailed;

    public bool HasClipboardFeedback => ClipboardFeedback.Length > 0;
    public bool ClipboardSucceeded => HasClipboardFeedback && !ClipboardFailed;
    public TerminalProfile? Result { get; private set; }
    public bool IsConfirmed => Result != null;

    partial void OnNameChanged(string value) => InvalidateValidation();
    partial void OnJsonTextChanged(string value) => InvalidateValidation();

    private void InvalidateValidation()
    {
        // 不能把旧内容的预览/校验结果当作新输入的导入许可。
        Result = null;
        CanImport = false;
        PreviewProfile = null;
        HasErrors = false;
        HasValidation = false;
        Issues.Clear();
        ValidationSummary = string.Empty;
    }

    [RelayCommand]
    public void Validate() => ApplyValidation(TerminalThemeJsonParser.Parse(JsonText, Name));

    private bool HasJson() => !string.IsNullOrWhiteSpace(JsonText);

    [RelayCommand(CanExecute = nameof(HasJson))]
    public void Format()
    {
        TerminalThemeJsonParseResult parsed = TerminalThemeJsonParser.Parse(JsonText, Name);
        if (parsed.FormattedJson != null)
        {
            // 格式化完整 JSON 文档，保留错误字段；不从 Profile 重新导出。
            JsonText = parsed.FormattedJson;
            parsed = TerminalThemeJsonParser.Parse(JsonText, Name);
        }

        ApplyValidation(parsed);
    }

    [RelayCommand]
    public void LoadExample()
    {
        JsonText = TerminalThemeJsonParser.ExampleJson;
        Validate();
    }

    private void ApplyValidation(TerminalThemeJsonParseResult parsed)
    {
        HasValidation = true;
        Result = null;
        Issues.Clear();
        foreach (TerminalThemeJsonIssue issue in parsed.Issues)
        {
            string location = issue.LineNumber is int line && issue.BytePositionInLine is int column
                ? Strings.Format(Keys.Location, line, column)
                : string.Empty;
            Issues.Add(new TerminalThemeJsonIssueViewModel(issue.Path, DescribeError(issue.Error), location));
        }

        CanImport = parsed.IsValid && parsed.Profile != null;
        PreviewProfile = CanImport ? parsed.Profile : null;
        HasErrors = Issues.Count > 0;
        ValidationSummary = CanImport
            ? Strings.Format(Keys.Valid, parsed.Profile!.Name)
            : Strings.Format(Keys.Invalid, Issues.Count);
    }

    public void Confirm()
    {
        // 提交时重新解析当前草稿，保证实际导入与编辑器内容一致。
        Validate();
        if (CanImport)
        {
            Result = PreviewProfile!.DeepCopy();
        }
    }

    public void Cancel() => Result = null;

    public string CreateLlmPrompt() => TerminalThemeJsonParser.CreateLlmPrompt(Name);

    public void ReportPromptCopied(bool succeeded)
    {
        ClipboardFailed = !succeeded;
        ClipboardFeedback = Strings.Get(succeeded ? Keys.Copied : Keys.CopyFailed);
    }

    private static string DescribeError(TerminalThemeJsonError error) => Strings.Get(error switch
    {
        TerminalThemeJsonError.EmptyInput => Keys.EmptyInput,
        TerminalThemeJsonError.InputTooLarge => Keys.InputTooLarge,
        TerminalThemeJsonError.InvalidJson => Keys.InvalidJson,
        TerminalThemeJsonError.ExpectedObject => Keys.ExpectedObject,
        TerminalThemeJsonError.DuplicateProperty => Keys.DuplicateProperty,
        TerminalThemeJsonError.UnknownProperty => Keys.UnknownProperty,
        TerminalThemeJsonError.MissingProperty => Keys.MissingProperty,
        TerminalThemeJsonError.UnsupportedSchemaVersion => Keys.UnsupportedSchemaVersion,
        TerminalThemeJsonError.InvalidName => Keys.InvalidName,
        TerminalThemeJsonError.InvalidColor => Keys.InvalidColor,
        TerminalThemeJsonError.InvalidAnsiColors => Keys.InvalidAnsiColors,
        _ => Keys.InvalidJson
    });
}

public sealed record TerminalThemeJsonIssueViewModel(string Path, string Message, string Location)
{
    public bool HasLocation => Location.Length > 0;
}
