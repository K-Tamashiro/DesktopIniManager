using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

/// <summary>
///     ファイルやフォルダーのドラッグ中に、ドロップできる場所へ点線枠を重ねて表示します。
///     枠は表示だけで、ドロップの受け付けや判定は変えません。
/// </summary>
internal sealed class DropZoneHighlight
{
    private readonly Func<bool> _canShow;
    private readonly Dispatcher _dispatcher;
    private readonly List<(AdornerLayer Layer, Adorner Adorner)> _shown = [];
    private readonly (UIElement Element, Func<IEnumerable<Rect>> Areas)[] _zones;
    private bool _leavePending;

    /// <summary>
    ///     点線枠の表示を管理するオブジェクトを作成します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="zones">枠を重ねる要素と、その中の枠の範囲。</param>
    private DropZoneHighlight(Window window, Func<bool> canShow,
        (UIElement Element, Func<IEnumerable<Rect>> Areas)[] zones)
    {
        _canShow = canShow;
        _dispatcher = window.Dispatcher;
        _zones = zones;
    }

    /// <summary>
    ///     指定した要素の全体に枠を表示するよう、ウィンドウのドラッグ イベントに接続します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="elements">枠を重ねる要素。</param>
    internal static void Attach(Window window, Func<bool> canShow, params UIElement[] elements)
    {
        Connect(window, new DropZoneHighlight(window, canShow,
            elements.Select(element => (element, (Func<IEnumerable<Rect>>)(() => [new Rect(element.RenderSize)])))
                .ToArray()));
    }

    /// <summary>
    ///     1 つの要素の中の複数の範囲に枠を表示するよう、ウィンドウのドラッグ イベントに接続します。
    /// </summary>
    /// <param name="window">ドラッグを監視するウィンドウ。</param>
    /// <param name="canShow">現在ドロップを受け付けられるかどうかを返す処理。</param>
    /// <param name="element">枠を重ねる要素。</param>
    /// <param name="areas">要素を基準にした枠の範囲を返す処理。</param>
    internal static void Attach(Window window, Func<bool> canShow, UIElement element, Func<IEnumerable<Rect>> areas)
    {
        Connect(window, new DropZoneHighlight(window, canShow, [(element, areas)]));
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
    ///     ドラッグがウィンドウの上にある間、受け付けられるなら枠を表示し、受け付けられないなら消します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        _leavePending = false;
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) && _canShow())
            Show();
        else
            Hide();
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
    ///     表示されている要素に枠を追加します。すでに表示中なら何もしません。
    /// </summary>
    private void Show()
    {
        if (_shown.Count > 0) return;
        foreach (var (element, areas) in _zones)
        {
            if (!element.IsVisible) continue;
            var layer = AdornerLayer.GetAdornerLayer(element);
            if (layer == null) continue;
            var adorner = new FrameAdorner(element, areas);
            layer.Add(adorner);
            _shown.Add((layer, adorner));
        }
    }

    /// <summary>
    ///     表示中の枠をすべて取り除きます。
    /// </summary>
    private void Hide()
    {
        foreach (var (layer, adorner) in _shown)
            layer.Remove(adorner);
        _shown.Clear();
    }

    /// <summary>
    ///     要素の上に点線枠を描くアドーナーです。マウスやドラッグの判定には関わりません。
    /// </summary>
    private sealed class FrameAdorner : Adorner
    {
        private const double Thickness = 1.5;
        private const double Radius = 4;
        private static readonly Pen UnderPen = CreatePen(Color.FromRgb(0x40, 0x40, 0x40), null);
        private static readonly Pen DashPen = CreatePen(Colors.White, new DashStyle([3.0, 2.0], 0));
        private readonly Func<IEnumerable<Rect>> _areas;

        /// <summary>
        ///     アドーナーを作成します。
        /// </summary>
        /// <param name="element">枠を重ねる要素。</param>
        /// <param name="areas">枠の範囲を返す処理。</param>
        internal FrameAdorner(UIElement element, Func<IEnumerable<Rect>> areas) : base(element)
        {
            _areas = areas;
            IsHitTestVisible = false;
        }

        /// <summary>
        ///     枠の線を作ります。
        /// </summary>
        /// <param name="color">線の色。</param>
        /// <param name="dashStyle">破線の形式。実線の場合は null。</param>
        /// <returns>凍結した線。</returns>
        private static Pen CreatePen(Color color, DashStyle dashStyle)
        {
            var pen = new Pen(new SolidColorBrush(color), Thickness);
            if (dashStyle != null) pen.DashStyle = dashStyle;
            pen.Freeze();
            return pen;
        }

        /// <summary>
        ///     Light テーマでも見えるよう、濃い色の実線の上に白い点線を重ねて描きます。
        /// </summary>
        /// <param name="drawingContext">描画先。</param>
        protected override void OnRender(DrawingContext drawingContext)
        {
            foreach (var area in _areas())
            {
                if (area.Width <= 2 || area.Height <= 2) continue;
                var frame = new Rect(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2);
                drawingContext.DrawRoundedRectangle(null, UnderPen, frame, Radius, Radius);
                drawingContext.DrawRoundedRectangle(null, DashPen, frame, Radius, Radius);
            }
        }
    }
}
