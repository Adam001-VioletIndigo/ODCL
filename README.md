# OpenDatabaseCleaner — Opencode 本地数据库清理器
`byAdam001`

使用[opencode](https://opencode.ai)时，你是否发现C盘爆满，打开发现opencode.db占用多达几十G，海量的数据被存入这个db文件，而其中很多都是无效数据。为此我开发了这款工具——OpencodeDbCLeaner帮助你便捷管理数据库文件

- **查看会话**：浏览全部历史会话，按占用空间排序，找出最占空间的会话。
- **删除会话**：圈选不要的会话一键删除，释放空间；磁盘紧张时自动分批处理，不会写爆磁盘。
- **重建优化**：一键 `VACUUM`，回收数据库里残留的空闲空间。
- **搬到别的盘**：把整个 opencode 数据文件夹移到其他磁盘，原位置自动留下
  符号链接，opencode 不需要做任何改动。

## 使用

1. 在 [GitHub Releases](https://github.com/Adam001-VioletIndigo/ODCL/releases)
   下载对应系统的压缩包：
   - `ODCL-Runtime-x64.zip`：64 位 Windows（绝大多数电脑选这个）
   - `ODCL-Runtime-x86.zip`：32 位 Windows
   - `ODCL-Runtime-arm64.zip`：ARM64 设备（如部分 Surface / 高通电脑）
2. 解压后运行里面的 `ODCL.exe`，无需安装。
3. 程序正常操作 **不需要管理员权限**；只有用到「搬到别的盘」时，Windows 会
   在需要时弹一次确认框。

> 首次使用前，请先退出正在运行的 opencode，避免数据库被占用导致操作失败。

## 安全说明

- 删除的会话无法找回，操作前请确认已选中自己不再需要的会话。
- 数据默认存在于 `C:\Users\<你的用户名>\.local\share\opencode\`，
  建议定期备份该文件夹。
- 「搬到别的盘」只有在目标副本完整性校验通过后才会删除原数据。

## 开发者

构建需要 .NET 10 SDK：

```powershell
dotnet build ODCL.csproj -c Debug -p:Platform=x64
```

## 许可证

MIT