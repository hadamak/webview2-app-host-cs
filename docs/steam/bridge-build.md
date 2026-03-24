# Steam ブリッジ ビルドガイド

この文書は `steam_bridge.dll` 自体をビルドまたは改修する人向けです。

この作業では Steamworks SDK が必要です。SDK はリポジトリには含めません。各担当者がローカル環境に別途用意してください。

---

## 1. 前提

- Windows
- Visual Studio / MSBuild による C++ ビルド環境
- Steamworks SDK をローカルに展開済み

必要な主なファイル:

- `public/steam/steam_api.h`
- `redistributable_bin/win64/steam_api64.lib`
- `redistributable_bin/win64/steam_api64.dll`

---

## 2. SDK の指定方法

ビルドでは `STEAMWORKS_SDK_ROOT`、または MSBuild プロパティ `SteamworksSdkDir` を使います。

例:

```powershell
$env:STEAMWORKS_SDK_ROOT = "C:\Steamworks\sdk"
msbuild src\steam-bridge\SteamBridge.vcxproj /p:Configuration=Release /p:Platform=x64
```

または:

```powershell
msbuild src\steam-bridge\SteamBridge.vcxproj /p:Configuration=Release /p:Platform=x64 /p:SteamworksSdkDir="C:\Steamworks\sdk"
```

---

## 3. ローカルで Steam サポート ZIP を作る

Steam サポート ZIP は公開 CI ではなくローカルで作成します。

補助スクリプト:

```powershell
tools\package-steam-support.ps1
```

例:

```powershell
$env:STEAMWORKS_SDK_ROOT = "C:\Steamworks\sdk"
powershell -ExecutionPolicy Bypass -File tools\package-steam-support.ps1
```

この ZIP はアプリ開発者向けであり、エンドユーザー向け通常配布物に含める API 文書とは別物です。

---

## 4. 出力物

ブリッジのビルド成果物:

- `src/bin/x64/Release/net472/steam_bridge.dll`

ローカル作成する Steam サポート ZIP には通常、次を含めます。

- `steam_bridge.dll`
- `steam_api64.dll`
- `steam.js`
- `steam-sample/`
- アプリ開発者向け Steam 利用ガイド
