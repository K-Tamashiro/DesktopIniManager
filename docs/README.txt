DesktopIniManager v4.7.2

【概要】
DesktopIniManager（DIM）は、開発用フォルダーやVisual Studioソリューションの解析、ファイル検索、Scoped Grep、フォルダー比較、DIFF表示、desktop.iniによるアイコン管理を統合したWindows向け開発支援ツールです。

【v4.7.2の主な変更点】
・v4.7.0 PreReleaseのパッチ表示を正式リリース。
・パッチ表示のTarget側はファイル選択を廃止し、Targetボタンでリポジトリのフォルダーを選ぶ方式に変更。リポジトリのパス＋パッチ内のファイルパスが対象ファイルになり、パッチ側の選択に連動します。選んだパスは次回起動時も使います。
・新規追加ファイルは、対象が無ければパッチの内容をTarget側に表示し、パッチのパスで保存できます（フォルダーも作成）。
・Commit messageを選択・コピー可能に。
・スクロールマップ上部のボタンで「1 filtered lines」の行を1ドットの線に縮め、上下のHunkを接近させる表示に対応（状態を次回復元）。
・ツールバーの折り畳み、Commit messageの開閉状態を次回復元。
・ESCで両側の入力をクリア、F5で再読み込み。
・VB6フォーム（.frm）のデザイナー部分を、展開後も差分のみ表示で折り畳む2段階表示に変更。
・行内差分で、空白は空白そのものが変わった場合だけ表示。数字はまとまりとして比較し、同じ桁数なら同じ桁どうしの違いを表示。
・一部に不正なバイトを含むShift-JISファイルを開けるように変更（該当箇所は�で表示し、その側の保存は行いません）。

【v4.7.0 PreReleaseの主な変更点】
・DIFF VIEWにパッチ表示（Patch View）を追加。unified diff／git format-patch形式のパッチを読み込み、変更ファイルごとに確認・適用できます。
・パッチの適用はDIM内で行い、git applyなどの外部コマンドは実行しません。
・Developer Differencerの「Open patch / diff」ボタン、DIFF VIEWのPatch…ボタン、ドラッグ＆ドロップで開けます。
・コミットのタイトル、作成者、メッセージを「Commit message」欄に表示。
・Target側でリポジトリのフォルダーを選ぶと、パッチの相対パスから対象ファイルを決め、パッチ側の選択に連動します（v4.7.2でファイル選択から変更）。
・Hunkの位置はgit applyと同様に最も近い一致位置に合わせます。Targetに当て済みのHunkも判定します。
・差分のある行のまとまりごとに適用・Undoでき、Hunkの位置は上下ボタンで1行ずつ調整できます。
・保存時は既存ファイルの文字コード、BOM、改行コードを維持。UTF-8とShift-JISが混在するパッチにも対応。
・テキストDIFFで空白を無視する場合、行頭・行末の空白を無視し、行中の連続した空白・タブを1つの空白として比較するよう変更。
・改行を無視する場合、改行のみの行をHunkに含めず、単独行として扱うよう変更。
・行内差分で変化した空白を赤いマーカーで表示。
・VB6フォーム（.frm）のデザイナー部分を折り畳んで表示。
・Code Grep、.gitignore判定、フォルダー検索、Developer Differencerの処理を高速化。

【v4.6.0の主な変更点】
・テキストDIFF VIEWに差分ブロック（HUNK）単位のマージ機能を追加。
・Source→Target、Target→Sourceの双方向コピーに対応。
・コピー後の操作ボタンはUndoに切り替わり、保存前の変更を取り消し可能。
・変更したテキストファイルの保存に対応。
・空白、タブ、改行コード、EOFマークの視覚化を改善。
・テキストDIFFのフォントと文字サイズを設定可能。
・スクロール追従や表示処理を調整。
・コードのリファクタリングと整理を実施。

【その他の主な機能】
・5つの独立したプロセススロット
・ファイル検索、Code Grep、Excel／PDFを対象とするDocument Grep
・日時、サイズ、属性／BOM、XXH128によるフォルダー比較
・.gitignore対応の比較、同期、ZIP出力
・テキストDIFFの差分コピー・Undo・保存、パッチ表示
・画像、アニメーションGIF、動画、WAV／MP3音声のDIFF表示
・Git difftool連携
・WindowsのExplorerメニュー連携

【動作環境】
Windows 10／11（x64）
.NET 10 Desktop Runtime（x64）

【導入】
配布ZIPを新しいフォルダーに展開し、DesktopIniManager.exeを起動してください。
DLL、JSON、Assets、Languagesは同梱のまま配置してください。

【配布】
GitHub: https://github.com/K-Tamashiro/DesktopIniManager/releases
公式サイト: http://dim.zebrasoft.co.jp/
操作マニュアル: http://dim.zebrasoft.co.jp/manual.html

Freeware / Tamayan / ZEBRASOFT
