---
name: avalonia-desktop-patterns
description: Use when writing Avalonia XAML, code-behind, ViewModels, or handling async lifecycle and UI thread interaction in .NET 10 desktop applications, especially when dealing with MVVM messaging, compiled bindings, or pointer drag-and-drop issues
---

# Avalonia Desktop Patterns 规范

## 核心理念
Avalonia 12 与 .NET 10 的架构目标是**高性能、强类型编译绑定、无反射、线程安全与可靠的交互手势传递**。

本项目汲取了成熟桌面项目的避坑经验与反模式教训，确立以下代码与架构规范。

---

## 1. MVVM 与跨模块通信

### ✅ 使用 CommunityToolkit.Mvvm 代码生成器
- 统一使用 `[ObservableProperty]`、`[RelayCommand]`、`[NotifyPropertyChangedFor]` 等特性，禁止手写冗长的 `INotifyPropertyChanged` 样板代码。
- ViewModel 必须继承自项目内的 `ViewModelBase : ObservableObject`。

### ✅ 跨模块通信：优先使用 Messenger，坚决杜绝手写接线板
- **正确做法**：使用 `WeakReferenceMessenger.Default.Send(...)` 与 `WeakReferenceMessenger.Default.Register(...)`。弱引用天然防泄漏，调用方与监听方解耦。
- **反模式警示**：**绝对不要**在某个 God 级别的服务（如生命周期协调器）中堆砌几百行手写 C# 事件订阅（`+=` / `-=`），这极易导致强引用泄漏并增加难以维护的生命周期复杂度。

---

## 2. 编译期绑定（Compiled Bindings）铁律

1. **默认开启编译期绑定**：
   在 `Directory.Build.props` 或 `.csproj` 中确保启用编译绑定：
   ```xml
   <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
   ```
2. **所有 DataTemplate 必须声明 `x:DataType`**：
   ```xml
   <!-- 正确：明确指定强类型 -->
   <DataTemplate DataType="models:SessionNode" x:DataType="models:SessionNode">
       <TextBlock Text="{Binding Name}"/>
   </DataTemplate>
   ```
3. **避免无编译检查的反射 ViewLocator**：
   页面与窗口的导航或嵌入优先采用显式强类型模板声明或编译期匹配。

---

## 3. UI 交互与手势防吞避坑

### 坑 1：TreeView / ListBox 吞掉 PointerPressed 导致拖拽完全失效
在 Avalonia 12 中，`TreeViewItem` 自身在响应点击并处理选中时，会将事件的 `e.Handled` 标为 `true`。若直接通过常规的 `tree.PointerPressed += ...` 监听，该事件根本无法冒泡。
* **标准解法**：在 code-behind 中强制开启 `handledEventsToo: true`：
  ```csharp
  tree.AddHandler(PointerPressedEvent, OnPointerPressed, 
      RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
  tree.AddHandler(PointerMovedEvent, OnPointerMoved, 
      RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
  ```

### 坑 2：自定义客户区标题栏拖动吞掉按钮与控件点击
手绘无边框或扩展标题栏时，在标题栏区域触发 `BeginMoveDrag(e)` 必须严格过滤交互子控件。
* **标准解法**：递归向上检查命中源（`e.Source as Control`）的视觉树父链：
  ```csharp
  private static bool IsInteractive(Control? control)
  {
      while (control != null)
      {
          if (control is Button or TextBox or ComboBox or MenuItem or ScrollViewer)
              return true;
          if (control.Focusable)
              return true;
          control = control.Parent as Control;
      }
      return false;
  }
  ```
  只有当 `!IsInteractive(hit)` 时，才允许执行窗口拖动。

---

## 4. 线程纪律与异步生命周期

1. **UI 线程独占访问**：
   后台任务、SSH 读写流、Socket 接收续体中更新任何绑定至界面的属性或集合时，**必须且只能**经由 `Dispatcher.UIThread` 回写：
   ```csharp
   Dispatcher.UIThread.Post(() =>
   {
       this.Status = newStatus;
   });
   ```
2. **长生命周期异步任务持有 CancellationTokenSource**：
   连接建立、持续心跳、数据同步等长时间运行的后台任务，必须持有独立的 `CancellationTokenSource`。
3. **窗口关闭与退出排水（Drain）**：
   在窗口关闭前，必须主动触发取消令牌，并等待在飞的 I/O 任务或数据库事务安全落盘，严禁通过强制退出导致 SQLite 锁表或日志被截断。

---

## 5. 常见违规借口与事实反驳

| 常见借口 (Rationalization) | 实际规范 (Reality) |
| :--- | :--- |
| “手写 C# 事件（`+=`）比 Messenger 更直观” | 手写事件在对象销毁时必须逐个 `-=`，遗漏一个就会造成强引用内存泄漏，形成维护黑洞。 |
| “`x:DataType` 写起来太啰嗦，不写也能跑” | 不写会导致编译期绑定降级或编译告警，失去了类型安全校验与 AOT 兼容保障。 |
| “后台线程直接改集合也没报错” | Avalonia 在特定平台和渲染帧时跨线程改 UI 会随机引发不可复现的崩溃，必须走 `Dispatcher`。 |
| “拖拽失效一定是 Avalonia 底层 Bug” | 99% 的拖拽失效是因为子控件将 `e.Handled` 标为 `true`，请使用 `handledEventsToo: true`。 |
