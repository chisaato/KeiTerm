# KeiTerm Justfile
# 常用构建、测试与跨平台打包命令

set shell := ["bash", "-uc"]

project := "src/Kei.Term.App/Kei.Term.App.csproj"
dist_dir := "dist"

# 默认列出所有可用 recipe
default:
    @just --list

# 构建整个解决方案 (Debug)
build:
    dotnet build Kei.Term.slnx

# 执行单元测试
test:
    dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 执行包含真实 sshd 的集成测试 (需要本地 sshd 环境)
test-sshd:
    KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 本地运行应用
run:
    dotnet run --project {{project}}

# 清理构建产物与发布目录
clean:
    dotnet clean Kei.Term.slnx
    rm -rf {{dist_dir}}

# 打包单个目标并自动压缩成 zip (参数: rid, 示例: just pack-target linux-x64)
pack-target rid:
    @echo "==> [{{rid}}] Publishing..."
    rm -rf {{dist_dir}}/{{rid}} {{dist_dir}}/KeiTerm-{{rid}}.zip
    dotnet publish {{project}} \
        -c Release \
        -r {{rid}} \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o {{dist_dir}}/{{rid}}
    @echo "==> [{{rid}}] Creating zip archive..."
    (cd {{dist_dir}} && zip -q -r "KeiTerm-{{rid}}.zip" "{{rid}}")
    @echo "==> [{{rid}}] Done: {{dist_dir}}/KeiTerm-{{rid}}.zip"

# Linux 各架构子目标
pack-linux-x64: (pack-target "linux-x64")
pack-linux-arm64: (pack-target "linux-arm64")

# Windows 各架构子目标
pack-windows-x64: (pack-target "win-x64")
pack-windows-arm64: (pack-target "win-arm64")

# macOS 各架构子目标
pack-macos-arm64: (pack-target "osx-arm64")
pack-macos-x64: (pack-target "osx-x64")

# 并行打包 Linux (x64 与 arm64)
[parallel]
pack-linux: pack-linux-x64 pack-linux-arm64

# 并行打包 Windows (x64 与 arm64)
[parallel]
pack-windows: pack-windows-x64 pack-windows-arm64

# 并行打包 macOS (Apple Silicon arm64 与 Intel x64)
[parallel]
pack-macos: pack-macos-arm64 pack-macos-x64

# 并行打包三系统全部 6 个主流目标架构并生成 zip
[parallel]
pack-all: pack-linux-x64 pack-linux-arm64 pack-windows-x64 pack-windows-arm64 pack-macos-arm64 pack-macos-x64
