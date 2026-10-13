# KeiTerm Justfile
# 常用构建、测试与跨平台打包命令

set shell := ["bash", "-uc"]

project := "src/Kei.Term.App/Kei.Term.App.csproj"
dist_dir := "dist"
app_name := "KeiTerm"

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

# macOS 各架构子目标：产出真正的 .app bundle（图标与 Dock 名称依赖 bundle 才会生效）
pack-macos-arm64: (bundle-macos "osx-arm64")
pack-macos-x64: (bundle-macos "osx-x64")

# 组装 macOS .app bundle：自包含发布 + Info.plist + .icns + ad-hoc 签名，并压缩为 zip。
# 不能沿用 pack-target：macOS 只有装进 .app 才能拿到正确的 Dock 名称、图标与 TCC 权限归属。
bundle-macos rid:
    #!/usr/bin/env bash
    set -euo pipefail
    APP="{{dist_dir}}/{{app_name}}.app"
    ZIP="{{dist_dir}}/{{app_name}}-{{rid}}.zip"
    rm -rf "$APP" "$ZIP"

    echo "==> [{{rid}}] Publishing self-contained..."
    dotnet publish {{project}} \
        -c Release \
        -r {{rid}} \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o "$APP/Contents/MacOS"

    mkdir -p "$APP/Contents/Resources"
    cp packaging/macos/Info.plist "$APP/Contents/Info.plist"

    # 许可证属于资源；放在 MacOS 可执行目录会导致 codesign 将其作为未签名代码检查。
    if [ -d "$APP/Contents/MacOS/Licenses" ]; then
        mv "$APP/Contents/MacOS/Licenses" "$APP/Contents/Resources/Licenses"
    fi

    # codesign 要求 Contents/MacOS 下全部是可签名的 Mach-O 代码，
    # 托管程序集的 .pdb 会让签名失败（"code object is not signed at all"），故移除。
    find "$APP/Contents/MacOS" -name '*.pdb' -delete

    echo "==> [{{rid}}] Building .icns from AppIcon.png..."
    ICONSET_ROOT="$(mktemp -d)"
    ICONSET="$ICONSET_ROOT/AppIcon.iconset"
    mkdir -p "$ICONSET"
    # iconutil 要求固定的文件名与尺寸组合
    for sz in 16 32 128 256 512; do
        sips -z "$sz" "$sz" packaging/macos/AppIcon.png --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
        dbl=$((sz * 2))
        sips -z "$dbl" "$dbl" packaging/macos/AppIcon.png --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/{{app_name}}.icns"
    rm -rf "$ICONSET_ROOT"

    # 版本号与 csproj 对齐，避免 Info.plist 手改后失真
    VERSION="$(dotnet msbuild {{project}} -getProperty:Version -nologo 2>/dev/null | tr -d '[:space:]')"
    if [ -n "$VERSION" ]; then
        plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
        plutil -replace CFBundleVersion -string "$VERSION" "$APP/Contents/Info.plist"
    fi

    echo "==> [{{rid}}] Ad-hoc signing..."
    # 必须由内向外逐层签名：bundle 内嵌的第三方原生可执行文件（royalterminal-pty-spawn）
    # 与 dylib 若未签名，直接签 bundle 会报 "code object is not signed at all"。
    find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -perm -u+x \) \
        -exec codesign --force --sign - {} \; 2>/dev/null || true
    if codesign --force --sign - "$APP" 2>/dev/null; then
        echo "==> [{{rid}}] Signed (ad-hoc)"
    else
        echo "    (ad-hoc 签名不可用，已跳过)"
    fi

    echo "==> [{{rid}}] Creating zip archive..."
    (cd "{{dist_dir}}" && zip -q -r "{{app_name}}-{{rid}}.zip" "{{app_name}}.app")
    echo "==> [{{rid}}] Done: $ZIP"

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
