# 发送队列磁盘容量估算校准（T3-07）

本估算只针对队列逻辑字节流入，给出磁盘剩余量的保守时间边界，不承诺文件系统可用空间不会被其他进程改变。诊断 API 将生成速率（队列新增冻结报文字节/秒）乘以 `OutputCapacityEstimator.CalibratedDiskAmplification = 2.3`，与目标条数、目标逻辑字节、全局逻辑字节和磁盘余量限制取最小值。当前磁盘余量还扣除配置的字节/百分比预留。未采到非零生成速率、磁盘空间不可读或空间保护已触发时，不给完整时长；保留期不等于缓存时长。

## 样本与方法

`DeliveryCacheTests.MeasuresDatabaseWalAndIndexAmplificationForCapacityCalibration` 使用生产队列写者、queue.db schema、WAL/synchronous=FULL 和真实 SQLite 文件，分别插入 300 条 1 KiB 与 64 KiB 报文，等待提交后比较 `DeliveryDraft.ByteSize` 总逻辑字节与主库 + 活跃 WAL 文件大小；随后停写者，对同一数据库执行 `PRAGMA wal_checkpoint(TRUNCATE)`，记录检查点后数据库大小。主库大小含记录、索引、页空闲空间；WAL统计这次写入的页变更，没有尝试把索引单独归因。每种尺寸仅一次批次，不代表长期稳态分布或百分位。

2026-10-19 本机实测：Ubuntu 26.04、Linux x86_64 kernel 7.0.0-31-generic、.NET SDK 10.0.401 / runtime 10.0.12、SQLite 3.51.3、ext4（测试文件位于程序集输出目录的 ext4 根文件系统，不是 `/tmp` tmpfs）。命令：

```sh
dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~MeasuresDatabaseWalAndIndexAmplificationForCapacityCalibration' \
  --logger 'console;verbosity=detailed' --results-directory /tmp/tenonedge-t3/capacity-measurement-ext4
```

| payload | 记录 | 逻辑字节 | queue.db | 活跃 WAL | 主库+WAL / logical | checkpoint 后 / logical |
|---:|---:|---:|---:|---:|---:|---:|
| 1,024 B | 300 | 397,500 | 69,632 | 477,952 | 1.378 | 1.288 |
| 65,536 B | 300 | 19,751,100 | 19,918,848 | 19,998,512 | 2.021 | 1.008 |

实际记录数包含持久元数据、索引和固定 per-record 逻辑开销；因此 1 KiB 报文时系数较高。较大样本的 WAL 因一次大批量提交达到主库量级，说明只测 checkpoint 后文件会漏掉活跃 WAL。选择 `k=2.3`，在本次最大实测 2.021 之上留约 13.8% 相对余量；这不是统计置信区间。小样本数量少且批次集中，不能推出更高写入速率下 fsync、自动检查点、磁盘控制器缓存、文件系统压缩/CoW、碎片和数据库增长策略的行为。

## 适用边界和误差

仅在本产品目前 queue.db schema、WAL、synchronous=FULL、ext4/Linux、1–64 KiB 冻结报文范围内使用该经验系数作为规划提示。真实部署可能是其他文件系统、挂载选项、SQLite/操作系统版本、写入批次、记录元数据大小或报文尺寸；对 >64 KiB 报文、长期积压/频繁删除、队列维护或不同 filesystem 的误差没有测定，不应把 2.3 当作安全上界。实际队列配额、保留/失败区保护和磁盘预留仍是硬限制，容量估算仅供操作员比较。删除不会保证数据库立即缩小。外部进程并发用盘、空间统计延迟及突发输入均不在模型内。

本次临时 `/tmp` tmpfs 首次探测（结果见测试日志但不采用）证实路径选择会影响条件，故最终测试改为在程序集输出目录文件系统测量；这避免将 tmpfs 结果误记为 ext4 校准。需要在其他受支持部署文件系统上声明适用时，应在对应介质上重新运行相同测量，并记录环境，不可由本地 ext4 结果外推。
