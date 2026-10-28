# Rust 对局核心与 C#/Lua 卡牌

当前实现了可运行的 `C#/Lua → IR → Rust 对局` 链路。规则版本 `subset-v0.4-aggro1`。

验证结果：29项Rust语义测试、C#/Lua 25种子共3652项检查、原C#规则50种子共11284项差分、3924项协议检查、26项HTTP检查通过。日德快攻25种开局通过1578条规则行动及回放；隔离fyserver双机器人真实HTTP对战完成81条规则行动，最终状态一致。纯Rust CLI完成66行动的脚本卡对局及逐行动重放；Lua导出器拒绝无限循环、外部随机调用和未知操作。报告在`../HeadlessKards/artifacts`。

- `headless_core.dll`：部署、移动、攻击、回合、合法行动、AI，以及效果解释器。C#网络程序默认使用它。
- `headless-sim.exe`：同一核心的独立JSON行协议入口，不需要.NET、Lua、UE或GPU。
- `card-lua.exe`：Lua 5.4卡包导出工具，只在制作/加载卡包时使用，不进入核心分发依赖。
- `../CardAuthoring`：无NuGet依赖的C#作者SDK；`../CardExamples/CSharp`是可编辑示例项目。
- `../CardExamples/cards.lua`：等价Lua示例。

玩家提供的日德快攻21种卡已移植，加上总部与两种衍生卡共24个定义；源码在`../CardExamples/aggro.lua`。网络启动与验证边界见[AGGRO.md](../HeadlessKards/AGGRO.md)。尚无原版客户端实战验证，不应把双机器人一致当成已经完全兼容原版。

## 构建与导出

```powershell
cd D:\KARDS\default\restored
pwsh -NoProfile -File Tools\HeadlessCore\Build.ps1
```

首次需下载Cargo.lock中锁定的依赖（`cargo fetch --manifest-path Tools\HeadlessCore\Cargo.toml`）；之后构建脚本使用offline。Lua及核心使用不同target目录，核心默认构建不启用Lua VM。已生成的工具在 `Tools\HeadlessCore\out`，C#网络程序仍在 `Tools\HeadlessKards\out`。

单独导出：

```powershell
dotnet run --project Tools\CardExamples\CSharp\CardExamples.csproj -c Release -- Tools\CardExamples\pack-csharp.json
Tools\HeadlessCore\out\card-lua.exe Tools\CardExamples\cards.lua Tools\CardExamples\pack-lua.json
Tools\HeadlessKards\out\HeadlessKards.exe pack-validate Tools\CardExamples\pack-csharp.json Tools\CardExamples\pack-csharp.normalized.json
Tools\HeadlessKards\out\HeadlessKards.exe script-test Tools\CardExamples\pack-csharp.json Tools\CardExamples\pack-lua.json
```

Lua可以在导出阶段用循环、函数和表生成卡牌。C#可以用普通代码构建SDK对象。导出的是统一、版本化的数据，不会把任意源代码直接转译成Rust。Rust加载时验证未知字段、指令、引用与图规模；不支持的机制需扩展原语。

## 跑脚本卡对局

```powershell
$env:HEADLESS_CARD_PACK='D:\KARDS\default\restored\Tools\CardExamples\pack-lua.json'
Tools\HeadlessKards\out\HeadlessKards.exe selfplay-config Tools\CardExamples\match.json Tools\HeadlessKards\artifacts\script-demo-replay.json
Tools\HeadlessKards\out\HeadlessKards.exe replay Tools\HeadlessKards\artifacts\script-demo-replay.json
Remove-Item Env:HEADLESS_CARD_PACK
```

卡包目前替换整个模拟卡库，示例因此包含原6定义与3张自定义卡。使用其他卡包时需包含开局使用的总部和牌组定义。修改卡包会改变catalog_hash；回放必须加载相同卡包及规则版本。旧v0.2录像需旧构建，不应拿新规则运行后要求哈希相同。

## 卡牌示例

C#：

```csharp
var enemies = new Query(Side.Enemy, Area.Board, UnitsOnly:true);
new Card("my_unit", "My unit", Kind.Infantry, 1, 1, 1, 3, true,
    [new(Trigger.Deploy, [Fx.Damage(Target.Random(enemies), 2)])]);
```

Lua效果部分：

```lua
{on="deploy", effects={{
    op="damage", amount=2,
    target={kind="random", query={side="enemy",area="board",units_only=true}}
}}}
```

目标查询、随机消耗、伤害、死亡及后续触发全部在Rust执行。C#/Lua不在战斗阶段回调，不持有可写对局状态。示例另外覆盖死亡抽牌和创建单位。

## 当前IR v1

- 触发：deploy、enter_play、start_turn、end_turn、after_attack、damage、death、other_death、frontline_changed。
- 目标：来源、事件对象、玩家所选、遍历绑定对象、刚生成卡牌、已保存目标集、查询全体和随机目标。指定目标卡需设置definition.targeting；targeting_optional允许没有候选目标时部署单位。
- 查询：友敌、区域、单位/类型、费用、最大攻击、阵营、指定卡名、空军、相邻卡牌、排除来源。reference配置按card_id排序；client配置保留卡牌数组次序，仍需实机核对是否与原版候选数组一致。
- 表达式：整数、查询计数、攻击力、事件伤害量、加法、乘法；条件支持存在、事件对象匹配、钉住、前线归属、比较、非、与。
- 操作：伤害、消灭、属性修改、抽牌、指定随机检索、随机弃牌、创建、免费入场、压制、钉住、行动费用修改、Kredits/费用槽、条件、目标快照、有限重复和延迟队列。
- 持续攻击力光环在部署、移动、压制和死亡时重新计算；战斗状态包含烟幕、奋战、钉住和飞机规则。
- 最大4096效果节点、24层嵌套、每次事件事务10000步；动态循环/触发失控会报卡牌和触发点。
- 行动在状态副本上执行；错误不提交扣费、伤害、创建或随机状态。队列和循环预算在Rust中执行。

## 明确边界

这里只覆盖当前卡包所需规则，并未实现原版完整卡池。守护、重甲、伏击及其他未涉及机制仍不支持。原版客户端随机候选顺序、回合触发细节和位置编号仍需实战核对。

脚本核心支持reference与client两种明确区分的随机配置。客户端流已整合蓝图额外随机消耗，独立保存在battle检查点中；原六定义示例保留reference契约。高级卡包的生成ID使用turn*1000+回合内序号；原示例未启用battle元数据时保留max+1。

网络动作码优先读取卡包的native_code，并在开局扫描双方完整卡池；不支持的卡牌停止对局。原版客户端仍需要拥有一致的卡牌资产与规则，自定义IR本身不会自动提供客户端蓝图。

## 对照与内部接口

默认`HEADLESS_RULES_BACKEND=rust`；`managed`只用于原六定义对照，不支持脚本卡，且仍通过Rust统一验证/计算卡库指纹，因此不是缺少DLL时的降级方案。`HEADLESS_RNG_BACKEND`影响旧C#对照和随机审计工具，Rust规则核心始终使用其内部Rust RNG。`rules-parity-test`对照旧C#子集的逐行动结果。

FFI为UTF-8 JSON请求/响应，按整次行动调用；C#使用P/Invoke并由Rust释放响应内存。CLI每行接收同一请求：`op`为new/legal/think/apply/begin/validate_pack，带cards与相应state/setup/command；响应为`{ok,result}`或`{ok:false,error}`。当前接口传递完整状态，尚未优化成Rust常驻会话句柄，也未证明整体吞吐/内存已优于旧版。

序列化使用[Serde](https://serde.rs/derive.html)，Lua导出器使用[mlua](https://docs.rs/mlua/0.12.2/mlua/)。
