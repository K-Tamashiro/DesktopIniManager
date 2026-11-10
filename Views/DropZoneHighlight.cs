using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

/// <summary>
///     ファイルやフォルダーのドラッグ中に、ドロップされる場所へ点線枠を重ねて表示します。
///     枠は表示だけで、ドロップの受け付けや判定は変えません。
/// </summary>
internal sealed class DropZoneHighlight
{
    private readonly Func<bool> _canShow;
    private readonly Dispatcher _dispatcher;
    private readonly Func<DragEventArgs, (UIElement Element, Rect[] Areas)> _resolve;
    private AdornerLayer _layer;
    private FrameAdorner _shown;
    private bool _leavePending;

    /// <summary>
    ///     点線枠の表示を管理するオブジェクトを作成します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="resolve">ドラッグ位置から、枠を重ねる要素とその中の枠の範囲を返す処理。範囲が null の場合は要素の全体。</param>
    private DropZoneHighlight(Window window, Func<bool> canShow,
        Func<DragEventArgs, (UIElement Element, Rect[] Areas)> resolve)
    {
        _canShow = canShow;
        _dispatcher = window.Dispatcher;
        _resolve = resolve;
    }

    /// <summary>
    ///     ドロップ先が 1 つの画面で、指定した要素の全体に枠を表示するよう、ウィンドウのドラッグ イベントに接続します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="element">枠を重ねる要素。</param>
    internal static void Attach(Window window, Func<bool> canShow, UIElement element)
    {
        Connect(window, new DropZoneHighlight(window, canShow, _ => (element, null)));
    }

    /// <summary>
    ///     ドラッグ位置に応じて選んだ要素の全体に枠を表示するよう、ウィンドウのドラッグ イベントに接続します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="target">ドラッグ位置からドロップ先の要素を返す処理。</param>
    internal static void Attach(Window window, Func<bool> canShow, Func<DragEventArgs, UIElement> target)
    {
        Connect(window, new DropZoneHighlight(window, canShow, e => (target(e), null)));
    }

    /// <summary>
    ///     1 つの要素の中で、ドラッグ位置に応じた範囲に枠を表示するよう、ウィンドウのドラッグ イベントに接続します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="element">枠を重ねる要素。</param>
    /// <param name="areas">ドラッグ位置から、要素を基準にした枠の範囲を返す処理。</param>
    internal static void Attach(Window window, Func<bool> canShow, UIElement element,
        Func<DragEventArgs, IEnumerable<Rect>> areas)
    {
        Connect(window, new DropZoneHighlight(window, canShow, e => (element, areas(e).ToArray())));
    }

    /// <summary>
    ///     ウィンドウのドラッグ イベントを接続します。既存の処理が Handled にした場合でも枠を更新できるよう、処理済みのイベントも受け取ります。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="highlight">接続するオブジェクト。</param>
    private static void Connect(Window window, DropZoneHighlight highlight)
    {
        window.AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler(highlight.OnDragOver), true);
        window.AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(highlight.OnDragOver), true);
        window.AddHandler(DragDrop.PreviewDragLeaveEvent, new DragEventHandler(highlight.OnDragLeave), true);
        window.AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler(highlight.OnDrop), true);
    }

    /// <summary>
    ///     ドラッグがウィンドウの上にある間、受け付けられるならドロップ先に枠を表示し、受け付けられないなら消します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        _leavePending = false;
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) && _canShow())
        {
            var (element, areas) = _resolve(e);
            Show(element, areas);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    ///     ドラッグが離れたら枠を消します。子要素の間を移動しただけでも発生し、その場合は続けて Enter が届くため、後回しにして判定します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _leavePending = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_leavePending) return;
            _leavePending = false;
            Hide();
        }));
    }

    /// <summary>
    ///     ドロップされたら枠を消します。確認ダイアログの間も枠が残らないよう、既存のドロップ処理より先に接続します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void OnDrop(object sender, DragEventArgs e)
    {
        _leavePending = false;
        Hide();
    }

    /// <summary>
    ///     指定した要素に枠を表示します。同じ要素と範囲ですでに表示中なら何もしません。
    /// </summary>
    /// <param name="element">枠を重ねる要素。null の場合は枠を消します。</param>
    /// <param name="areas">要素を基準にした枠の範囲。null の場合は要素の全体。</param>
    private void Show(UIElement element, Rect[] areas)
    {
        if (element == null || !element.IsVisible || areas is { Length: 0 })
        {
            Hide();
            return;
        }

        if (_shown != null && _shown.AdornedElement == element && _shown.HasAreas(areas)) return;
        Hide();
        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer == null) return;
        _shown = new FrameAdorner(element, areas);
        _layer = layer;
        layer.Add(_shown);
    }

    /// <summary>
    ///     表示中の枠を取り除きます。
    /// </summary>
    private void Hide()
    {
        if (_shown == null) return;
        _layer.Remove(_shown);
        _shown = null;
        _layer = null;
    }

    /// <summary>
    ///     要素の上に点線枠を描くアドーナーです。マウスやドラッグの判定には関わりません。
    /// </summary>
    private sealed class FrameAdorner : Adorner
    {
        private const double Thickness = 2;
        private const double Radius = 8;
        private const double Inset = 2;
        private static readonly DashStyle Dash = CreateDash();
        private static readonly Brush FallbackBrush = CreateFallbackBrush();
        private readonly Rect[] _areas;

        /// <summary>
        ///     アドーナーを作成します。
        /// </summary>
        /// <param name="element">枠を重ねる要素。</param>
        /// <param name="areas">要素を基準にした枠の範囲。null の場合は要素の全体。</param>
        internal FrameAdorner(UIElement element, Rect[] areas) : base(element)
        {
            _areas = areas;
            IsHitTestVisible = false;
        }

        /// <summary>
        ///     表示中の範囲が指定した範囲と同じかどうかを判定します。
        /// </summary>
        /// <param name="areas">比べる範囲。null の場合は要素の全体。</param>
        /// <returns>同じ場合は true。</returns>
        internal bool HasAreas(Rect[] areas)
        {
            if (_areas == null || areas == null) return _areas == null && areas == null;
            return _areas.SequenceEqual(areas);
        }

        /// <summary>
        ///     枠の破線の形式を作ります。長さは線の太さの倍数で、5 描いて 3 空けます (ResourceHackerReplacer と同じ)。
        /// </summary>
        /// <returns>凍結した破線の形式。</returns>
        private static DashStyle CreateDash()
        {
            var dash = new DashStyle([5.0, 3.0], 0);
            dash.Freeze();
            return dash;
        }

        /// <summary>
        ///     テーマに枠の色がない場合に使う色を作ります。
        /// </summary>
        /// <returns>凍結したブラシ。</returns>
        private static Brush CreateFallbackBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x5B, 0x86, 0xE5));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        ///     テーマの枠の色で、範囲の内側に角丸の点線を描きます。範囲が小さい場合は角丸を小さくします。
        /// </summary>
        /// <param name="drawingContext">描画先。</param>
        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = TryFindResource("DropZoneFrame") as Brush ?? FallbackBrush;
            var pen = new Pen(brush, Thickness) { DashStyle = Dash };
            pen.Freeze();
            foreach (var area in _areas ?? [new Rect(AdornedElement.RenderSize)])
            {
                if (area.Width <= Inset * 2 || area.Height <= Inset * 2) continue;
                var frame = new Rect(area.X + Inset, area.Y + Inset, area.Width - Inset * 2,
                    area.Height - Inset * 2);
                var radius = Math.Min(Radius, Math.Min(frame.Width, frame.Height) / 4);
                drawingContext.DrawRoundedRectangle(null, pen, frame, radius, radius);
            }
        }
    }
}
