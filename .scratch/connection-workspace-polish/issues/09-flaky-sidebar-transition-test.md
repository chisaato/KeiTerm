# 侧栏动画测试偶发失败

Type: task
Status: resolved
Blocked by: 无

## 现象

`tests/Kei.Term.Tests/SidebarTransitionTests.cs` 的 `FileManagerSidebar_AnimatesBothDirections_AndReclaimsOnlyItsColumn(onLeft: True)` 在全量测试高负载下偶发失败，已出现两次（2026-10-21），错误为 `Assert.Equal() Failure: Expected: 0 Actual: 1`；单独运行及全量重跑均通过。

## 线索

- `AdvanceAnimationAsync` 以 15 次 `Task.Delay(16)` + `HeadlessAvalonia.Pump()` 推进真实时钟，按迭代次数而非真实流逝时间计数；动画实现见 `src/Kei.Term.App/Views/SidebarTransition.cs`。
- 失败的断言可能是 `transform.X == 0`、`surface.Opacity == 1` 或列宽归零中的一个，需先定位行号。
- 该测试在 `7e641a6` 中随侧栏比例宽度改动被修改过。

## 期望

定位失败断言与根因；让测试对调度抖动稳健（例如以动画完成信号或“在超时内最终满足”的等待替代固定迭代），且仍能因真实动画/布局回归而失败。不得通过放宽断言掩盖产品缺陷。

## Answer

根因是测试间共享状态干扰，不是生产缺陷：

- 并行运行的设置类测试会通过全局 Messenger 广播 `FileManagerDockChangedMessage(false)`。临时向该测试注入同一消息即可精确复现原列宽断言 `Expected: 0 Actual: 1`：左侧栏被换到右侧后，原左列成了终端的 `1*` 列，并不是动画没走完。未注入的全量基线中还出现过一次方向观测断言失败，同样指向跨测试干扰。诊断用的注入已移除。
- 次要问题：`AdvanceAnimationAsync` 按固定迭代次数而不是真实流逝时间计数。

修复（只改测试）：

- `SidebarTransitionTests` 加入现有的 `GuiResourceCollection`（`DisableParallelization = true`），不再与其他测试并行，从而避开全局消息干扰。
- 两个测试文件中，等待动画完成改为按条件等待（2s 超时，超时附带诊断信息）；需要观察动画进行到一半的用例改用 Stopwatch 计时，并保留完整观察窗口。所有断言保持原有严格度。
- 未修改生产代码。

## 验证记录

- 子任务：8 个 `yes` 负载进程与测试一起绑定在 CPU 0–7，目标两组测试（每轮 23 例）连续 40 轮通过；随后全量 1429 通过、36 跳过、0 失败。负载进程已全部结束，`git diff --check` 通过。
- 主会话：确认无残留负载进程；build 0 错误；全量连续两次均为 1429 通过、36 跳过、0 失败。

## Comments

首批压力验证第 15 轮出现过一次 2s 等待超时，当时还没有失败诊断信息，原因没有查清。补上诊断后连续 40 轮未再复现；超时和断言都没有放宽。如果再出现，诊断信息会打印出透明度、位移和停靠侧的实际值。调查日志在 `/tmp/opencode/sidebar-investigation/`（临时目录）。
