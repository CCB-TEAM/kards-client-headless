# 随机兼容契约

更新：当前规则程序为`subset-v0.3-script1`，默认由Rust核心执行对局与RNG，卡库指纹由Rust规范化后统一计算。C#原语仍用于对照/审计；下文旧版本号是此前随机迁移记录。脚本接口与当前运行方式见`../HeadlessCore/README.md`。本文件的固定kards-sim随机原语配置没有改变。

本轮对照仓库：[CCB-TEAM/kards-sim](https://github.com/CCB-TEAM/kards-sim)，固定提交 `5067a25321456e191ae4083ce098b8a2e49a31ca`。源码完整克隆到`Research/Headless/kards-sim`，未改动其随机实现。

## “1:1”的对象和边界

目标是固定提交的随机原语在**相同初始状态、相同候选顺序、相同调用顺序**下逐位一致，包括状态和随机数消耗次数。不是仅保证同种子可复现，也不是把“分布相似”当兼容。

这不等于完整对局已经与参考模拟器或原版UE一模一样。当前仍只支持六项卡牌定义。没有执行的触发器不会凭空消耗随机数；若缺少效果、候选次序不同或AI策略不同，后续整局随机轨迹仍会分歧。参考Program/HTTP自对弈的随机AI调用`S.Rng.Next(legal.Count)`，会消耗对局流；本程序的贪心AI不消耗它，不应拿两者的整局录像直接要求一致。

参考源码实际也不是UE FRandomStream：不要用UE的LCG替换后声称兼容这个仓库。已生成蓝图中的Stream种子/按action_id重置，并不意味着宿主真正执行了同样的随机状态更新。

## 两条流必须区分

| 路径 | 参考实际行为 | 本项目 |
|---|---|---|
| Core/Rng.cs | 每局两个ulong状态，八次预热，后续连续使用 | LogicRandom：C#与Rust双后端 |
| Core.Rng.Next(n) | n<=1返回0且不消耗；其余拒绝采样，再取模 | 保留拒绝边界及消耗次数 |
| Core.Rng.Shuffle | 从末尾到开头的Fisher–Yates，调用Next(i+1) | 用于原型的开始牌组洗牌，先左后右共享一条流 |
| EngineHost.RandomIntegerInRangeFromStream | 忽略Stream对象；使用引擎流原始NextULong后取模；反序边界先交换 | LogicRandom.BridgeRange，不改成Next(n) |
| Kismet.Host.Array_ShuffleFromStream | 忽略传入Stream；使用**进程共享**System.Random(12345) | HostShuffleRandom显式复刻该算法；ProcessHostShuffle为共享实例 |
| Intel候选下标洗牌 / Engine随机目标 | 走Core.Rng | 原语已具备；尚未移植对应卡牌效果 |

范围边界刻意保持参考行为：Next(1)不消耗，但BridgeRange(x,x)消耗一次；范围宽度int溢出为0时先消耗一次，再除零。某些其他溢出范围可能产生区间外结果，也保留，不能“修好”后改变兼容性。没有把该取模分支改成无偏拒绝采样。

宿主流不是每局一条。并发多局时，仅保存单局seed不足以重放它，还需要整个进程内宿主调用顺序和全局状态；不能在开始/恢复某一局时擅自重置共享流。ProcessHostShuffle串行保护每次操作，但并发调度顺序仍需调用方记录。

## 状态与迁移

- seed改为参考相同的**有符号32位整数**，支持负数及int.MinValue；拒绝超范围JSON，避免悄悄截断。
- LogicRandomState包含配置标识、初始seed、S0、S1、RawDraws（包含8次预热）。捕获和恢复本身不前进随机流。
- HostRandomState包含56项表、两个索引及RawDraws，摆脱对System.Random私有布局的依赖。
- State.SchemaVersion=2；规则版本subset-v0.2-rng1；新的普通/编码回放显式记录RandomProfile。旧SplitMix64录像不会被静默按新算法解释，需用旧构建重放或重建样例。
- 导入fyserver已经洗好的牌库时，**不能从牌序或match_id猜出随机状态**。FyBootstrap.logic_random必须由已知初始化过程或检查点提供；缺省为null，明确表示未知。当前无随机效果的小卡池仍可运行；将来添加随机效果必须拒绝未知随机状态。

## Rust的范围与选择

Rust模块位于`Tools/HeadlessRngRust`，无第三方crate，C ABI v1，用调用方持有的24字节状态，不分配堆内存。导出初始化、原始数、Next、闭区间和整数排列；洗牌批量跨FFI，避免每个交换都调用P/Invoke。当前Windows x64发布DLL为112 KiB。

规则、HTTP、宿主兼容流仍在C#；这不是“整个客户端已经Rust化”。引入Rust的收益是把位运算兼容契约隔离成可复用内核，并验证跨语言的一致性；没有证据说明它已经降低整个.NET进程的内存或提高整体吞吐。

默认C#后端不强制部署原生库。使用Rust必须明确选择；库缺失或ABI错误直接报错，不静默回退：

```powershell
# 在restored目录构建；不联网获取crate
pwsh -NoProfile -File Tools\HeadlessRngRust\Build.ps1

$env:HEADLESS_RNG_BACKEND='rust'
Tools\HeadlessKards\out\HeadlessKards.exe selftest

$env:HEADLESS_RNG_BACKEND='managed'
```

两个后端使用相同状态/回放格式，可跨后端恢复。FY消息的编码盐由FyCodec单独产生，不使用对局流或宿主流；传输编码不应改变卡牌效果随机数。

## 逐步审计与恢复

```powershell
Tools\HeadlessKards\out\HeadlessKards.exe rng-trace Tools\HeadlessKards\random-trace-example.json Tools\HeadlessKards\artifacts\random-trace.json
```

每份trace模型是一整个新参考进程：`seeds`可定义多局，各局有自己的Core流，但全部HostShuffle共用一条宿主流。结果记录每步输出、Core状态、消耗计数和最终完整检查点。闭区间除零作为记录中的error保留，随后可以检查错误已经消耗了哪一个随机数。

恢复时提交`{"seeds":null,"operations":[...],"initial":上次输出的final}`，恢复整个上下文；不允许同时给seed重新初始化。HostShuffle步骤的stream标识不选择另一条宿主流，这正是参考实际行为。

## 交叉验证

`Tools/HeadlessRandomParity`直接编译链接未修改的参考Core/Rng.cs、Kismet/Host.cs及其值模型，不编译全量卡牌，也不依赖UAssetAPI包。

- 263个种子，每个320步混合操作，逐步检查输出、参考私有S0/S1与C#/Rust状态。
- 包括负种子、无消耗边界、拒绝采样边界、反序范围、int溢出、除零后的状态及跨后端恢复。
- 700次跨两个实际Host实例的交错洗牌，验证静态共享System.Random序列。
- 引擎开始牌库顺序及最终随机检查点，与参考Rng直接执行左/右洗牌一致。
- 整份trace跨后端输出相同，完整上下文分段恢复也得到同一检查点。

EngineHost闭区间分支采用其固定源码中的精确表达式作独立oracle，**没有编译完整EngineHost**；报告单独标明。测试验证的是给定串行调用轨迹，不声称涵盖原版所有卡牌或已经实机同步。

本次验证结果：随机差分279,585项通过；C#和Rust各通过128项规则检查、3,924项协议检查及26项HTTP检查；真实fyserver Codec/DTO源码互操作2,095项通过。两后端示例trace文件SHA256完全一致：`8E7FE27DBB95C0E3EE39DACD3B8F63E18AFE1631E2AEB2C517EBFC87FB4E937E`。Rust生成的普通和协议回放均通过C#后端验证。

报告与逐步轨迹位于本目录`artifacts`，其中`random-parity-report.json`记录参考提交和验证边界，`random-trace-managed.json`及`random-trace-rust.json`可逐步检查输出与状态。
