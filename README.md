# PaperTodo-S3 (MinIO 同步接力插件)

[English](#features) | [简体中文](#功能特性)

PaperTodo-S3 是专为桌面便签应用 [PaperTodo](https://github.com/snownico0722/PaperTodo) 设计的跨设备双向同步插件，支持基于 MinIO、AWS S3、阿里云 OSS、Rust S3 等兼容 S3 协议的对象存储服务。

---

## 功能特性

1. **多电脑串行接力**: 适合办公室与家庭电脑流转使用，离机自动推送到云端，开机自动拉取最新便签与待办。
2. **Note 图片附件同步**: 自动提取 Markdown 便签中插入的图片，按 SHA-256 内容寻址上传并拉取到本地，异地无缝查看图片。
3. **内容哈希去重**: 图片与便签以内容哈希为依据存储，重复引用的图片与未修改的内容永不重复传输，节约流量与时间。
4. **安全冲突隔离**: 两台设备发生并发编辑冲突时，插件绝不静默覆盖本地数据，而是自动将云端版本作为 `[冲突副本]` 独立创建新便签，数据安全 100% 兜底。
5. **异构屏幕保护**: 仅同步便签内容、待办条目与图片，不强制同步绝对物理窗口坐标 (X/Y)，避免跨屏幕/多屏/不同分辨率 DPI 下窗口飞出屏幕。
6. **后台静默 + 状态可视**: 停止编辑 30 秒后自动后台静默推送；便签顶栏常驻同步状态图标（☁ 已同步 / ⏳ 同步中 / ⚠ 冲突 / ❌ 错误），支持点击一键手动同步。
7. **内网与自建友好**: 支持 HTTP/HTTPS、IPv6、自定义端口，支持 `InsecureSkipVerify`（允许内网自签名证书）。

---

## 配置说明

在 PaperTodo **设置 (Settings) -> 扩展与插件 (Plugins) -> MinIO 同步接力** 中配置：

| 配置项 | 字段说明 | 常见配置示例与避坑指南 |
|---|---|---|
| **MinIO / S3 Endpoint** | S3 API 服务端地址 | **⚠️ 注意**：必须填写 MinIO 的 **S3 API 端口（通常是 9000）**，不要填 Console 网页管理端口（通常是 9001）。如：`http://192.168.8.4:9000` 或单独反代 S3 API 的域名 `https://s3.example.com` |
| **Bucket 存储桶** | 存放数据的存储桶名称 | `papertodo`（请提前在 MinIO 控制台建好该 Bucket） |
| **存储前缀 (Prefix)** | 桶内子路径前缀 | 默认 `sync/` |
| **Access Key** | 访问密钥 AK | MinIO 或 S3 用户访问密钥 |
| **Secret Key** | 秘密密钥 SK | MinIO 或 S3 用户秘密密钥 |
| **允许不安全 SSL** | 忽略 SSL 证书错误 | 自签名证书、内网 HTTPS 或 Lucky 反代请勾选开启 |
| **开机自动启用同步** | 自启动后台服务 | 默认开启，以隐藏纸片保持顶栏同步图标常驻 |
| **空闲自动静默同步** | 防抖自动上传 | 默认开启，编辑停止 30 秒后自动上传 |

---

## 快速安装

1. 编译或下载发布包后，将产物目录放置在 PaperTodo 的 `plugins/` 目录下：
   ```text
   PaperTodo/
   └── plugins/
       └── com.papertodo.plugin.minio/
           ├── plugin.json
           ├── PaperTodo.Plugin.MinioSync.dll
           └── PaperTodo.Plugin.MinioSync.deps.json
   ```
2. 重启 PaperTodo，即可在设置中看到插件并开始使用。

---

## 编译与测试

本项目基于 .NET 10 (WPF) 构建：

```powershell
# 1. 运行自动化测试（覆盖 S3 签名、增量推拉、冲突副本测试）
dotnet test tests/PaperTodo.MinioSyncChecks/PaperTodo.MinioSyncChecks.csproj -c Release

# 2. 发布插件产物
dotnet publish src/PaperTodo.Plugin.MinioSync/PaperTodo.Plugin.MinioSync.csproj -c Release -r win-x64 --self-contained false -o dist
```

---

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
