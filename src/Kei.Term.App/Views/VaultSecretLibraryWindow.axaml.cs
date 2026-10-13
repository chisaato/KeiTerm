using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class VaultSecretLibraryWindow : Window
{
    private Guid? _selectedSecretId;
    private VaultSecretLibraryViewModel? _viewModel;

    public VaultSecretLibraryWindow()
    {
        InitializeComponent();
        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    public VaultSecretLibraryWindow(VaultSecretLibraryViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // 监听 Items 引用替换或集合变更，按 _selectedSecretId 恢复选中项
        viewModel.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(VaultSecretLibraryViewModel.Items))
            {
                HookCollectionChanged();
                RestoreSelection();
            }
        };

        HookCollectionChanged();
        RestoreSelection();
    }

    private void HookCollectionChanged()
    {
        if (_viewModel?.Items is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnItemsCollectionChanged;
            collection.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RestoreSelection();
    }

    private void OnSecretListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SecretList.SelectedItem is VaultSecretListItemViewModel selected)
        {
            _selectedSecretId = selected.Id;
        }
    }

    private void RestoreSelection()
    {
        if (_selectedSecretId is Guid id && _viewModel != null)
        {
            var match = _viewModel.Items.FirstOrDefault(item => item.Id == id);
            SecretList.SelectedItem = match;
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}