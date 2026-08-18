# ODCL — Opencode Database Cleaner

日期：2026-08-18 ｜ 状态：已确认 ｜ 技术栈：C# / .NET 10 / WinUI 3（未打包，自包含）

## 目标

一个轻量的桌面工具，直接读取 opencode 的 SQLite 数据库（`~/.local/share/opencode/opencode.db`），
按会话列出内容与占用，支持单会话/批量删除、VACUUM 重建、以及把整个 opencode 数据文件夹转移到其他磁盘并用
目录符号链接指回原路径。

## 环境约束

- 本机有 .NET SDK 10.0.302，`dotnet new` 无 WinUI 3 模板 → 手写项目文件（.csproj + App/MainWindow），
  NuGet 引用 `Microsoft.WindowsAppSDK`、`Microsoft.Data.Sqlite`、`Microsoft.Windows.SDK.BuildTools`。
- 非打包（unpackaged）自包含部署；运行需网络还原 NuGet。
- 目标机路径：`C:\Users\34946.DELL_G15_5510\.local\share\opencode\`。

## 数据模型（只读，来自实际 schema）

- `session`：会话元数据（title, time_created/updated, tokens, cost, directory）。
- `message`（FK session ON DELETE CASCADE）：`id`,`session_id`,`data`JSON(role/agent/modelID/time/finish)。
- `part`（FK message ON DELETE CASCADE）：`id`,`message_id`,`session_id`,`data`JSON(type/text/tool/reasoning/file…)。
- `session_message`/`session_input`/`session_context_epoch`（FK session CASCADE）。
- `event`（FK event_sequence CASCADE）：事件溯源日志，占库体积 98%+。
- `event_sequence`：aggregate 序列（无 FK 到 session，删除会话后 event/event_sequence 需手动清理）。
- 体积统计：`dbstat`（按表含索引）＋ `PRAGMA freelist_count * page_size`。

## 统计维度（顶部状态栏）

- 数据库总大小：主文件 + `-wal` + `-shm`。
- event：数据 + 索引字节（dbstat 汇总）。
- 关联数据：message + part + session_message/input/context_epoch + todo 等。
- freelist 占用；磁盘剩余；"重建所需大小"估算（见磁盘策略）。

## 磁盘策略（核心，封装为纯函数类 `DiskStrategy`）

- `估算重建大小 estFinal` = 删除后各保留会话的 (event 字节 + message/part 字节) 总和 × 1.2 + 常数千字节。
- `canRebuild = FreeBytes >= estFinal * 1.2`：满足时才允许 VACUUM。
- 删除路径选择：
  - 快速路径（FreeBytes >= estFinal * 1.5）：按会话单事务 DELETE，每会话后 `wal_checkpoint`。
  - 分批路径（磁盘不足）：按 `rowid IN (SELECT rowid … LIMIT B)` 分批，批大小由
    `currentFreeBytes` 反推（WAL 增长 ≤ 剩余磁盘 25%），每批后 `wal_checkpoint(TRUNCATE)`（busy retry）。
- 上次实践中验证：WAL 模式下单次大删除会让 WAL 暴涨填满磁盘并回滚——此策略是必须的。

## 删除语义（与手工清理一致）

事务顺序：删 `event`（该 aggregate）→ 删 `event_sequence` → 删 `session`（级联 message/part/
session_message/input/context）。

## 转移数据库（新增）

1. 选择目标文件夹（校验：非空、不位于源文件夹内部）。
2. 建议先退出 opencode：转移前检测 `Get-Process` 中是否存在 `opencode` 进程，
   存在则在 UI 强提醒（不硬性阻止，用户自行确认）。
3. 安全移动：复制整个 `.local/share/opencode` 到目标 → 在目标执行 `PRAGMA integrity_check` +
   `COUNT(*)` 验证 → 删除原文件夹（连同 `-shm`/`-wal`）→ `mklink /d "<原路径>" "<目标路径>"`。
4. `mklink /d` 失败（无管理员/开发者模式）→ 自动走 UAC 提权重试；仍失败则给出两条命令让用户手动执行。
5. 成功后重新打开库并刷新统计；UI 继续显示原路径（经链接解析）。

## UI 布局（单窗口）

- 顶部：库路径 + 统计条 + `VACUUM` 按钮（依据 canRebuild 置灰）+ `转移数据库` 按钮。
- 左侧 ListView（支持多选）：会话按占用字节降序（默认），显示标题/时间/事件数/大小。
- 右侧 TreeView：message（角色/模型/时间/finish）→ part（text/reasoning/tool 名+输入输出/file）。
- 删除前确认框，显示估算释放空间。

## 测试

`DiskStrategy` 核心规则留一个可运行的自动检查（`--selftest` 命令行模式，断言分批阈值/批大小边界，
失败非零退出码）。