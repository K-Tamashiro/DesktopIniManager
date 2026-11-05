DesktopIniManager v4.7.0 PreRelease

【概要】
DesktopIniManager（DIM）は、開発用フォルダーやVisual Studioソリューションの解析、ファイル検索、Scoped Grep、フォルダー比較、DIFF表示、desktop.iniによるアイコン管理を統合したWindows向け開発支援ツールです。

【v4.7.0 PreReleaseの主な変更点】
・DIFF VIEWにパッチ表示（Patch View）を追加。unified diff／git format-patch形式のパッチを読み込み、変更ファイルごとに確認・適用できます。
・パッチの適用はDIM内で行い、git applyなどの外部コマンドは実行しません。
・Developer Differencerの「Open patch / diff」ボタン、DIFF VIEWのPatch…ボタン、ドラッグ＆ドロップで開けます。
・コミットのタイトル、作成者、メッセージを「Commit message」欄に表示。
・Target…でリポジトリ内のファイルを1つ選ぶと、パッチの相対パスからリポジトリのルートを判定し、パッチ側とTarget側の選択が連動します。
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
