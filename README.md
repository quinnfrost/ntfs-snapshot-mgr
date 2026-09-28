>**所有代码由copilot+deepseek-v4-flash完成，并且程序需要管理员权限，使用时注意**
# NTFS Snapshot Manager

Windows 桌面工具，用于管理本地 NTFS 卷影复制（Volume Shadow Copy / VSS）。

## 功能

- 列出所有本地 NTFS 卷及其快照
- 显示快照创建时间、占用空间、磁盘总/剩余空间
- 创建新快照
- 删除快照（含确认对话框）
- 设置每个卷的快照存储上限（滑条 + 精确输入 + 单位切换）
- 右键/双击/回车在资源管理器中打开快照
- 设备热插拔自动刷新卷列表
- 失焦超过 5 秒后自动刷新
- 关键操作日志

## 环境要求

- Windows 10 / 11
- .NET 8.0 SDK（或更高版本）
- **管理员权限**（管理 VSS 必需）

## 构建

```powershell
dotnet build NtfsSnapshotMgr.slnx
```

## 运行

```powershell
dotnet run --project NtfsSnapshotMgr
```

或以管理员身份直接运行生成的 exe：

```powershell
NtfsSnapshotMgr\bin\Debug\net8.0-windows\NtfsSnapshotMgr.exe
```

## 技术栈

- C# WinForms / .NET 8
- WMI (`System.Management`) 调用 `Win32_ShadowCopy`、`Win32_ShadowStorage`、`Win32_LogicalDisk`
- `WM_DEVICECHANGE` 监听设备变更
- 日志写入 exe 同目录的 `NtfsSnapshotMgr.log`

## 许可

MIT
