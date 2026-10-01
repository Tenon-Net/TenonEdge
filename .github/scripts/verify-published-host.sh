#!/usr/bin/env bash
# 在未预装 .NET 的干净容器中验证 linux-x64 自包含发布物（T0-13；V-01、V-07、V-08 的发布环境复验，BE-AT-06）。
#
# 用法：verify-published-host.sh <发布目录> [证据目录]
# 依赖：docker、curl、python3、unzip、openssl、GNU coreutils（base64、timeout、sha256sum）。
# 容器、数据卷与临时文件只为本次验证创建，结束时删除；容器的输出在删除前保存到证据目录。
# 证据目录另含发布物的 SHA-256 清单。发布物本身不上传、不分发。
#
# 检查项：
#   1. 干净镜像中没有 .NET。镜像默认按 digest 固定为 mcr.microsoft.com/dotnet/runtime-deps:10.0（只含原生依赖），可用 CLEAN_IMAGE 覆盖。
#   2. 发布物以容器进程启动：存活、就绪；日志记录实际加载的 SQLite 版本，不低于 3.51.3。
#   3. 经 API 完成首次设置；系统信息返回 NodeId、SQLite 版本与 HTTPS 证书指纹。
#   4. SIGTERM 停止后退出码 0；换一个安装路径、用同一数据卷重新启动：NodeId 与证书不变，重启前的会话 Cookie 仍有效（密钥环在数据卷中）。
#   5. 同一数据卷上的第二个实例以退出码 3 结束（数据目录单实例锁）。
#   6. 数据目录不在已挂载的卷上时（容器的可写层），管理界面照常可用，就绪检查返回 503，日志提示挂载数据卷（V-08）。
#   7. 换入 SQLite 3.49.1 的原生库（SQLitePCLRaw.lib.e_sqlite3 2.1.11）时，管理界面照常可用，就绪检查返回 503，日志说明原因。
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "用法：$0 <发布目录> [证据目录]" >&2
  exit 2
fi

publish_dir=$(realpath "$1")
evidence_dir=$(realpath -m "${2:-artifacts/publish-check}")
image="${CLEAN_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da}"
low_sqlite_package_version="2.1.11"
run_id="tenonedge-publish-$(date +%s)-$$"
volume="$run_id-data"
low_volume="$run_id-low-data"
work_dir=$(mktemp -d)
password="Verify-Publish-2026"
api="api/edge-admin/v1"

mkdir -p "$evidence_dir"
summary="$evidence_dir/summary.txt"
: > "$summary"

cleanup() {
  # 删除之前重新保存每个容器的全部输出（docker logs 总是返回容器启动以来的全部输出），
  # 这样启动即退出的容器、以及保存过一次之后才失败的步骤，证据目录中都有完整的输出。
  local container
  for container in $(docker ps -a --filter "name=$run_id" --format '{{.Names}}'); do
    docker logs "$container" > "$evidence_dir/${container#"$run_id"-}.log" 2>&1 || true
  done
  docker ps -aq --filter "name=$run_id" | xargs -r docker rm -f > /dev/null 2>&1 || true
  docker volume rm -f "$volume" "$low_volume" > /dev/null 2>&1 || true
  rm -rf "$work_dir"
}
trap cleanup EXIT

# 写入摘要，同时输出到标准错误：在命令替换中调用时（例如 host_port 失败）说明也会出现在控制台与 CI 的步骤日志中。
note() {
  echo "$*" >> "$summary"
  echo "$*" >&2
}

fail() {
  note "失败：$*"
  exit 1
}

# 启动一个容器运行发布物：$1 名称后缀，$2 发布目录，$3 容器内安装路径，$4 数据卷。输出容器名。
# $4 为空时不挂载卷：先在容器的可写层创建数据目录（宿主要求数据目录已存在），再以 exec 启动宿主，使它仍是 1 号进程。
start_host() {
  local name="$run_id-$1"
  local arguments=(-d --name "$name" -v "$2:$3:ro" -e TenonEdge__DataDirectory=/var/lib/tenonedge -p 127.0.0.1::8080)
  if [[ -n "$4" ]]; then
    docker run "${arguments[@]}" -v "$4:/var/lib/tenonedge" "$image" "$3/TenonEdge.Host" > /dev/null
  else
    docker run "${arguments[@]}" --entrypoint sh "$image" -c "mkdir -p /var/lib/tenonedge && exec $3/TenonEdge.Host" > /dev/null
  fi
  echo "$name"
}

host_port() {
  local mapping
  mapping=$(docker port "$1" 8080/tcp 2> /dev/null | head -n 1) || true
  [[ -n "$mapping" ]] || fail "容器 $1 没有映射端口（可能启动即退出），输出见证据目录"
  echo "${mapping##*:}"
}

# 等待 URL 返回指定状态码：$1 URL，$2 状态码，$3 秒数。
wait_for_status() {
  local deadline=$((SECONDS + $3)) status=""
  while (( SECONDS < deadline )); do
    status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$1" || true)
    if [[ "$status" == "$2" ]]; then
      return 0
    fi
    sleep 0.5
  done
  echo "等待 $1 返回 $2 超时，最后一次为 ${status:-无应答}" >&2
  return 1
}

json_field() {
  python3 -c 'import json,sys; value=json.load(sys.stdin)
for key in sys.argv[1].split("."): value=value[key]
print(value)' "$1"
}

save_logs() {
  docker logs "$1" > "$evidence_dir/$2.log" 2>&1 || true
}

note "发布目录：$publish_dir（$(du -sh "$publish_dir" | cut -f1)，$(find "$publish_dir" -type f | wc -l) 个文件）"
(cd "$publish_dir" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$evidence_dir/publish-sha256.txt"
note "发布物的 SHA-256 清单：publish-sha256.txt"
docker pull -q "$image" > /dev/null || fail "拉取干净镜像 $image 失败"
image_digest=$(docker image inspect "$image" --format '{{index .RepoDigests 0}}') || fail "读取镜像 $image 的 digest 失败"
[[ -n "$image_digest" ]] || fail "镜像 $image 没有 digest"
note "干净镜像：$image_digest"

# 1. 干净镜像中没有 .NET：没有 dotnet 命令，两个常见的安装目录都不存在。
docker run --rm --entrypoint sh "$image" -c \
  '! command -v dotnet > /dev/null 2>&1 && [ ! -e /usr/share/dotnet ] && [ ! -e /usr/lib/dotnet ]' \
  || fail "干净镜像中找到了 .NET"
note "1. 干净镜像中没有 dotnet 命令，也没有 /usr/share/dotnet、/usr/lib/dotnet。"

# 2. 启动发布物。
docker volume create "$volume" > /dev/null
first=$(start_host first "$publish_dir" /opt/tenonedge "$volume")
port=$(host_port "$first")
base="http://127.0.0.1:$port"
wait_for_status "$base/healthz" 200 60 || { save_logs "$first" first; fail "发布物没有启动"; }
wait_for_status "$base/readyz" 200 30 || { save_logs "$first" first; fail "发布物没有就绪"; }
save_logs "$first" first
sqlite_line=$(grep -m 1 "SQLite 原生库版本" "$evidence_dir/first.log" || true)
[[ "$sqlite_line" == *"SQLite 原生库版本 3.53.3"* ]] || fail "日志中的 SQLite 版本不符：${sqlite_line:-没有记录}"
note "2. 以 /opt/tenonedge/TenonEdge.Host 启动，存活与就绪均为 200；日志：$(echo "$sqlite_line" | sed 's/^ *//' | cut -c1-80)…"

# 3. 首次设置与系统信息。
cookies="$work_dir/cookies.txt"
setup_status=$(curl -s -o "$work_dir/setup.json" -w '%{http_code}' -c "$cookies" -H 'Content-Type: application/json' \
  -d "{\"accountName\":\"admin\",\"password\":\"$password\"}" "$base/$api/setup")
[[ "$setup_status" == 200 ]] || fail "首次设置返回 $setup_status"
curl -sf -b "$cookies" "$base/$api/system/info" > "$work_dir/info-first.json" || fail "读取系统信息失败"
node_id=$(json_field nodeId < "$work_dir/info-first.json")
fingerprint=$(json_field httpsCertificate.sha256Fingerprint < "$work_dir/info-first.json")
sqlite_version=$(json_field configStore.sqliteVersion < "$work_dir/info-first.json")
[[ "$sqlite_version" == "3.53.3" ]] || fail "系统信息中的 SQLite 版本为 $sqlite_version"
note "3. 首次设置 200；系统信息：NodeId $node_id，SQLite $sqlite_version，HTTPS 证书 SHA-256 $fingerprint"

# 4. 正常停止后换安装路径重启。
docker stop -t 30 "$first" > /dev/null
first_exit=$(docker inspect "$first" --format '{{.State.ExitCode}}')
save_logs "$first" first
[[ "$first_exit" == 0 ]] || fail "SIGTERM 停止后退出码为 $first_exit"
second=$(start_host second "$publish_dir" /srv/relocated "$volume")
base="http://127.0.0.1:$(host_port "$second")"
wait_for_status "$base/readyz" 200 60 || { save_logs "$second" second; fail "换安装路径后没有就绪"; }
session_status=$(curl -s -o /dev/null -w '%{http_code}' -b "$cookies" "$base/$api/auth/session")
[[ "$session_status" == 200 ]] || fail "换安装路径重启后，原会话返回 $session_status"
curl -sf -b "$cookies" "$base/$api/system/info" > "$work_dir/info-second.json" || fail "重启后读取系统信息失败"
[[ "$(json_field nodeId < "$work_dir/info-second.json")" == "$node_id" ]] || fail "重启后 NodeId 改变"
[[ "$(json_field httpsCertificate.sha256Fingerprint < "$work_dir/info-second.json")" == "$fingerprint" ]] || fail "重启后 HTTPS 证书改变"
note "4. SIGTERM 停止退出码 0；以 /srv/relocated 重启后原会话 Cookie 有效（200），NodeId 与 HTTPS 证书不变"

# 5. 同一数据卷上的第二个实例被拒绝。
third=$(start_host third "$publish_dir" /opt/tenonedge "$volume")
third_exit=$(timeout 60 docker wait "$third" || echo "未退出")
save_logs "$third" third
[[ "$third_exit" == 3 ]] || fail "第二个实例的退出码为 $third_exit"
note "5. 同一数据卷上的第二个实例退出码 3：$(grep -m 1 "宿主退出" "$evidence_dir/third.log" | sed 's/^ *//' || true)"
docker stop -t 30 "$second" > /dev/null
save_logs "$second" second

# 6. 数据目录不在已挂载的卷上。
unmounted=$(start_host unmounted "$publish_dir" /opt/tenonedge "")
base="http://127.0.0.1:$(host_port "$unmounted")"
wait_for_status "$base/healthz" 200 60 || { save_logs "$unmounted" unmounted; fail "数据目录不在卷上时没有启动"; }
wait_for_status "$base/readyz" 503 30 || { save_logs "$unmounted" unmounted; fail "数据目录不在卷上时就绪检查不是 503"; }
docker stop -t 30 "$unmounted" > /dev/null
save_logs "$unmounted" unmounted
grep -q "不在已挂载的持久卷上" "$evidence_dir/unmounted.log" || fail "日志中没有说明数据目录不在已挂载的卷上"
grep -q "请为数据目录挂载数据卷" "$evidence_dir/unmounted.log" || fail "日志中没有提示挂载数据卷"
note "6. 数据目录不在已挂载的卷上（容器可写层）：存活 200、就绪 503，日志提示为数据目录挂载数据卷"

# 7. 换入低版本 SQLite 原生库。
package="sqlitepclraw.lib.e_sqlite3"
curl -sf -o "$work_dir/low.nupkg" "https://api.nuget.org/v3-flatcontainer/$package/$low_sqlite_package_version/$package.$low_sqlite_package_version.nupkg" \
  || fail "下载 SQLitePCLRaw.lib.e_sqlite3 $low_sqlite_package_version 失败"
catalog=$(curl -sf "https://api.nuget.org/v3/registration5-gz-semver2/$package/$low_sqlite_package_version.json" --compressed | json_field catalogEntry)
expected_hash=$(curl -sf "$catalog" | json_field packageHash)
actual_hash=$(openssl dgst -sha512 -binary "$work_dir/low.nupkg" | base64 -w 0)
[[ "$expected_hash" == "$actual_hash" ]] || fail "SQLitePCLRaw.lib.e_sqlite3 $low_sqlite_package_version 的 SHA-512 与 nuget.org 登记不符"
cp -r "$publish_dir" "$work_dir/low-publish"
unzip -p "$work_dir/low.nupkg" runtimes/linux-x64/native/libe_sqlite3.so > "$work_dir/low-publish/libe_sqlite3.so"
docker volume create "$low_volume" > /dev/null
low=$(start_host low "$work_dir/low-publish" /opt/tenonedge "$low_volume")
base="http://127.0.0.1:$(host_port "$low")"
wait_for_status "$base/healthz" 200 60 || { save_logs "$low" low; fail "换入低版本 SQLite 后没有启动"; }
wait_for_status "$base/readyz" 503 30 || { save_logs "$low" low; fail "换入低版本 SQLite 后就绪检查不是 503"; }
docker stop -t 30 "$low" > /dev/null
save_logs "$low" low
grep -q "SQLite 原生库版本 3.49.1" "$evidence_dir/low.log" || fail "日志中没有记录 SQLite 3.49.1"
grep -q "低于要求的 3.51.3" "$evidence_dir/low.log" || fail "日志中没有说明低版本的原因"
note "7. 换入 SQLite 3.49.1（包哈希与 nuget.org 登记一致）：存活 200、就绪 503，日志说明低于 3.51.3、运行时不会启动"

note "全部通过。"
