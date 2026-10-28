# HeadlessKards：小卡池无界面规则与AI原型

当前规则版本 `subset-v0.4-aggro1`。默认由Rust执行对局规则、合法行动、AI和随机；C#/.NET 10负责网络协议、HTTP、命令行与DTO转换。保留旧C#规则作为六定义对照后端。均不启动UE、不加载美术、不需要GPU。C#没有第三方NuGet依赖；Rust使用锁定的Serde/JSON/SHA依赖。

已实现C#/Lua卡牌导出与Rust效果解释器，并移植用户日德快攻的21种卡、总部与两种衍生卡。该卡包已通过语义、回放和隔离fyserver双机器人测试；尚未核对真人原版客户端。启动与限制见[AGGRO.md](AGGRO.md)，原语见[HeadlessCore/README.md](../HeadlessCore/README.md)。

已新增可登录fyserver、创建机器人测试卡组、加入对战码并进行对局的网络层。**与原版KARDS客户端的完整对战尚未验证。** 日德快攻请用`Start-AggroBot.ps1`；旧教学卡组和协议背景见[NETWORK.md](NETWORK.md)。`serve`仍为内部HTTP测试接口；没有替换正式服务器或游戏exe/pak。

## 运行

PowerShell：

```powershell
Set-Location D:\KARDS\default\restored\Tools\HeadlessKards
.\out\HeadlessKards.exe selftest
.\out\HeadlessKards.exe selfplay 42 artifacts\sample-replay.json
.\out\HeadlessKards.exe replay artifacts\sample-replay.json
.\out\HeadlessKards.exe serve 1146
```

`out`中exe依赖同目录dll、cards.json和.NET 10 ASP.NET Core运行时，不能只复制exe。可直接使用本机已安装的SDK执行`dotnet out/HeadlessKards.dll ...`。修改源码后：

```powershell
dotnet build -c Release -o out
```

运行`serve`后仅监听`http://127.0.0.1:1146`；Ctrl+C结束。该端口与正式fyserver的1145区分。测试服务不持久保存会话，退出后需通过回放恢复；最多保留128个测试会话，用DELETE清理。

## 首批卡牌

这些是原版卡牌内部ID，不是本次新加进游戏的资产。只移植以下六个定义，未导入完整卡库：

| ID | 类型 | 部署费 | 行动费 | 攻/防 | 额外规则 |
|---|---|---:|---:|---|---|
| card_location_london | 总部 | — | — | 0/20 | 总部，实际显示名ALEXANDRIA |
| card_location_berlin | 总部 | — | — | 0/20 | 总部，实际显示名DANZIG |
| card_unit_12th_infantry_regiment_tut | 教学步兵 | 1 | 1 | 2/2 | 无额外事件 |
| card_unit_121st_regiment_tut | 教学步兵 | 1 | 1 | 2/2 | 无额外事件 |
| card_unit_panzer_35_t_tut | 教学坦克 | 2 | 1 | 2/2 | 坦克可移动并攻击 |
| card_unit_t_34 | 坦克 | 5 | 2 | 5/5 | 闪击 |

Flora解析库原始导出在`Research/Headless`，`verify_catalog.py`验证所有数值、类型、闪击状态及这些卡没有蓝图事件。T-34的`subtype.t34`标签在当前卡池中没有响应者，未建立通用GameplayTag系统。最初检查的九五式、三号坦克有额外触发效果，明确未加入支持清单。

## 已实现的子集

- 独立状态与合法行动枚举；JSON严格解析枚举及未知字段。遇到未知卡牌直接拒绝，不按空白单位猜测。
- Kredits增长/刷新，当前模型上限12；部署费与行动费分别结算。
- 支援区最多5张含总部；中央前线最多5张且同一时刻只允许一方占据；手牌上限9。
- 部署疲劳、闪击、步兵移动/攻击互斥、坦克各移动及攻击一次。
- 当前两类单位射程均1：从支援区只能攻击敌方前线，从前线可攻击敌方前线/支援区/总部。
- 同时伤害与反击、死亡移入弃牌、总部被消灭后的胜负。
- 回合切换、抽牌、满手烧牌、逐次递增的空牌库疲劳。
- 确定性贪心AI：优先击杀总部，评估交换、推进前线及部署。只用合法行动，无搜索树；不是强度成熟的AI。
- 行动按revision校验，过期/重复提交不会再执行；状态以复制方式原子更新。
- 固定版本kards-sim兼容洗牌与随机状态、卡库指纹、逐行动状态SHA256和可验证回放。详见[RANDOMNESS.md](RANDOMNESS.md)。

**当前开局是测试模型**：默认12张单位牌，双方先取4/5张，左方1 Kredit开始，首个回合不再抽牌。没有原版调度、40张组牌/国家/稀有度限制，也未验证完整原版起手、回合、行动限制的所有细节。用途是先验证白名单规则与AI闭环。

不支持指令、部署触发、亡语、守护、重甲、轰炸机/战斗机/炮兵、光环、随机卡牌效果、战役升级、兔子协议、PE-BOX及天启鸟脚本。添加这些卡前必须先增加规则处理器，不能仅往cards.json填入其身材。

## HTTP内部接口

| 方法与路径 | 功能 |
|---|---|
| GET /health | 版本和卡库指纹，明确native_client_protocol=false |
| GET /v1/cards | 六项卡牌定义 |
| POST /v1/matches | 新建测试会话，JSON如`{"seed":42}` |
| GET /v1/matches/{id} | 当前状态 |
| GET /v1/matches/{id}/legal | 当前revision及合法行动 |
| GET /v1/matches/{id}/think | AI建议；只读取，不执行 |
| POST /v1/matches/{id}/actions | 提交行动并更新状态 |
| GET /v1/matches/{id}/replay | 导出含逐步哈希的完整回放 |
| DELETE /v1/matches/{id} | 删除会话 |

PowerShell完整AI一手示例（先在另一个终端运行serve）：

```powershell
$base='http://127.0.0.1:1146'
$game=Invoke-RestMethod "$base/v1/matches" -Method Post -ContentType 'application/json' -Body '{"seed":42}'
$id=$game.id
$plan=Invoke-RestMethod "$base/v1/matches/$id/think"
$body=@{expected_revision=$plan.revision;command=$plan.action}|ConvertTo-Json -Depth 5
Invoke-RestMethod "$base/v1/matches/$id/actions" -Method Post -ContentType 'application/json' -Body $body
```

自定义测试牌组通过`left_deck`和`right_deck`数组传内部卡牌ID；只接受白名单单位，长度1..200是测试工具限制。标准JSON：

```json
{"seed":7,"left_deck":["card_unit_12th_infantry_regiment_tut","card_unit_t_34"],"right_deck":["card_unit_panzer_35_t_tut","card_unit_t_34"]}
```

## 已验证

- Release编译0警告、0错误。
- `selftest`：128项检查，含100个种子AI自对战、逐步回放校验、未知卡牌、过期行动、容量、部署疲劳、闪击、步兵/坦克行动差异、反击双方死亡、总部胜负、满手和疲劳。
- `Smoke-Test.ps1`启动独立临时端口并自动结束自己启动的进程：26项HTTP检查，一局75行动至胜负、回放独立校验通过。输出位于artifacts。
- 未做原版客户端的差分对局测试，尚不能证明与原版执行结果一致。

## 接fyserver仍需完成

已检查用户指定仓库master提交`4dd0a0891f56b54e3bd783ee8f22d9acd757ef12`及Flora的BP_OnlineMatch：

1. 在专用测试模式中使用白名单牌组，替换当前pw分支自动跳过AI回合的行为；不能让引擎接受当前默认的完整德国/芬兰牌组。
2. 将双方起始卡牌ID、牌序、调度、Kredits、行动次数与本引擎状态对应，而不是另起一套不匹配的ID和洗牌。
3. 适配XActionPlayCardFromHand/PC、XActionMoveCardToLine/ML、XActionAttackCard/AC及回合动作。客户端compact参数与本程序Command不是相同格式；出牌还有卡牌代码、chooseOneIndex及敌方总部防御校验字段。
4. 接收玩家行动与sub_actions，并与引擎状态逐步对比；决定哪些结果由本地subaction计算，防止抽牌、伤害、回合处理执行两次。
5. 在一局真实客户端对局中逐行动比较状态哈希/规范化快照，出现差异立即停止AI，不继续用错误状态决策。

以上列表是首版的接入目标；当前已完成的协议适配和剩余限制见下一节。仍未挂入正式服务。

## 协议层第二阶段（2026-10-28）

此阶段最初使用subset-v0.1，现已升级为subset-v0.2-rng1随机配置。适配代码位于FyProtocol.cs、FyCodec.cs、NativeSnapshot.cs。Flora MCP离线时，使用同一套本地解析库只读导出BP_OnlineMatch到Research/Headless/Protocol。

已完成：

- 导入fyserver `match_and_starting_data`布局，保留实际卡牌ID和牌库顺序，不重新洗牌；只支持已完成调度、尚未开始第一回合的白名单卡池。
- 强制声明`mulligan_complete=true`和`first_turn_draws`。`last_seen_action_id`可显式设置调度后行动游标；不是由服务器current_action_id猜测。中途快照不能用于重新初始化，请从开局重放。
- 适配PC/ML/AC及对应完整XAction名称，读取compact action_data字符串字段；验证双方玩家ID、卡牌代码、目标、插入位置、回合和总部防御校验。卡牌代码来自原版deckCodeIDsTable2。总部校验键按SetMatch_ID_AsTwoDigits取match_id字符串最后两位。
- 拆分XActionEndOfTurn和XActionStartOfTurn。结束只切换待开始的一方；下一条开始行动才抽牌、刷新Kredits和行动次数。
- 支持fyserver的Base64/XOR消息格式，区分服务器action_id和24位send_action_id；拒绝截断、伪造长度及发送ID不一致。
- 行动游标严格递增，完全相同的重试幂等；同ID不同内容、错序、错误操作者、错误校验值均拒绝，失败不修改状态。
- 支持编码消息流回放和快照差分。快照不一致时，HTTP会话停止AI规划及后续行动，仍允许导出记录和删除。

适配边界：仅接受`local_subactions=true`和空sub_actions。当前不发送也不支持XStartOfGame（原版对局以首个XActionStartOfTurn为动作1）、调度行动、ActionEndMatch、非空sub_actions及额外长字段混合payload；这些内容不会被静默丢弃。完整开局握手、服务端胜负上报、原版客户端的回包与现场状态差分仍需实现/验证。棋盘插入和移除后的location_number归一化也尚未与实机核对。

新增命令：

```powershell
.\out\HeadlessKards.exe protocol-test
.\out\HeadlessKards.exe wire-fixture artifacts\native-bootstrap.json
.\out\HeadlessKards.exe wire-selfplay 42 artifacts\sample-wire-replay.json
.\out\HeadlessKards.exe wire-replay artifacts\sample-wire-replay.json
.\out\HeadlessKards.exe compare-snapshot artifacts\sample-wire-replay.json artifacts\native-snapshot.json
```

wire-fixture生成的是**合成的、符合服务端DTO布局的测试开局**，不是从真实客户端抓取的数据。wire-selfplay仍是两个本程序AI互相执行，不能证明原版客户端已经接受动作。compare-snapshot返回0表示所比较字段一致，2表示差分或发现未支持效果；原版卡牌JSON不包含所有移动/攻击次数，比较报告会明确注明此限制。

快照wrapper：`cards`为GetMatchCardsAsJsonString输出的数组（或该数组的JSON字符串），外加`kredits_left`、`kredits_right`、`max_kredits_left`、`max_kredits_right`。核对卡牌集合、ID、内部名称、攻防、归属、位置和可选位置序号；遇到守护、重甲、光环增益、战役升级等当前未支持状态会报告unsupported。

新增内部HTTP接口（仍只监听127.0.0.1）：

| 方法与路径 | 功能 |
|---|---|
| POST /v1/native/matches | 导入FyBootstrap，返回会话ID和checkpoint |
| GET /v1/native/matches/{id} | 状态、待开始标记、游标、位置和哈希 |
| GET /v1/native/matches/{id}/think | AI建议动作及编码字符串a，不执行 |
| POST /v1/native/matches/{id}/actions | 用`{"a":"编码消息"}`接受一条已分配action_id的动作 |
| POST /v1/native/matches/{id}/compare | 核对快照，发现差分后停止该会话 |
| GET /v1/native/matches/{id}/replay | 导出编码消息回放 |
| DELETE /v1/native/matches/{id} | 删除会话 |

导入并执行一次原生格式动作：

```powershell
$base='http://127.0.0.1:1146'
$body=Get-Content artifacts\native-bootstrap.json -Raw
$game=Invoke-RestMethod "$base/v1/native/matches" -Method Post -ContentType 'application/json' -Body $body
$plan=Invoke-RestMethod "$base/v1/native/matches/$($game.id)/think"
Invoke-RestMethod "$base/v1/native/matches/$($game.id)/actions" -Method Post -ContentType 'application/json' -Body (@{a=$plan.a}|ConvertTo-Json)
```

验证情况：

- 协议测试3955项检查，含50个种子的完整编码对局；分离回合后的规则状态与内核直接执行逐行动一致。
- Tools/HeadlessKardsInterop直接编译链接克隆仓库中的CodecService.cs、MatchModels.cs和GameConstants.cs，使用匹配的精简源生成上下文，未修改这些上游文件。2000次双向codec测试，加106条真实服务端DTO序列化消息回放，共2106项通过。
- 更新Smoke-Test.ps1：26项HTTP检查，包含编码消息完整对局、重试、回放和快照分歧后的停机。
- 上述都是本地自动化验证，尚无真实KARDS客户端的一局联调结果。下一步应在隔离测试服务中接入专用小卡池模式，补开局握手并采集逐行动状态差分。

