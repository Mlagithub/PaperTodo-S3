# PaperTodo MinIO 同步插件 (MinioSync)

基于 MinIO / S3 兼容对象存储的 [PaperTodo](https://github.com/snownico0722/PaperTodo) 跨设备便签、待办与笔记图片双向串行接力同步插件。

## 功能特性

1. **跨设备串行接力**: 在办公室电脑和家里电脑之间无缝续接便签与待办事项。
2. **Note 图片附件同步**: 笔记中插入的图片自动提取并上传至对象存储，换机后自动拉取并注入本地图片库。
3. **内容寻址与哈希去重**: 图片按 SHA-256 存放在 `v1/assets/{hash}.bin`，多篇笔记引用同一图片或未修改图片永不重复传输。
4. **安全冲突隔离**: 两台设备发生并发编辑冲突时，自动保留本地修改，并将远端版本作为 `[冲突副本]` 独立创建新便签，杜绝静默数据丢失。
5. **异构屏幕自适应**: 不强行同步绝对窗口坐标 (X/Y)，避免跨屏幕/不同分辨率/多屏下的错位问题。
6. **后台静默 + 状态可视**: 停止编辑 30 秒后自动后台静默推送；顶栏常驻同步状态图标（☁ 已同步 / ⏳ 同步中 / ⚠ 冲突 / ❌ 错误），支持一键手动同步。
7. **内网与自建环境友好**: 支持 HTTP/HTTPS、IPv6、自定义端口，支持 `InsecureSkipVerify`（允许自签名证书）。

## 配置说明

在 PaperTodo 设置面板中找到 **「MinIO 同步接力」** 插件：

| 配置项 | 说明 | 示例 |
|---|---|---|
| **MinIO / S3 Endpoint** | 对象存储服务器地址 | `http://192.168.8.4:9000` 或 `https://minio.example.com` |
| **Bucket 存储桶** | 用于存放同步数据的 Bucket 名称 | `papertodo` |
| **存储前缀 (Prefix)** | 桶内子路径前缀，默认 `sync/` | `sync/` |
| **Access Key** | 访问凭据 AK | `your-access-key` |
| **Secret Key** | 访问凭据 SK | `your-secret-key` |
| **允许不安全 SSL** | 自签名证书或内网 HTTPS 时勾选 | `true` / `false` |
| **空闲自动静默同步** | 停止编辑 30 秒后自动推送到云端 | `true` / `false` |

## 构建与测试

```powershell
# 运行自动化测试
dotnet test tests/PaperTodo.MinioSyncChecks/PaperTodo.MinioSyncChecks.csproj

# 编译并发布插件
dotnet publish plugin-samples/PaperTodo.Plugin.MinioSync/PaperTodo.Plugin.MinioSync.csproj -c Release -r win-x64 --self-contained false
```
