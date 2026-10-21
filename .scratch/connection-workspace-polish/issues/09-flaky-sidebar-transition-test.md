# 侧栏动画测试偶发失败

Type: task
Status: ready-for-agent
Blocked by: 无

## 现象

`tests/Kei.Term.Tests/SidebarTransitionTests.cs` 的 `FileManagerSidebar_AnimatesBothDirections_AndReclaimsOnlyItsColumn(onLeft: True)` 在全量测试高负载下偶发失败，已出现两次（2026-10-21），错误为 `Assert.Equal() Failure: Expected: 0 Actual: 1`；单独运行及全量重跑均通过。

## 线索

- `AdvanceAnimationAsync` 以 15 次 `Task.Delay(16)` + `HeadlessAvalonia.Pump()` 推进真实时钟，按迭代次数而非真实流逝时间计数；动画实现见 `src/Kei.Term.App/Views/SidebarTransition.cs`。
- 失败的断言可能是 `transform.X == 0`、`surface.Opacity == 1` 或列宽归零中的一个，需先定位行号。
- 该测试在 `7e641a6` 中随侧栏比例宽度改动被修改过。

## 期望

定位失败断言与根因；让测试对调度抖动稳健（例如以动画完成信号或“在超时内最终满足”的等待替代固定迭代），且仍能因真实动画/布局回归而失败。不得通过放宽断言掩盖产品缺陷。

## Comments
