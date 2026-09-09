# SMVVM移行の引き継ぎ

更新: 2026-09-09

## 作業方針

- 1画面または1責務を単位に完結させる。複数画面を同時に全面変更しない。
- ビルド、テスト実行、デバッグは利用者が担当する。エージェントはリファクタリングとソースの静的確認に集中する。
- Behaviorは追加しない。Viewは初期化、ViewModelは状態・コマンド・処理の調整、Modelはデータ、Serviceは外部操作とWPF固有の画面操作を担当する。
- .NET 10移行の変更も現在の作業ツリーに含まれる。既存変更を一括で破棄しない。

## 今回の完結単位: MainWindowのコードビハインド最小化

- `MainWindow.xaml.cs`: 初期化と構成要素の接続のみ、21行。
- `MainWindow.xaml`: 操作はCommandで接続。WindowのTreeCompact依存プロパティをViewModelのプロパティへ変更。
- `ViewModels/MainWindowViewModel.cs`: 検索、結果・進捗・ツリー・ファイル一覧、アイコン適用・解除、設定リセットを保持。
- `Services/MainWindowPresentationService.cs`: MainWindowの画面連携、ダイアログ、ライフサイクルを担当。Windowを継承しない。
- 同Serviceの`.Tree.cs`、`.Appearance.cs`、`.Windows.cs`: スクロールと選択、表示設定、子ウィンドウ管理を整理。partialファイルは同じ画面ライフサイクルを共有する。
- `Models/LanguageChoice.cs`、`Models/MainWindowAction.cs`: 言語選択と画面操作のデータ。
- `Services/LanguageIconService.cs`: 言語アイコンの取得。

ViewModelからの画面操作要求をPresentationServiceが受ける。ServiceはWPFの選択・描画イベントを扱うが、attached behaviorは使用しない。

## 前回から含まれている変更

Grep、差分比較、差分ビューア、アイコン選択のViewModelを追加済み。検索、比較・同期・Clean、テキスト差分計算などの処理を移している。入力履歴の反映にはSetCurrentValueを使い、Bindingを維持する。既存テストの移動したメンバーへの参照も更新した。

## 次の単位

1. `GrepWindow.xaml.cs`を初期化だけにする。エディタープリセット・設定をServiceへ、グループ表示・スクロールをPresentationServiceへ分離する。
2. `DeveloperDifferencerWindow.xaml.cs`と`.Clean.cs`のダイアログ・プレビュー操作をServiceへ分離する。
3. `DiffViewWindow.cs`の描画・画像表示・外部エディター連携をServiceへ分離する。
4. `IconGroupBrowserWindow.xaml.cs`、起動画面、Appの構成を仕上げる。MainWindowの残るinternal状態へのService側アクセスも専用メソッドに集約する。

ソリューション全体の完全SMVVM化はまだ完了していない。次はGrepだけを対象にし、完結してから次の画面へ進む。

## 検証状況

C#ソース57ファイルの構文解析、変更したXAMLのXML解析、イベント・ViewModelメンバー・プロジェクト参照先の静的確認を実施した。コンパイル、アプリ起動、テスト、デバッグは未実施。型解決や実行時Bindingの成否は未確認。

利用者側では、まず.NET 10でビルドし、問題があればエラー一覧を次回作業に渡す。動作確認ではMainWindowの起動、検索・キャンセル、ツリー選択とスクロール、右クリックメニュー、テーマ・言語切り替え、設定保存を確認する。
