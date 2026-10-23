# WaveformControl

DIM Audio DIFF用のWPF波形コントロール。`Controls/WaveformData.cs` と
`Controls/WaveformControl.cs` は差分解析・プレイヤー・DIMの設定に依存せず、WPFとNAudioだけで使えます。

```csharp
await waveform.LoadAsync(path, cancellationToken);
waveform.PositionClicked += seconds => player.CurrentTime = TimeSpan.FromSeconds(seconds);
waveform.SetPosition(player.CurrentTime);
waveform.ZoomX(2);              // 時間方向を2倍に拡大
waveform.ZoomY(1.5);            // 振幅方向を1.5倍に拡大
waveform.SetViewRange(20, 10);  // 20～30秒
waveform.Fit();
```

- ホイール：マウス位置を基準に時間ズーム。Ctrl＋ホイール：振幅ズーム。
- 左ドラッグ：時間方向の移動。左クリック：シーク時刻の通知。
- DIMではShift＋ホイール／水平ホイールとスクロールバーも利用できます。
- 最大拡大は0.5秒幅。0.5秒未満の音源は全体表示になります。
- UI操作とSetDataはUIスレッドから呼びます。LoadAsyncはデコードをバックグラウンドで行います。
- 1秒2048区間のMin/Maxをチャンネル別に保存。時間分解能は読み込み時に指定できます。
- 1時間を超える音源はエラーとし、途中で切り捨てません。
- ズーム・移動・再生位置更新ではPCMを読み直しません。カーソルは独立したDrawingGroupです。
- Min/Maxのためサンプル単位の波形や区間内の振動順序は表現しません。

全体マップも同じ不変データを共有できます。

```csharp
overview.SetData(waveform.Data);
overview.InteractionEnabled = false;
waveform.ViewChanged += () =>
    overview.HighlightViewRange(waveform.ViewStart, waveform.ViewDuration);
```

DIMでは既存のマップのABリピート・音量メーターを維持し、その描画元を同じWaveformDataに変更しています。
差分区間の描画はDIM側のAudioWaveformアダプターが担当します。差分解析や再生処理はコントロールに含みません。

検証コマンド：

```powershell
dotnet build Tests/DesktopIniManager.DifferencerTests.csproj
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --waveform-control
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-progress
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-overview
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-differences
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-repeat
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-metadata
Tests/bin/Debug/net10.0-windows/win-x64/DesktopIniManager.DifferencerTests.exe --audio-diff
```

描画は元のMin/Maxを維持したまま、64区間単位の索引で画面内の各列に必要な正確なピークを取得します。
表示範囲・サイズ・振幅倍率が同じ間は描画形状を再利用し、差分色の描画でも共有します。
2048 Hz未満の音源は1サンプル1区間で保持します。音源終了後の比較枠には波形を描きません。
