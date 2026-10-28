# 本地 fyserver 连接

2026-10-28更新：日德快攻24定义卡包已接入网络层，并通过隔离fyserver的双机器人测试。请按[AGGRO.md](AGGRO.md)运行`Start-AggroBot.ps1`；下文`Start-Bot.ps1`与教学卡组说明保留用于原六定义回归测试。真人原版客户端的规则与随机同步尚未核对。

已实现普通玩家连接层：POST /session 自动创建或登录独立机器人账号；用 fyserver 的 JWT 编码用户名认证；复用或创建机器人专属测试卡组；加入 battle_code 队列；等待匹配；lvl-loaded；双方调度；轮询编码动作；AI执行；胜负报告及 /post 清理。

不走 /singleplayerlobby：那个入口的 pw 对手是服务器自动跳过回合的人机，不能再同时接入本机器人。

## 运行

先启动你的 fyserver，然后执行：

```powershell
pwsh -NoProfile -File D:\KARDS\default\restored\Tools\HeadlessKards\Start-Bot.ps1
```

默认连接 http://127.0.0.1:1145，账号 headless_lcorp，对战码 LCORP。在游戏中输入相同对战码。Ctrl+C退出；匹配队列等待默认30分钟。只支持本地回环HTTP；不跟随重定向。

配置在 bot-local.json。deck_id 可指定该机器人账号已有卡组，否则创建/复用 Headless test。只操作机器人自己的卡组，不修改真人账户。输出目录按运行时间及随机后缀隔离，包含 bot.log、原始 starting-info.json、调度后的 bootstrap.json、actions.jsonl、最终 checkpoint.json 及可用 wire-replay 命令验证的 wire-replay.json。日志不保存 Authorization 或完整session响应。

## 当前必须解决的卡组限制

当前只有 README 中6项卡牌定义。默认机器人测试卡组含教学卡，重复数量也超出正常收藏组牌限制；**它只用于真实fyserver上的无头客户端互测，不是已验证能被原版客户端导入的卡组**。请不要将这个代码当作已完成的真人对战方案。

下一次真人测试前，应先提供客户端能导入的普通卡组代码，逐项审计并补齐它的规则，再让机器人使用同一套。直接把普通卡改名成教学卡、忽略能力、跳过未知动作都不能保证同步。

机器人会在发送 lvl-loaded、调度与出牌前同时扫描双方完整牌组；任何未支持卡牌都会停止并列出名字，不按白板模拟，也不会继续自动出牌。客户端导入失败与规则白名单是两个独立问题；直接修改服务器卡组不能证明客户端会接受，也不能解决缺少效果的问题。

first_turn_draws 默认 true，明确写入引导数据。尚未用原版客户端验证起手/首回合抽牌；发现状态或总部校验不一致会停止。当前没有自动重连恢复：网络故障时请保留日志分析；不能直接在打到一半的对局上重新初始化。

## 协议一致性

- 每次提交后等待服务器回传，再更新本地状态；不在POST成功时预先执行。
- 服务器 action_id 是权威编号，send_action_id 只用于自己的提交/回传关联；两者不混用。
- 重复轮询不重复执行，冲突回传、动作缺口、错误玩家、校验不一致都停止。
- POST超时或回传超时不会自动重发，避免fyserver缺少幂等去重时重复部署/攻击。
- 对局直接以先手方的XActionStartOfTurn作为动作1，不再发送XStartOfGame标记（原版对局记录中不存在该动作，客户端无法识别）。
- 导入实际调度后的牌库和替换手牌，不能把初始starting_info当作调度后的状态。
- 网络编码随机盐不占用对局随机流。fyserver生成的牌库不透露效果RNG状态，因此不会从match_id伪造种子。

## 已验证

用现有fyserver.exe的独立副本、本地端口19145、全新本地数据库，运行两个机器人作为正常玩家完成真实HTTP对战。未修改正式服务器文件或数据库。对局648741共22回合，服务器记录124条动作，正常判定right获胜。双方规则检查点在动作121相同：

`3D64ABD149C0BEEE130450C1140685942B2A96B75E60070CB9CEC7B3DB0F8AB0`

双方网络回放均通过独立重放。记录中的剩余动作是服务器的终局通知/双方终局报告。报告见 artifacts/network-integration-report.json；独立测试服务器已关闭。

这证明了与真实fyserver的网络闭环，**尚不是与原版KARDS客户端完整对局的验证**。
