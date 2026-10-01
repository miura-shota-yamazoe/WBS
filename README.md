# WBSアプリ

Windows PC内でプロジェクトと親子タスクを管理する、ASP.NET Core MVC製のWBSアプリです。

現在は **T03：保存先・初回DB作成・起動時Migration** まで完了しています。起動時にSQLiteの永続DBを初期化し、再起動後もデータを保持します。画面はアプリ名のみです。業務画面・exe起動終了・配布版は後続タスクで実装します。

## 開発環境

- .NET SDK 10.0.300（global.jsonで10.0.300系の修正版を許容）
- Visual Studio 2026のASP.NET/Web開発ワークロード、または.NET CLI
- Git
- 初回のパッケージ・ローカルツール取得時はNuGetへの接続が必要

| 依存 | 固定バージョン |
|---|---|
| Microsoft.EntityFrameworkCore.Sqlite / Design | 10.0.12 |
| dotnet-ef（ローカルツール） | 10.0.12 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 |
| Microsoft.NET.Test.Sdk | 18.0.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |

パッケージはcsprojで固定し、推移的依存もpackages.lock.jsonで管理します。EF用ツールはグローバルインストールせず、リポジトリ内のツールマニフェストで固定します。SDK・パッケージの更新時はビルド・テストを行い、ロックファイルも更新してください。

## 構成

```text
WbsApp.sln
src/WbsApp/          MVCアプリ
tests/WbsApp.Tests/ Webスモーク・実SQLiteのMigration/制約/起動テスト
.config/            ローカルEFツールマニフェスト
01～08の資料        計画・仕様・設計・タスク進捗
資料履歴/           統一前の資料（現行実装の参照対象外）
```

## 復元・ビルド・テスト

リポジトリのルートで実行します。

```powershell
dotnet restore WbsApp.sln --locked-mode
dotnet tool restore
dotnet build WbsApp.sln --configuration Release --no-restore
dotnet test WbsApp.sln --configuration Release --no-build --no-restore
dotnet ef --version
```

依存を意図的に変更するときだけ、通常の `dotnet restore WbsApp.sln` でロックファイルを更新します。テスト結果やビルド成果物はGit管理対象外です。

## 開発時の起動・終了

```powershell
$developmentDatabase = Join-Path $PWD 'artifacts\dev\wbsapp.db'
dotnet run --project src/WbsApp --launch-profile WbsApp -- --Database:Path "$developmentDatabase"
```

[開発時の画面](http://127.0.0.1:5180) をブラウザーで開きます。使用中のポートを変更する場合は、次のように起動してください。

```powershell
dotnet run --project src/WbsApp --no-launch-profile -- --urls http://127.0.0.1:5181 --Database:Path "$developmentDatabase"
```

現在の開発時の終了方法は実行中ターミナルでCtrl+Cです。exeのブラウザー自動起動、多重起動防止、画面からの終了操作はT16で実装します。上記コマンドでは開発用DBを作業フォルダー内に作成します。自動テストは専用の一時DBとメモリー内DBを使い、利用者の既定DBを変更しません。

## 資料と進捗管理

- [開発計画書](01_WBSアプリ開発計画書.md)
- [要件定義（仕様の正本）](02_要件定義_改訂版.md)
- [基本設計](03_WBS基本設計書_改訂版.md)
- [DB設計](04_DB設計書.md)
- [画面設計](05_画面設計書.md)
- [AI実装ガイド](06_AI実装ガイド.md)
- [決定事項](07_その他決定事項.txt)
- [開発タスク進捗（タスク管理の正本）](08_開発タスク進捗.md)

作業開始・完了・保留時には開発タスク進捗を更新します。ユーザーデータ、ログ、個人設定をコミットしないでください。

## データモデルとMigration

モデルは `src/WbsApp/Models`、EF設定は `src/WbsApp/Data/Configurations`、初期Migrationは `src/WbsApp/Data/Migrations` にあります。

T02のデザイン時FactoryはMigration生成用のメモリー内接続です。現時点で `dotnet ef database update` を実行しても永続DBの初期化にはなりません。永続DBはアプリ起動時に初期化します。テストは `EnsureCreated` ではなく実際のMigrationを使用します。

## 保存先と起動時のDB処理

- `Database:Path` を指定しない場合は `%LOCALAPPDATA%\WbsApp\Data\wbsapp.db` に保存します。保存フォルダーは起動時に作成します。
- 開発・テストで保存先を変更する場合は、コマンドラインの `--Database:Path` または環境変数 `Database__Path` でDBファイルの絶対パスを指定します。空文字・相対パス・メモリー内接続は指定できません。
- 新規DB（テーブルのない空ファイルを含む）には、Webサーバー開始前に初期Migrationを適用します。既存DBで更新が不要なら、そのまま起動してデータを保持します。
- 未対応のMigration履歴、破損DB、保存先不備、Migration失敗では通常起動を停止します。現在はコンソールへ原因を記録します。利用者向けのWindows起動案内・エラーID・ファイルログはT04/T16で追加します。
- **既存DBに未適用Migrationがある場合は、更新前バックアップ連携（T17c）が完成するまで更新せず停止します。** 初回Migrationの途中失敗で内部管理テーブルが残った場合も自動更新・自動削除しません。破損DBや既存利用データを削除して起動し直す操作は行わないでください。開発用の使い捨てDBだけ、新しい絶対パスで作り直せます。
- 接続は外部キー有効・プール無効で構成し、終了後のDB接続を保持しません。SQL待機時間は5秒ですが、単一起動制御・Migrationロック残留の扱い・更新復旧はT16/T17の範囲です。

## 配布・バックアップ

初回DB作成は実装済みです。自己完結型ZIP、既存DBの更新前自動バックアップと更新連携、終了後の手動コピー・復元は未完成です。開発途中の構成を利用者向け完成版として配布しないでください。実装と配布検証の完了後に、検証済みの利用・復旧手順を追記します。
