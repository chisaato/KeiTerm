using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public sealed class SettingsEditorDialogTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"keiterm_editor_dialog_{Guid.NewGuid():N}.db");
    private async Task<SqliteExternalEditorRepository> CreateRepositoryAsync()
    {
        SqliteExternalEditorRepository repository = new($"Data Source={_database}");
        await repository.InitializeAsync();
        return repository;
    }

    [Fact]
    public async Task PlatformDrafts_SaveTogetherWithoutMutatingOriginalOrDroppingArguments()
    {
        SqliteExternalEditorRepository repository = await CreateRepositoryAsync();
        ExternalEditor editor = new() { Name = "Editor", ArgumentsTemplate = "--reuse-window \"{path}\"" };
        editor.Paths = [new(Guid.NewGuid(), editor.Id, "linux", "/usr/bin/code"), new(Guid.NewGuid(), editor.Id, "any", "code")];
        await repository.SaveEditorAsync(editor);
        ExternalEditorEditViewModel draft = new(editor);
        draft.SelectedOs = "linux";
        draft.Path = "/opt/code";
        draft.SelectedOs = "windows";
        draft.Path = @"C:\Tools\code.exe";
        draft.SelectedOs = "linux";
        Assert.Equal("/opt/code", draft.Path);
        draft.Name = "Edited";
        draft.SaveCommand.Execute(null);
        Assert.NotNull(draft.Result);
        Assert.Equal("Editor", editor.Name);
        Assert.Equal("/usr/bin/code", editor.GetEffectivePath("linux"));
        await repository.SaveEditorAsync(draft.Result);
        ExternalEditor loaded = Assert.IsType<ExternalEditor>(await repository.GetEditorByIdAsync(editor.Id));
        Assert.Equal("Edited", loaded.Name);
        Assert.Equal("/opt/code", loaded.GetEffectivePath("linux"));
        Assert.Equal(@"C:\Tools\code.exe", loaded.GetEffectivePath("windows"));
        Assert.Equal("code", loaded.GetEffectivePath("android"));
        Assert.Equal(editor.ArgumentsTemplate, loaded.ArgumentsTemplate);
    }

    [Fact]
    public async Task CancelledEditor_DoesNotChangeDatabaseOrCurrentSelection()
    {
        SqliteExternalEditorRepository repository = await CreateRepositoryAsync();
        ExternalEditor editor = new() { Name = "Original" };
        editor.Paths.Add(new(Guid.NewGuid(), editor.Id, "any", "code"));
        await repository.SaveEditorAsync(editor);
        ExternalEditorEditViewModel draft = new(editor) { Name = "Cancelled", Path = "different" };
        draft.CancelCommand.Execute(null);
        Assert.Null(draft.Result);
        FileTransferSettingsPage page = new(repository);
        await page.ReloadAsync();
        await page.EditEditorCommand.ExecuteAsync(null);
        Assert.Equal("Original", Assert.Single(page.Editors).Name);
        Assert.Equal(editor.Id, page.SelectedEditor?.Id);
        Assert.Equal("code", (await repository.GetEditorByIdAsync(editor.Id))?.GetEffectivePath("any"));
    }

    [Fact]
    public async Task ListCommands_UpdateDefaultAndAssociations_AndDeleteCascades()
    {
        SqliteExternalEditorRepository repository = await CreateRepositoryAsync();
        ExternalEditor first = new() { Name = "First", IsDefault = true };
        first.Paths.Add(new(Guid.NewGuid(), first.Id, "any", "first"));
        await repository.SaveEditorAsync(first);
        ScriptedInteraction interaction = new();
        FileTransferSettingsPage page = new(repository) { Interaction = interaction };
        await page.ReloadAsync();
        ExternalEditorEditViewModel editorDraft = new() { Name = "Second", Path = "code", IsDefault = true };
        editorDraft.SaveCommand.Execute(null);
        ExternalEditor second = Assert.IsType<ExternalEditor>(editorDraft.Result);
        interaction.EditorResults.Enqueue(second);
        await page.AddEditorCommand.ExecuteAsync(null);
        Assert.False(page.HasError);
        Assert.Equal(second.Id, page.SelectedEditor?.Id);
        Assert.Equal(second.Id, Assert.Single(page.Editors, e => e.IsDefault).Id);
        Assert.False((await repository.GetEditorByIdAsync(first.Id))!.IsDefault);
        FileAssociationEditViewModel ruleDraft = new(null, page.Editors.Select(e => e.Model).ToArray())
        {
            Pattern = "*.py;*.json", SelectedEditor = page.SelectedEditor, Priority = 7
        };
        ruleDraft.SaveCommand.Execute(null);
        interaction.AssociationResults.Enqueue(ruleDraft.Result);
        await page.AddAssociationCommand.ExecuteAsync(null);
        Assert.True(page.HasAssociations);
        Assert.Equal(second.Id, FileAssociationResolver.ResolveEditorId("app.py", await repository.GetAllAssociationsAsync()));
        FileAssociationEditViewModel editedRule = new(Assert.Single(page.Associations).Model, page.Editors.Select(e => e.Model).ToArray()) { Pattern = "Dockerfile", Priority = 10 };
        editedRule.SaveCommand.Execute(null);
        interaction.AssociationResults.Enqueue(editedRule.Result);
        await page.EditAssociationCommand.ExecuteAsync(null);
        Assert.Equal("Dockerfile", Assert.Single(page.Associations).Pattern);
        Assert.Equal(10, Assert.Single(await repository.GetAllAssociationsAsync()).Priority);
        await page.DeleteEditorCommand.ExecuteAsync(null);
        Assert.Empty(page.Associations);
        Assert.False(page.HasAssociations);
        Assert.False(page.EditAssociationCommand.CanExecute(null));
        Assert.Equal(first.Id, Assert.Single(page.Editors).Id);
    }

    [Fact]
    public void EditorValidation_KeepsDialogOpenUntilRequiredFieldsAreProvided()
    {
        ExternalEditorEditViewModel draft = new();
        draft.SaveCommand.Execute(null);
        Assert.Null(draft.Result);
        Assert.True(draft.HasError);
        draft.Name = "VS Code";
        draft.Path = "code";
        draft.SaveCommand.Execute(null);
        Assert.NotNull(draft.Result);
        Assert.False(draft.HasError);
    }

    [Fact]
    public void AssociationCancelAndValidation_KeepSourceRuleUnchanged()
    {
        ExternalEditor editor = new() { Name = "Code" };
        FileAssociationRule rule = new() { Pattern = "*.txt", EditorId = editor.Id, Priority = 3 };
        FileAssociationEditViewModel draft = new(rule, [editor]) { Pattern = "*.py", Priority = 9 };
        draft.CancelCommand.Execute(null);
        Assert.Null(draft.Result);
        Assert.Equal("*.txt", rule.Pattern);
        Assert.Equal(3, rule.Priority);
        draft.Pattern = " ";
        draft.SaveCommand.Execute(null);
        Assert.Null(draft.Result);
        Assert.True(draft.HasError);
        draft.Pattern = "*.py";
        draft.SaveCommand.Execute(null);
        Assert.Equal(rule.Id, draft.Result?.Id);
        Assert.Equal(9, draft.Result?.Priority);
        Assert.False(draft.HasError);
    }

    [Fact]
    public void ProxyValidation_ShowsErrorAndPreservesOriginalProfile()
    {
        ProxyProfile source = new() { Name = "Original", Config = new Socks5ProxyConfig("localhost", 1080, null) };
        ProxyEditViewModel draft = new(source) { Name = "Edited", Port = 0 };
        draft.SaveCommand.Execute(null);
        Assert.False(draft.IsConfirmed);
        Assert.True(draft.HasError);
        draft.Port = 8080;
        draft.SaveCommand.Execute(null);
        Assert.True(draft.IsConfirmed);
        Assert.False(draft.HasError);
        Assert.Equal("Original", source.Name);
        Assert.Equal(1080, ((Socks5ProxyConfig)source.Config).Port);
        Assert.Equal(8080, ((Socks5ProxyConfig)draft.Build()!.Config).Port);
    }

    [PlatformFact(TestPlatform.MacOS)]
    public void MacApplicationSelection_UsesBundledCodeAndWaitsForDocument()
    {
        string application = Path.Combine(Path.GetTempPath(), $"Editor {Guid.NewGuid():N}.app");
        string cli = Path.Combine(application, "Contents", "Resources", "app", "bin", "code");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        try
        {
            File.WriteAllText(cli, "unused");
            string file = "/tmp/edit with spaces.py";
            var command = FileEditorLauncher.CreateEditorStartInfo(file, application);
            Assert.Equal(cli, command.FileName);
            Assert.Equal($"--wait \"{file}\"", command.Arguments);
            Assert.False(command.UseShellExecute);
            File.Delete(cli);
            var generic = FileEditorLauncher.CreateEditorStartInfo(file, application);
            Assert.Equal("/usr/bin/open", generic.FileName);
            Assert.Equal(new[] { "-W", "-a", application, file }, generic.ArgumentList);
        }
        finally { Directory.Delete(application, true); }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_database + suffix);
    }
}
