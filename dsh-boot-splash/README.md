# dsh-boot-splash

> 本仓**只有 vk 版**：位置 —— 不是插件：开机片头层（盖住页面未渲染时的空白），需先装 [dsh-vk-suite](https://github.com/Ln1m/dsh-vk-suite) 契约 + 骨架。
> 冲突：一个槽位只渲染优先级最高的一条，同优先级重复注册会直接抛错；与占同一位置的插件互斥（详见 [dsh-vk-suite](https://github.com/Ln1m/dsh-vk-suite) 的「推荐怎么用 / 会跟谁冲突」）。

[English](README.en.md) · 中文

![片头画面](assets/intro-1.png)
![片头画面](assets/intro-2.png)

*画面帧：`media/cyberpunk-intro.mp4` 的第 3 秒与第 5 秒。*

给 WinForms + WebView2 程序加一层**启动片头**：窗口一出现就铺满播动画，盖住「WebView2 还在初始化、网页还没渲染出来」的那段空白；网页一就绪，右上角浮出毛玻璃「跳过」，这一遍播完或用户点掉才撤。

- 播完停几秒再从头播（默认 3 秒，可配）
- 网页就绪前一直循环，不留黑屏
- 网页就绪后仍继续播完当前这一遍 —— 不想等就点「跳过」
- 换片不用改代码：丢进 `videos\`，改一行 `config.json`

## 动画来源

`media/cyberpunk-intro.mp4` 取自 **[NativeDog1/dsh-boot-animation](https://github.com/NativeDog1/dsh-boot-animation)**，原文件是该仓库的 `media/deepseek-cyberpunk-intro.mp4`（1280×720 / 24 fps / 7.05 s / H.264 + AAC）。

上游以 **BSD-3-Clause** 分发代码与媒体，许可证原文见
[`licenses/dsh-boot-animation-BSD-3-Clause.txt`](licenses/dsh-boot-animation-BSD-3-Clause.txt)。
**动画的著作权归原作者**，本仓库只是转载它并配套播放代码；本仓库自身的代码按 [MIT](LICENSE) 授权。

DeepSeek 的名称与标识归杭州深度求索人工智能基础技术研究有限公司所有，本项目与其没有隶属关系。

## 目录约定

运行时目录默认 `%USERPROFILE%\.dsh\boot-splash\`：

| 路径 | 作用 |
|---|---|
| `index.html` | 播放页：读配置、放片、间隔重播、跳过气泡 |
| `config.json` | `{ "video": "cyberpunk-intro.mp4", "gapMs": 3000 }` |
| `videos\*.mp4` | 片库；把 mp4 丢进来就能选它 |

`config.json` 读不出来、或 `video` 指的文件不存在时，页面回落到 `videos/cyberpunk-intro.mp4`。文件是 UTF-8 带不带 BOM 都行。

## 嵌入

```csharp
// 首次：把播放页与默认素材摊到运行时目录
BootSplashOverlay.Ensure(@"C:\Users\you\.dsh\boot-splash", @"<仓库>\src\splash");

BootSplashOverlay splash = new BootSplashOverlay(@"C:\Users\you\.dsh\boot-splash", null);
splash.Attach(this);                 // 你的 Form；会盖在最上层

await web.EnsureCoreWebView2Async(mainEnv);
await splash.StartAsync(mainEnv);    // 与主视图共用同一个 Environment

// 主视图 NavigationCompleted 且 e.IsSuccess 时：
splash.ReleaseToUser();

// 需要重试/加载失败时，让位给你自己的文字提示：
splash.Hide();
```

`src/BootSplashOverlay.cs` 是这份逻辑的可复用整理版；DSH 桌面套壳（[Ln1m/dsh-host-desktop](https://github.com/Ln1m/dsh-host-desktop)）里的等价实现内联在它的 `App.cs`。

## 行为细节

- 视频播完 → 停 `gapMs` 毫秒（默认 3000）→ 从头发，循环
- 页面收到 `__splashReady()`：设 `hold`，露出「跳过」；若当时正在停顿时立刻回报已结束
- 播完（`hold` 已置位）或点了「跳过」→ 用 `chrome.webview.postMessage` 报 `splash-ended` / `splash-skip`，宿主收到才撤层
- 层撤下只是 `Visible = false`，不影响宿主自己的控件层级

## 编译

需要 .NET Framework 自带的 csc 与 WebView2 运行时：

```powershell
csc /nologo /target:winexe /out:demo.exe `
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll `
  src\BootSplashOverlay.cs your-program.cs
```

三个 WebView2 SDK DLL 从 NuGet 包 `Microsoft.Web.WebView2` 取，或用你机器上任意一个装了 WebView2 的程序自带的那份。

## 许可

- 本仓库代码：[MIT](LICENSE)
- `media/cyberpunk-intro.mp4`：见上「动画来源」，BSD-3-Clause，著作权归原作者
