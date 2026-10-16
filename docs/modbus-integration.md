# Modbus 后端接入与软件验证

T2-02 已接入产品 Host 的 `input.modbus`，支持只读 TCP 与 RTU over TCP。本文保留 T2 的运行计划种子配置与后端软件验证；T3 的[网页配置 Modbus → 映射 → HTTP](modbus-web-workflow.md)已另有不手写业务 JSON 的操作与软件验收场景。完整验收与未覆盖场景统一见[实施状态](implementation-status.md)，不将软件从站视为实物兼容证明。

## 软件验证

已还原锁定依赖、安装 SDK 10.0.401 的 Linux 开发环境可运行：

```sh
dotnet test tests/IntegrationTests -c Release --no-restore \
  --filter FullyQualifiedName~ModbusHostTests
```

测试自动创建独立数据目录，初始化本地管理员，加载种子。回环模拟从站返回手写已知寄存器，产品 Host 采集、构造消息、持久化到发送缓存，再由正式 HTTP 输出投递给独立 TCP 接收端；验证整数精度、时间来源、输出确认后删除队列记录，以及认证管理 API 中的采集计数。两种帧格式均测试，**不是 PLC、实物串口服务器或厂商兼容证明**。

## 运行计划格式

以下地址与点位是人工示例，必须按设备手册及现场网络修改；不应直接用于生产。先复制到独立测试目录的 JSON 文件：

```json
{
  "schemaVersion": 1,
  "units": [
    {
      "kind": "input.modbus",
      "key": "meter/fast",
      "configuration": {
        "connectionId": 1,
        "host": "192.168.10.10",
        "port": 502,
        "frameFormat": "tcp",
        "unitId": 1,
        "periodMs": 5000,
        "connectTimeoutMs": 5000,
        "readTimeoutMs": 5000,
        "requestIntervalMs": 0,
        "rtuSilenceMs": 500,
        "points": [
          { "key": "energy", "functionCode": 3, "address": 0, "type": "uint64", "byteOrder": "ABCD" }
        ]
      }
    },
    {
      "kind": "output.http",
      "key": "sink",
      "configuration": {
        "connectionId": 2,
        "baseAddress": "http://192.168.10.20:8080/",
        "path": "events",
        "inputs": ["meter/fast"]
      }
    }
  ]
}
```

- `address` 是明确的**零基协议偏移**，不是 `40001` 手册编号。功能码 01/02 的类型只能为 `bool`；03/04 支持 `int16`、`uint16`、`int32`、`uint32`、`float32`、`int64`、`uint64`、`float64`，以及 `bit` + `bit: 0..15`。32/64 位类型必须指定 `ABCD`、`CDAB`、`BADC` 或 `DCBA`；不要按本机端序猜测。
- RTU over TCP 使用 `frameFormat: "rtu-over-tcp"`、UnitId 1—247；TCP 为 0—255。不是串口直连，不开放写入功能码。超时、请求间隔与静默窗口须按设备实测；默认 5 秒周期不保证容得下多个 5 秒读取超时。
- 同一物理连接的采集组引用同一个 `connectionId`，物理参数保持一致。一个设备的组使用相同连接和 UnitId，字段 key 在该设备内唯一。不同协议不能共用连接编号。
- 部分失败只输出当前成功字段，失败字段列入 `quality`；整组失败不生成正常消息。64 位整数原文无损，JavaScript 接收端须使用支持大整数的解析方式，或在输出映射中转为字符串。
- 先不指定种子启动 Host，通过现有 API 完成首次管理员设置，再正常停机。使用同一专用测试数据目录，在下次启动通过 `TenonEdge:Runtime:SeedSnapshot` 指定 JSON **绝对路径**；目录由 `TenonEdge:DataDirectory` 指定。这是启动种子，不是在线合并导入，不要替换生产配置。
- 出站地址策略仍生效：私网默认允许，公网需要白名单，本机端口须由管理员显式放行，管理端口不可例外。HTTP 认证凭证通过现有凭证 API 保存，禁止写入种子；本例匿名输出只适合隔离测试接收端。
- 管理诊断：登录后读取 `/api/edge-admin/v1/runtime/status`。正常输出是否收到，应由独立接收端及发送缓存状态共同验证，不以健康检查或单元启动成功代替。

多组不同周期、压力/资源、恢复和物理设备验收仍按 T2-02/T2-08 继续，不将本页当作完整阶段验收。
