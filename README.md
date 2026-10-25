# Legado-WP

**「阅读 3.0」(Legado) 书源兼容的 UWP 阅读器 —— 让 Windows 10 Mobile 继续读书。**

在 Lumia 929（Windows 10 Mobile 15254，ARM32）上真机开发与测试。

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20Mobile%20%7C%20UWP-0078D7)]() [![Device](https://img.shields.io/badge/tested%20on-Lumia%20929-107C10)]() [![License](https://img.shields.io/badge/license-MIT-yellow)]()

## 简介

Legado-WP 是一个用 C# / XAML 从零实现的 Windows 10 (UWP) 小说阅读器，**规则引擎语义对照 [legado](https://github.com/gedoor/legado)（阅读 3.0）源码移植**，可导入社区通用的"阅读"书源 JSON，在网络文学网站上搜索、阅读小说。

项目目标是给末代 Windows Phone（Windows 10 Mobile 15254）一个能用的现代阅读器：

- 纯 UWP + .NET Native AOT，最低支持 Windows 10 15063，ARM32 真机实测
- 不携带任何书源，由用户自行导入（兼容格式见下）
- 全部代码离线可用（除联网取书本身）

## 已实现功能

| 功能 | 说明 |
|---|---|
| 书源管理 | URL 导入「阅读」书源 JSON（单对象/数组），启停、删除、去重合并 |
| 全源搜索 | 并发搜索全部启用书源，瞬态失败自动重试，按匹配度排序（精确 > 前缀 > 包含） |
| 书籍详情 | 简介 + 目录 + 一键加入书架 / 离线全文 |
| 阅读器 | 三种排版：上下滑动 / 平移覆盖 / 翻页动画；字号调节；段首缩进；跨章续读 |
| 书签 | 按章（分页模式下含页码）添加/列表/一键跳转 |
| 阅读进度 | 自动记忆，书架点击直达上次位置 |
| 离线全文 | 逐章下载缓存（含目录与书源自包含，断网可读，书源删除也可读） |
| 书架 | 长按移出（保留离线数据） |
| 编码 | GBK/GB2312 站点自动识别与请求编码 |
| JS 引擎 | Jint 执行书源中的 `{{js}}` / `<js>` / `@js:` 片段，内置 `java.*` 桥核心集（md5/SHA/HMAC/AES/DES/RSA、base64/hex、`java.ajax`、Cookie 等） |
| 规则语法 | Default（class/tag/id/text/children + 新旧索引）、`@CSS:`、`$.` JSONPath、`##` 替换、`@put/@get` 变量、`&&/\|\|/%%` 组合 |

## 书源兼容说明

**兼容「阅读 3.0 (Legado)」的书源 JSON 格式（社区流通的 2.x / 3.x 书源）**，导入方式：书源页粘贴书源 JSON 的 URL。

当前**不支持或部分支持**的写法（会持续补齐）：

- 依赖 `webView` / `startBrowser` 的书源（需要内置浏览器，暂未实现）
- `java.*` 桥未覆盖的方法（压缩包读取、文件系统、`queryTTF` 字体反爬等）
- 繁简转换 `java.t2s / s2t`（暂原样返回）
- XPath 规则（`@XPath:` / `/` 开头，暂未实现）

**选源建议**：优先使用无 JS 或轻 JS 的 HTTP(S) 书源，体验最佳。部分仅 TLS 1.3 的站点受限于 WP10 系统网络栈无法访问。

> ⚠️ 本项目**不内置、不分发任何书源**。书源由用户自行导入，请遵守当地法律法规与目标网站的条款，仅供个人学习研究。

## 部署到 Windows 10 Mobile 手机

### 一、编译（PC 端）

环境要求：

- Visual Studio 2022（含 BuildTools）+ **通用 Windows 平台生成工具**工作负载
- Windows 10 SDK **10.0.16299**（含 ARM 工具）
- .NET Native / NuGet 还原（首次编译自动完成）

```powershell
git clone https://github.com/superxyline/Legado-WP.git
cd Legado-WP

# 还原 + 编译 ARM32 包（工程默认 MinVersion=15063，正好覆盖 WP10 15254）
MSBuild legado-wp.csproj -t:Restore -p:Configuration=Release -p:Platform=ARM
MSBuild legado-wp.csproj -p:Configuration=Release -p:Platform=ARM
```

产物：`AppPackages\legado-wp_x.y.z.w_ARM_Test\*.appx`

### 二、签名（自签证书）

UWP 包必须签名才能侧载：

```powershell
# 1. 生成代码签名证书（CN 需与包 Publisher 一致）
$cert = New-SelfSignedCertificate -Subject 'CN=legado-wp' -Type CodeSigningCert `
  -CertStoreLocation 'Cert:\CurrentUser\My'
$pwd = ConvertTo-SecureString -String '你的密码' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath legado-wp.pfx -Password $pwd
Export-Certificate -Cert $cert -FilePath legado-wp.cer

# 2. 带签名参数重新打包
MSBuild legado-wp.csproj -p:Configuration=Release -p:Platform=ARM `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateKeyFile=legado-wp.pfx `
  -p:PackageCertificatePassword=你的密码
```

> `.pfx` / `.cer` 已在 `.gitignore` 中，请勿提交仓库。

### 三、部署到手机（WinAppDeployCmd）

1. 手机：**设置 → 更新和安全 → 面向开发者** → 打开「开发者模式」和「设备发现」
2. 手机与 PC 同一局域网（或 USB 连接且 `Windows Phone IP over USB` 服务运行中）
3. PC 管理员 PowerShell 导入证书到本机信任（侧载验证需要）：

```powershell
Import-Certificate .\legado-wp.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

4. 扫描设备并安装（首次需手机上「设备发现 → 配对」弹出的 PIN）：

```powershell
# 工具位于 SDK 安装目录（x86 目录下）
$deploy = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.16299.0\x86\WinAppDeployCmd.exe'

# 扫到手机 IP 后：
& $deploy install -file .\AppPackages\legado-wp_0.0.11.0_ARM_Test\legado-wp_0.0.11.0_ARM.appx `
  -ip <手机IP> -pin <配对码>
```

依赖包（.NET Native Framework/Runtime、VCLibs，位于 `AppPackages\...\Dependencies\ARM\`）逐个 `install` 即可；系统自带更高版本时会自动跳过。

5. 开始屏幕找到「阅读」图标，导入书源 URL，开始阅读。

> 日常升级：卸载重装会清书架与进度（书源需重导）。可用 `-p` 传新版本号编译后用 `update` 子命令保数据升级。

## 路线图

- [ ] XPath 规则支持（`@XPath:`）
- [ ] `java.*` 桥扩展（压缩包、字体反爬 `queryTTF`）
- [ ] 繁简转换（t2s / s2t）
- [ ] 换源（同名书多源切换）
- [ ] 夜间模式 / 主题色
- [ ] 本地 TXT / EPUB
- [ ] 发现页（exploreUrl）
- [ ] 仿真纸页翻页动画（Composition 变形）
- [ ] 断点续传提示与离线管理页

## 架构速览

```
Core/
  Entities.cs        书源实体（BookSource + 规则子对象，Newtonsoft 反序列化）
  RuleSplit.cs       规则切分状态机（&&/||/%%，跳过平衡组）+ 结果合并
  AnalyzeByJSoup.cs  Default 语法 + @CSS: + 全套索引（对照 AnalyzeByJSoup.kt）
  Analyzers.cs       JSONPath（Newtonsoft）+ 正则多段
  AnalyzeRule.cs     引擎入口：Mode 分发 / <js>{{}} 切分 / $n 组替换 / ## 替换
  AnalyzeUrl.cs      URL 规则（POST/GET/charset/headers/option JSON/{{key}}）
  HttpHelper.cs      IPageFetcher 抽象 + HttpClient 实现（GBK/三级 charset 探测/Cookie）
  WebBook.cs         搜索→详情→目录(翻页)→正文(翻页+净化) 四阶段链路
  JsExtensions.cs    java.* 桥（WinRT 加密：MD5/SHA/HMAC/AES/RSA + ajax）
  JsEngine.cs        Jint 封装（变量绑定 + fetcher 注入）
  Storage.cs         书源/书架/进度/书签持久化
  OfflineStore.cs    离线全文缓存（自包含 meta：源+目录）
Views/               书架/搜索/详情目录/阅读器/书源管理 五页面
```

## 致谢

- **[gedoor/legado](https://github.com/gedoor/legado)** ——「阅读 3.0」原作者与社区，本项目全部规则语义的来源；
- **[huajideshutiao/legado](https://github.com/huajideshutiao/legado)** —— 活跃维护的 KMP 重构分支，本项目移植时的重要参考实现；
- [Jint](https://github.com/sebastienros/jint)、[AngleSharp](https://github.com/AngleSharp/AngleSharp)、[Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) —— 引擎三件套。

## 免责声明

本项目仅供技术学习与研究。不提供、不托管任何内容源；用户导入的书源及其访问行为由用户自行负责。请支持正版阅读。

## License

MIT
