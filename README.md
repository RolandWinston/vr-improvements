# VR Improvements

Roland 的 VR 改进实践：把启动故障、操控负担和演出镜头不适拆成可检查的工程问题，再用 AI 辅助实现、回归测试与版本记录推进。

| 模块 | 解决的问题与实现 | 验证范围 |
|---|---|---|
| [Outer Wilds 近地降落](outer-wilds) | 按高度、速度、坡度判断能否进入；先制动侧向运动再下降；玩家输入、失去地面或接触地面即退出 | 提供纯控制策略测试；实际飞行和头显体验待验证 |
| [ALVR / SteamVR 启动](vr-session) | Steam 已初始化但缺少 watchdog 记录时，结合当前进程的 console 证据与静置时间判断；保留端口、驱动和进程身份检查 | 提供启动解析、启动证据和有线请求自测；头显画面待验证 |
| [MiSide Comfort](miside-comfort) | 字幕距离与位置调整；按剧情摄像机运动渐入渐出遮罩，避免把玩家主动转头作为触发条件 | 发布现有源码快照；本仓库尚未完成集成构建和头显测试 |

## 我的工作

提出舒适度与易用性需求，拆解进入条件、退出条件和异常状态；组织 AI 辅助编码、日志定位和回归验证，保留失败用例与未验证项。模块是对现有 VR 生态的工程改进，游戏、VR 模组和运行库由各自作者维护。

## 运行纯测试

Windows PowerShell，安装 Windows .NET Framework 4.x 编译器：

```powershell
powershell -NoProfile -File ./outer-wilds/Test.ps1
powershell -NoProfile -File ./vr-session/Test.ps1
```

这两个入口只编译本仓库代码并运行纯策略测试，不启动 Steam、ALVR 或游戏。结果写入控制台，编译产物在 `build/`。

## 集成说明

Outer Wilds 需要合法安装的游戏、OWML 2.16.3 和 NomaiVR。将游戏 Managed 与 OWML 路径传给 `outer-wilds/Build.ps1 -ManagedPath ... -OwmlPath ...`；脚本只构建，不自动部署。近地辅助的进入上限为高度 120 m、相对速度 12 m/s、坡度 30°，不承担完整导航或障碍规划。

VRSession 适配 ALVR 20.14.1。公开副本用 `VRSESSION_ALVR_ROOT` 和 `VRSESSION_STEAM_ROOT` 指定安装位置，无参数默认为只读状态查询。显式 `--ensure` 会启动会话并调整 ALVR 驱动注册；`--stop` 会请求退出。使用这些运行操作前需核对本机版本与路径。公开副本的路径配置和默认动作与原部署版不同。

MiSide 源码依赖 BepInEx IL2CPP、Harmony、Unity 与游戏对应程序集；当前作为集成源码展示，不提供一键安装包。具体参数与行为见源文件。

测试结论和公开副本变更见 [VERIFICATION.md](VERIFICATION.md)，依赖与权利说明见 [NOTICE.md](NOTICE.md)。[个人作品集](https://rolandwinston.github.io/)
