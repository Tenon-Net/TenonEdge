# 已解码 LoRa 应用数据接入

对应总设计 §21、T2-07，以及 LR-AT 软件子场景。**本目录是人工样本与软件链路测试，不是无线实测或厂商兼容清单。** 实际命令、环境、结果与未覆盖范围统一见 [实施状态 §2.67](implementation-status.md#267-t2-07-已解码-lora-样例与真实-mqtt-软件链路)。

## 1. 接入边界

```text
传感器 → 无线网关 / 网络服务器 / 上游解码器
        → 已解码 JSON → MQTT Broker → TenonEdge MQTT 输入
        → 字段选择与必填校验 → 通用 MQTT 输出
```

TenonEdge 不实现 LoRa 射频、LoRaWAN 入网、网络服务器、二进制传感器解码或下行控制。本样例不安装 ChirpStack，也不接收它的原始 Gateway Bridge 帧。ChirpStack 的 HTTP 集成是推送，不等同于首版 HTTP 拉取；不能把其推送地址填成一个尚不存在的 TenonEdge HTTP 接收接口。

现场先确认：上游已解码；输出 JSON 而非 protobuf；报文路径、类型、单位、设备绑定和时间语义与设备的实际解码器一致。不能从本例的字段名推断其他设备的单位或量程。

## 2. 仓库样例

[samples/lora/](../samples/lora/) 中所有标识、时间和测量值均为人工构造，没有客户数据、设备密钥或真实凭证。正常样本只展示应用事件的相关字段子集，不是 ChirpStack 的完整事件导出。

| 文件 | 用途 |
|---|---|
| `soil-001.json` | 人工温度 23.7 °C、湿度 41.5%、电池 92% |
| `soil-002.json` | 第二台逻辑设备，18.2 °C、55.5%、81% |
| `missing-decoded-object.json` | 只有 `data`，没有解码业务对象；不能把 Base64 当作正常测量值 |
| `invalid-temperature.json` | 温度类型/内容非法，不能生成正常值或补上旧值 |
| `runtime-seed.json` | 实际后端运行计划格式：两个精确 MQTT 输入，共享一个连接，扇入一个通用输出，不合并消息 |

配置的三项输出映射全部 `required: true`。输入层的字段不存在表示“未携带”；**路线必填规则**拒绝缺失/转换失败的整条输出路径。不要删掉这些规则后仍声称“缺少解码会被拒绝”。失败会进入运行事件和路径计数，不伪造 0，不回填上一条值。明确的数据拒绝仍可确认 QoS 1，以免永久重投无效数据。

### 设备与主题

输入分别绑定 `soil-001/up`、`soil-002/up`，并使用下列精确主题：

```text
application/00000000-0000-0000-0000-000000000001/device/0011223344556677/event/up
application/00000000-0000-0000-0000-000000000001/device/1122334455667788/event/up
```

应用 UUID 与 DevEUI 均须换成现场真实值，DevEUI 大小写以实际主题为准（参考的上游默认使用小写）。**逻辑设备身份来自输入配置，不由 `deviceInfo.deviceName` 或报文里的 DevEUI 自动创建设备、自动分流或合并。** 若上游把报文发到错误主题，本样例不会另外核验报文 DevEUI 与主题的一致性；须保证上游发布和 Broker ACL 正确。

输出使用 `tenon/lora/{deviceKey}`，标准信封包含 `deviceKey`、`values`、`quality`、`collectedAt`、`timeSource` 等；湿度重命名为 `soilMoisture`。输入与输出主题不能互相匹配，避免回流。

### 时间与状态

样例将 `/time` 按 `rfc3339` 映射为 `source: "upstream"`，不是 C# 名称 `Upstream`。人工时间固定为 `2026-01-01T00:00:00Z`，不是实际采集时刻。ChirpStack 的事件时间也不能未经核对直接称为传感器采样时间；网关接收时间另存。

没有配置时间映射时，使用网关接收时间及其校时状态；无效/过度未来时间按现有时间规则回退并计数。保留消息默认忽略，本例没有打开 `processRetained`。低频上报或休眠不等于离线，Broker 连接活跃也不等于传感器活跃；本例不新增设备超时判定或下行修改上报周期。

## 3. 运行与复现

最直接的验证入口（Linux、SDK 10.0.401、可用的测试 Docker 环境）：

```sh
dotnet test tests/IntegrationTests -c Release --no-restore \
  --filter FullyQualifiedName~LoraMqttSampleTests
```

先按项目工程要求还原锁定依赖。测试复用真实 Mosquitto 夹具和实际产品 Host，自动创建独立数据目录，通过首次设置/会话/凭证 API 初始化，再载入本目录的种子和报文。它只替换测试 Broker 端口、为主题加隔离前缀；字段定义、必填映射、时间映射和报文均直接读取仓库样例。测试不运行 LoRaWAN 网络服务。

手动使用种子时：

1. **仅在专用测试数据目录操作**。这是运行计划种子，不是合并导入功能；不要用它替换正在运行的生产配置。
2. 复制 `runtime-seed.json`，核对连接 1 的 Host、Port、TLS、`usePassword`，以及两台设备的精确订阅主题。密码不能写入种子。
3. 先不指定种子启动 Host，完成管理员初始化，按现有连接凭证 API 为连接 1 保存与接收方地址一致的凭证；再正常停机。API 说明以当前 OpenAPI 为准，须遵守会话/CSRF 和权限规则。
4. 下次启动指定 `TenonEdge:Runtime:SeedSnapshot` 为该副本的**绝对路径**。用 `TenonEdge:DataDirectory` 指向同一测试目录。默认 `127.0.0.1:1883` 只是示例，使用本机 Broker 还须由管理员放行该出站端口；不能放行管理端口或关闭 TLS 验证。
5. 用独立 MQTT 客户端先订阅 `tenon/lora/+`，再向两条输入主题发布相应 JSON。须使用被授权的账号，不把密码放入仓库、样例或命令历史。
6. 查看标准信封的设备、字段与时间，以及 `/api/edge-admin/v1/runtime/status` 中对应 `path/soil-001/up/1/dataRejected` 等计数。网页仍在 T3，不能把此种子称为配置页面。

种子内容变化会生成新版本；未变化的种子不会每次启动重复导入。配置、凭证、出站规则及更换接收方的行为仍按既有设计执行。

## 4. 可靠性与安全前提

- 上游**发布**和网关订阅都须使用 QoS 1，才能建立相应可重投路径。只把订阅改为 QoS 1，不能把上游 QoS 0 升级为可靠投递；参考的 ChirpStack 配置默认发布 QoS 为 0，必须现场核对。
- Broker 须支持双方使用的 MQTT 版本；本产品使用 3.1.1，参考的 ChirpStack 源码使用 5.0。持久会话、客户端身份、积存条数/字节、消息大小、磁盘持久化及进程崩溃窗口必须另行核验，不能从“连通”推断“断电不丢”。
- Broker 之前未产生、无线丢失、解码失败或上游未持久化的事件，不在 TenonEdge 队列的补发保证内。重连可能重复输入，首版不按 DevEUI/fCnt/deduplicationId 做无线消息去重，也不提供恰好一次承诺。
- 为网关配置最小权限：只读对应应用上行主题，只写自己的输出主题。若共用一个连接账号，它同时需要这两类权限；本测试夹具不证明现场 ACL 已正确配置。
- 不采集或提交 AppKey、NwkKey、AppSKey、Join-Server 封装密钥、真实凭证。上游事件可能携带敏感上下文，进入样本、调试/失败报文前须脱敏或在上游去除；本产品不会因为字段映射未输出该字段，就保证原始报文不被保存。

## 5. 来源与验收边界

参考总设计 §21，以及：

- [ChirpStack MQTT 集成](https://www.chirpstack.io/docs/chirpstack/integrations/mqtt.html)
- [ChirpStack 事件类型](https://www.chirpstack.io/docs/chirpstack/integrations/events.html)
- [ChirpStack 设备配置与解码](https://www.chirpstack.io/docs/chirpstack/use/device-profiles.html)

本轮静态回读本地 ChirpStack 提交 `3237e30d7` 的 `api/proto/integration/integration.proto`（UplinkEvent 的 `object` 为可选解码结果）及 `chirpstack/src/config.rs`（主题模板、JSON/QoS 默认值）。该提交的版本号为 `4.20.0-test.2`，不是本项目验证过的稳定版本。未复制其源码或部署其服务。

LR-AT-01/02/03 的软件子场景由上述测试覆盖，具体通过记录仅见实施状态。LR-AT-04 的实物上报周期、LR-AT-05 的实际无线补传、LR-AT-06 的真实传感器—无线网关—网络服务链路均不能由本测试替代。当前没有厂商型号兼容声明。未来实物记录须包含各固件/服务/解码版本、区域、字段单位、时间含义、脱敏样本和证据，不用静态样例冒充实物接入。
