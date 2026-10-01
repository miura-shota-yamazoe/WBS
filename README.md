# WBSアプリ

Windows PC内でプロジェクトと親子タスクを管理する、ASP.NET Core MVC製のWBSアプリです。

現在は **T01：開発構成・依存バージョン・Git準備** が完了した段階です。起動時にはアプリ名のみを表示します。プロジェクト・タスク管理、DB作成・Migration自動適用、exe起動・終了、配布版は後続タスクで実装します。

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
tests/WbsApp.Tests/ Web応答・SQLite接続のスモークテスト
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
dotnet run --project src/WbsApp --launch-profile WbsApp
```

[開発時の画面](http://127.0.0.1:5180) をブラウザーで開きます。使用中のポートを変更する場合は、次のように起動してください。

```powershell
dotnet run --project src/WbsApp --no-launch-profile -- --urls http://127.0.0.1:5181
```

T01時点の終了方法は実行中ターミナルでCtrl+Cです。exeのブラウザー自動起動、多重起動防止、画面からの終了操作はT16で実装します。現段階では永続DBを作成せず、SQLiteスモークテストだけがメモリー内DBを使用します。

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

## 配布・バックアップ

自己完結型ZIP、初回DB作成・更新、更新前自動バックアップ、終了後の手動コピー・復元は未完成です。開発途中の構成を利用者向け完成版として配布しないでください。実装と配布検証の完了後に、検証済みの利用・復旧手順を追記します。
