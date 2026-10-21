# 文件列表按 Delete 不会删除选中项

Type: task
Status: resolved
Blocked by: 无

## 现象

在文件视图的文件列表里选中文件或目录后按 Delete，没有任何反应，只能通过右键菜单的「删除」来删除。

## 原因

`RemoteFileManagerView.axaml` 只在 `UserControl.KeyBindings` 上绑定了 F5，文件列表 `FileListBox` 上没有 Delete 的绑定。全局快捷键（`MainWindow.Shortcuts.cs`）里也没有处理 Delete。

## Answer

- `src/Kei.Term.App/Views/RemoteFileManagerView.axaml`：在 `FileListBox.KeyBindings` 上加 `Delete → DeleteItemCommand`，参数是 `SelectedItem`。批量选择沿用 `DeleteItemAsync` 现有的逻辑：有 `SelectedItems` 时以它为准，同样会排除 `..`，同样要先确认。右键菜单的「删除」上补了 `InputGesture="Delete"` 提示。
- 绑定放在列表上而不是 `UserControl` 上，所以焦点在路径输入框时，Delete 仍然只做文本编辑。
- `tests/Kei.Term.Tests/RemoteFileOperationsTests.cs`：`DeleteKey_OnFileList_DeletesSelection_ButNotFromPathInput` 走 headless 的真实键盘路由。焦点在路径框时按 Delete 不弹确认；焦点在列表条目上时按 Delete 会弹确认并删掉远端条目。

## 验证记录

- 去掉绑定后，新用例失败；恢复绑定后，新用例通过。
- 全量单元测试通过。

## Comments
