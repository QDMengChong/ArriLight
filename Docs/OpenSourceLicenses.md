# 开源许可页面

入口为“我的 → 关于我们 → 开源许可”。列表可按项目名称或许可证搜索，点击项目查看版本、作者、
许可证全文和第三方版权声明；“项目主页”按钮打开上游网站。界面使用现有中英文资源，许可文本保留原文。

数据位于 `ArriLight/Resources/Raw/OpenSourceLicenses/catalog.json`，原文位于同目录的 `texts`。
这些文件作为 `MauiAsset` 打包，查看许可不需要联网。列表包含 Android/iOS 项目恢复出的直接与间接 NuGet 依赖、
.NET 运行时及平台 SDK，以及随应用分发的 Open Sans 字体；它描述整个移动项目，包含构建工具依赖。
当前字体的嵌入信息为 Open Sans 1.10 / Apache-2.0，不能用现行 Open Sans 的许可证替换。

许可来源为实际 NuGet 包的 `.nuspec`、`LICENSE`、`THIRD-PARTY-NOTICES`，缺少完整许可的包使用已核对的上游原文。
上游原文缓存在 `tools/license-sources`，来源 URL 记录在生成脚本及目录清单中。
内容相同的原文按 SHA-256 前 24 位去重，保留各组件到原文及来源的关联；`.gitattributes` 禁止对原文转换换行。
ArriOpto.BLE 是源码引用项目，没有声明开源许可证，因此未给它编造第三方开源许可。

升级依赖或 SDK 后，在具有 Android/iOS 工作负载的 Windows 开发环境运行：

```powershell
dotnet restore ArriLight/ArriLight.csproj
python -X utf8 tools/update_open_source_licenses.py
dotnet test tests/ArriLight.Tests/ArriLight.Tests.csproj --filter FullyQualifiedName~OpenSourceLicenseTests
```

脚本使用 Python 标准库读取恢复清单、本机 NuGet 缓存和 SDK 原文，并读取 TTF 中的版权及许可信息。
上游文本已有缓存时可离线运行；第一次收集缺失文本时从脚本记录的官方来源下载。
包缺少完整许可或已核对的上游包版本发生变化时会报错，应核对新版本的来源、许可和版权声明后更新映射。
SDK 清单使用本机 .NET 10 工作负载版本；发布环境换用不同 SDK 时应重新生成并检查清单。

测试检查完整原文和内容哈希、直接依赖版本、恢复出的间接依赖覆盖，以及复合许可证。
发布前还应在 Android/iOS 上检查列表滚动、搜索、详情长文本、离线打开和返回导航。
