# RustManager Plugin

Rust Manager の拡張機能を有効にする Rust Dedicated Server 向け Carbon/Oxide プラグインです。

## Download

- [RustManager.cs](https://raw.githubusercontent.com/miyagawayuu/rustmanager-plugin/main/RustManager.cs)

## Install

`RustManager.cs` をサーバーのプラグインディレクトリに配置してください。

```text
carbon/plugins/RustManager.cs
```

または

```text
oxide/plugins/RustManager.cs
```

配置後、Carbon/Oxide が自動ロードしない場合はサーバーコンソールまたはRCONで reload してください。

```text
c.reload RustManager
```

または

```text
oxide.reload RustManager
```

## Features

- プレイヤー全体取得（オンライン / スリープ中）
- チーム情報取得
- TC認証プレイヤー取得
- インベントリ / ベルト / 装備 / バックパック操作
- RustManager独自ホワイトリスト
- トラップ検知
- ライブマップ画像アップロード連携

## Configuration

ライブマップ画像アップロードを使う場合は、生成された `RustManager.json` の `mapUpload` を設定してください。

```json
{
  "mapUpload": {
    "enabled": true,
    "uploadUrl": "https://rustmanager.io/api/rust-map/upload",
    "serverKey": "YOUR_SERVER_KEY"
  }
}
```

`serverKey` は Rust Manager の管理画面側で発行されるサーバーキーを使用します。

## Notes

このリポジトリは `RustManager.cs` 配布専用です。Rust Manager 本体のソースコードは含みません。
