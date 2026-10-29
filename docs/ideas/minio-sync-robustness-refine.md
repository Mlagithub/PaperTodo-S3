# PaperTodo-S3 核心问题深度收敛与架构重构方案 (One-Pager)

## Problem Statement
如何在一个没有中央锁服务、完全基于 S3 弱一致性对象存储的无状态环境中，以最低的心智负担和代码复杂度，消除待办 ID 漂移引发的死循环同步，彻底防范离线并发删除导致的数据灭失，并保证后台同步链路具备绝对的线程安全性与多云可用性？

---

## Recommended Direction
我们将围绕**“数据 0 丢失、状态 0 漂移、进程 0 卡死”**三个核心目标，对插件开展精准重构：
1. **哈希业务语义化解耦**：彻底剥离内容哈希中对本地运行时动态 `todo.Id` 的依赖，消除跨端 ID 差异导致的假性修改与无限死循环同步。
2. **三路状态机兜底删除**：严格实现三方比对（本地当前、云端清单、历史已知），当本地删除与远端新改发生冲突时，绝不静默覆盖云端，自动恢复为 `[冲突副本]`。
3. **并发版本快照机制**：引入原子递增版本号，解决同步耗时期间用户产生新打字导致的增量修改丢失。
4. **全异步防死锁改造**：移除 `Dispose()` 中的同步阻塞 `.Wait()`，引入 `CancellationTokenSource` 优雅取消，确保退出绝对安全。
5. **协议与元数据补全**：开放 S3 `Region` 配置并提供配置输入防抖；图片附件支持多格式 MIME 自动识别，并在下载残缺时实行整单熔断保护。

---

## Key Assumptions to Validate
- [ ] **语义哈希幂等性**：跨设备拉取 Todo 并应用后，两端计算出的 `ContentHash` 必须完全相等，且空闲触发的静默同步推送数恒为 0。
- [ ] **删除冲突兜底性**：设备 A 修改便签，设备 B 在未拉取最新内容前删除本地便签，同步后云端内容不丢失，设备 B 生成 `[冲突副本]`。
- [ ] **线程安全性**：在宿主关闭或重载插件时，调用 `Dispose()` 耗时必须处于毫秒级，且绝无主线程卡死或死锁现象。
- [ ] **多云兼容性**：在配置非 `us-east-1` 的云厂商（如阿里云 OSS `oss-cn-hangzhou`、腾讯云 COS）时，SigV4 鉴权能够正常通过。

---

## MVP Scope (具体重构清单)

### Task 1: 语义哈希与消除乒乓同步 (`SyncManifest.cs`)
- 重构 `PaperSyncData.ComputeContentHash()`：
  - 待办项哈希改由 `(Order, Text, Done, ReminderAt, LinkedPaperId)` 计算；
  - 移除对 `todo.Id` 的计算依赖，确保跨设备导入后内容哈希一致。

### Task 2: 健壮的删除-修改冲突判定 (`SyncEngine.cs`)
- 重构拉取阶段的本地删除判定逻辑：
  - 遇到本地不存在但历史已知记录中存在的便签时，对比云端当前哈希与本地历史已知哈希；
  - 若哈希一致，安全执行云端删除；
  - 若哈希不一致（远端有并发更新），判定为并发冲突，重新拉取并另存为 `[冲突副本]`，阻断对云端数据的误删。

### Task 3: 增量版本快照与防并发漏更 (`MinioSyncRuntime.cs`)
- 引入原子变更计数器 `_changeVersion`：
  - `OnWorkspaceEvent` 触发时原子递增；
  - 同步开始前捕获当前版本；
  - 同步成功后，仅当期间无新版本递增才重置 `_hasPendingChanges = false`，若有新增则重置防抖计时器。

### Task 4: 消除 `Dispose()` 死锁风险 (`MinioSyncRuntime.cs`)
- 移除 `Dispose()` 中的 `.Wait(TimeSpan.FromSeconds(3))` 同步阻塞；
- 引入 `CancellationTokenSource`，在 `Dispose` 时取消在途网络 IO，保证纯非阻塞优雅退出。

### Task 5: 开放 S3 Region 配置与输入防抖 (`S3Config.cs`, `plugin.json`, `MinioSyncRuntime.cs`)
- 在 `plugin.json` 与 `MinioPluginSettings.cs` 中增加 `region` 字段（默认 `us-east-1`）；
- 在 `MinioSyncRuntime.OnSettingsChanged` 中引入 1.5 秒输入防抖，避免编辑配置过程中的高频错误请求。

### Task 6: 图片附件 MIME 动态保留与部分失败熔断 (`SyncEngine.cs`, `ImageAssetSyncer.cs`)
- 自动识别或探测真实图片格式（PNG/JPEG/GIF/WebP），拉取导入时传递正确的 MIME 类型；
- 便签引用的图片附件若有任意一张拉取失败，熔断该便签的更新并返回错误，防止生成坏链半成品。

### Task 7: 自动化测试用例扩充 (`PaperTodo.MinioSyncChecks`)
- 新增 `TodoPushPullIdleHashChecks`：验证两端 Todo 内容哈希一致性与空闲防死循环；
- 新增 `DeleteConflictPreservationChecks`：验证并发删除修改下的冲突保护。

---

## Not Doing (and Why)
- **不做复杂的 CRDT / OT 实时协作**：便签是单人跨设备流转工具，维持“串行接力 + 冲突分支隔离（[冲突副本]）”是复杂度与可靠性平衡的最佳实践。
- **本期不做 S3 图片垃圾回收 (GC) 后台 Worker**：对象存储成本极低，优先保证核心数据流的绝对安全与轻量，后续作为独立运维工具实现。
- **不引入 AWS SDK 重型第三方包**：坚守轻量自研 SigV4 架构，保持编译产物极致精简（仅数百 KB）。

---

## Open Questions
- 是否需要在便签详情或悬浮提示中展示最后一次同步成功的具体时间及设备名称（例如“来自 PC-办公室 14:30”）？
