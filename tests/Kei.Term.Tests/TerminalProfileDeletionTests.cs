using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

public class TerminalProfileDeletionTests
{
    [Fact]
    public void RemovingCustomProfile_RemovesItAndBroadcastsAfterFallbackIsEffective()
    {
        ProfileManagerService manager = new(new FixedSettingsService());
        TerminalProfile custom = new() { Name = "User palette" };
        manager.AddCustomTerminalProfile(custom);
        List<TerminalProfileChange> changes = [];
        manager.TerminalProfileChanged += change =>
        {
            changes.Add(change);
            Assert.DoesNotContain(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
            Assert.Equal(manager.DefaultTerminalProfile.Id, manager.ResolveEffectiveTerminalProfile(custom.Id).Id);
        };

        manager.RemoveCustomTerminalProfile(custom.Id.ToUpperInvariant());

        Assert.DoesNotContain(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
        Assert.Contains(changes, change => string.Equals(change.ProfileId, custom.Id, StringComparison.OrdinalIgnoreCase)
            && change.Reason == TerminalProfileChangeReason.ProfileContentEdited);
    }

    [Fact]
    public void RemovingDefaultCustomProfile_FallsBackToBuiltInAndNotifiesInheritedTabs()
    {
        ProfileManagerService manager = new(new FixedSettingsService());
        TerminalProfile custom = new() { Name = "Default palette" };
        manager.AddCustomTerminalProfile(custom);
        manager.SetDefaultTerminalProfile(custom.Id);
        List<TerminalProfileChange> changes = [];
        manager.TerminalProfileChanged += changes.Add;

        manager.RemoveCustomTerminalProfile(custom.Id);

        Assert.Null(manager.DefaultTerminalProfileId);
        Assert.Equal(BuiltInPresets.GetDefaultTerminalProfile().Id, manager.DefaultTerminalProfile.Id);
        Assert.True(manager.DefaultTerminalProfile.IsBuiltIn);
        Assert.Contains(changes, change => change.Reason == TerminalProfileChangeReason.DefaultSelectionChanged);
    }

    [Fact]
    public void RemovingBuiltInId_IsRejectedEvenWhenCustomProfileCollides()
    {
        ProfileManagerService manager = new(new FixedSettingsService());
        string builtInId = BuiltInPresets.GetDefaultTerminalProfile().Id;
        manager.AddCustomTerminalProfile(new TerminalProfile { Id = builtInId, Name = "Collision" });
        manager.SetDefaultTerminalProfile(builtInId);
        List<TerminalProfileChange> changes = [];
        manager.TerminalProfileChanged += changes.Add;

        manager.RemoveCustomTerminalProfile(builtInId.ToUpperInvariant());

        Assert.Contains(manager.AllTerminalProfiles, profile => profile.Id == builtInId && profile.IsBuiltIn);
        Assert.Single(manager.SnapshotWorkingTerminalProfiles());
        Assert.Equal(builtInId, manager.DefaultTerminalProfileId);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task RestoreWorking_RestoresDeletedCommittedProfileAndDefault()
    {
        string directory = NewDirectory();
        try
        {
            ProfileManagerService manager = new(new FixedSettingsService(), directory);
            TerminalProfile custom = new() { Name = "Saved palette", Background = "#123456" };
            manager.AddCustomTerminalProfile(custom);
            manager.SetDefaultTerminalProfile(custom.Id);
            await manager.CommitAndSaveProfilesAsync();
            manager.RemoveCustomTerminalProfile(custom.Id);
            Assert.DoesNotContain(manager.SnapshotWorkingTerminalProfiles(), profile => profile.Id == custom.Id);

            IReadOnlyList<string> affected = manager.RestoreWorkingToCommitted();

            Assert.Contains(custom.Id, affected);
            TerminalProfile restored = Assert.Single(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
            Assert.Equal("#123456", restored.Background);
            Assert.Equal(custom.Id, manager.DefaultTerminalProfile.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledDeleteConfirmation_LeavesSelectionAndProfilesUntouched()
    {
        string directory = NewDirectory();
        try
        {
            ProfileManagerService manager = new(new FixedSettingsService(), directory);
            TerminalProfile custom = new() { Name = "Keep palette" };
            manager.AddCustomTerminalProfile(custom);
            SettingsViewModel model = new(new FixedSettingsService(), directory, profileManager: manager);
            AppearanceSettingsPage appearance = Appearance(model);
            appearance.SelectedTerminalProfile = custom;
            ScriptedInteraction interaction = new();
            interaction.DeleteAnswers.Enqueue(false);
            model.SetInteraction(interaction);

            await model.DeleteTerminalProfileCommand.ExecuteAsync(null);

            Assert.Equal(custom.Name, Assert.Single(interaction.DeletePrompts));
            Assert.Contains(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
            Assert.Same(custom, appearance.SelectedTerminalProfile);
            Assert.Equal(custom.Id, manager.DefaultTerminalProfileId);
            Assert.True(appearance.CanDeleteSelectedTerminalProfile);
            Assert.False(File.Exists(Path.Combine(directory, "profiles.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedDelete_IsDraftUntilApply_AndCancelRestoresIt(bool apply)
    {
        string directory = NewDirectory();
        try
        {
            FixedSettingsService settings = new();
            ProfileManagerService manager = new(settings, directory);
            TerminalProfile custom = new() { Name = "Saved palette" };
            manager.AddCustomTerminalProfile(custom);
            manager.SetDefaultTerminalProfile(custom.Id);
            await manager.CommitAndSaveProfilesAsync();
            SettingsViewModel model = new(settings, directory, profileManager: manager);
            AppearanceSettingsPage appearance = Appearance(model);
            model.SetInteraction(new ScriptedInteraction());
            Assert.True(appearance.CanDeleteSelectedTerminalProfile);
            List<TerminalProfileChange> changes = [];
            manager.TerminalProfileChanged += changes.Add;

            await model.DeleteTerminalProfileCommand.ExecuteAsync(null);

            Assert.DoesNotContain(appearance.TerminalProfiles, profile => profile.Id == custom.Id);
            Assert.Equal(manager.DefaultTerminalProfile.Id, appearance.SelectedTerminalProfile!.Id);
            Assert.False(appearance.CanDeleteSelectedTerminalProfile);
            Assert.NotEqual(custom.Id, manager.DefaultTerminalProfileId);
            Assert.Contains(changes, change => change.ProfileId == appearance.SelectedTerminalProfile.Id
                && change.Reason == TerminalProfileChangeReason.DefaultSelectionChanged);
            ProfileManagerService beforeApply = new(settings, directory);
            await beforeApply.InitializeAsync();
            Assert.Contains(beforeApply.AllTerminalProfiles, profile => profile.Id == custom.Id);

            if (apply)
            {
                Assert.True(await model.ApplyChangesAsync());
            }
            else
            {
                await model.CancelCommand.ExecuteAsync(null);
                Assert.Contains(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
                Assert.Equal(custom.Id, appearance.SelectedTerminalProfile!.Id);
                Assert.True(appearance.CanDeleteSelectedTerminalProfile);
            }

            ProfileManagerService reopened = new(settings, directory);
            await reopened.InitializeAsync();
            Assert.Equal(!apply, reopened.AllTerminalProfiles.Any(profile => profile.Id == custom.Id));
            Assert.Equal(apply ? BuiltInPresets.GetDefaultTerminalProfile().Id : custom.Id, reopened.DefaultTerminalProfile.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DeleteCommand_RejectsBuiltInNullAndBusySelectionsWithoutPrompting()
    {
        string directory = NewDirectory();
        try
        {
            ProfileManagerService manager = new(new FixedSettingsService(), directory);
            SettingsViewModel model = new(new FixedSettingsService(), directory, profileManager: manager);
            AppearanceSettingsPage appearance = Appearance(model);
            ScriptedInteraction interaction = new();
            model.SetInteraction(interaction);
            await model.DeleteTerminalProfileCommand.ExecuteAsync(null);
            Assert.False(appearance.CanDeleteSelectedTerminalProfile);
            appearance.SelectedTerminalProfile = null;
            Assert.False(appearance.CanDeleteSelectedTerminalProfile);
            await model.DeleteTerminalProfileCommand.ExecuteAsync(null);
            TerminalProfile custom = new() { Name = "Busy palette" };
            manager.AddCustomTerminalProfile(custom);
            appearance.SelectedTerminalProfile = custom;
            model.IsBusy = true;
            await model.DeleteTerminalProfileCommand.ExecuteAsync(null);

            Assert.Empty(interaction.DeletePrompts);
            Assert.Contains(manager.AllTerminalProfiles, profile => profile.Id == custom.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static AppearanceSettingsPage Appearance(SettingsViewModel model)
        => (AppearanceSettingsPage)model.Categories.First(category => category.Page is AppearanceSettingsPage).Page;

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeiTerm-ProfileDeletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
