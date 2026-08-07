# 随心记

面向盲人用户的网址收藏与密码本桌面应用，支持纯键盘操作与争渡读屏适配。

## 项目结构

```
SuixinJi/
├── .github/
│   └── workflows/
│       └── build.yml          # GitHub Actions CI/CD 工作流
├── .nuget/                     # NuGet 本地缓存（自动生成，已 gitignore）
│   ├── packages/               #   NuGet 包缓存
│   └── v3-cache/               #   HTTP 请求缓存
├── Models/                     # 数据模型
├── Services/                   # 业务服务
├── Views/                      # WPF 对话框视图
├── Converters/                 # 值转换器
├── App.xaml / App.xaml.cs      # 应用入口
├── MainWindow.xaml / .cs       # 主窗口
├── BlindNotepad.csproj         # 项目文件
├── NuGet.config                # NuGet 配置（指向项目内缓存）
├── .gitignore                  # Git 忽略规则
└── README.md                   # 本文件
```

## 本地构建

### 前提条件
- Windows 10/11 或安装了 .NET 8 SDK 的系统
- .NET 8 SDK（含 WPF 工作负载）

### 构建命令

```bash
# 还原依赖（使用项目内 .nuget/ 缓存）
dotnet restore --configfile NuGet.config

# 编译
dotnet build -c Release

# 发布自包含单文件
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./artifacts/release
```

## CI/CD 自动打包

### 工作方式

1. 推送代码到 `main`/`master` 分支 → 自动构建，产物上传为 Artifact
2. 推送 `v*` 标签（如 `v2.0.0`）→ 自动构建并发布到 GitHub Releases
3. 提交 Pull Request → 自动构建验证（不发布）
4. 手动触发 → 在 Actions 页面点击 "Run workflow"

### 快速发布

```bash
# 创建标签并推送，触发自动发布
git tag v2.0.0
git push origin v2.0.0
```

### 缓存策略

- NuGet 包缓存在项目内 `.nuget/packages/` 目录
- CI 环境使用 `actions/cache` 缓存 `.nuget/` 目录，加速后续构建
- 缓存 key 基于 `*.csproj` 和 `NuGet.config` 的哈希值，依赖变化时自动刷新

## 功能特性

- 网址收藏（Enter 打开，Ctrl+Enter 复制网址）
- 密码本（AES-256 加密，主密码保护）
- 记事本模块
- 争渡读屏适配（ZDSRAPI + UIA LiveRegion）
- 自定义快捷键（导出/导入配置）
- 重复检测、数据备份恢复、审计日志
- TOTP 动态验证码、防截屏保护
