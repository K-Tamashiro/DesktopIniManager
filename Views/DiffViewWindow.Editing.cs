using DesktopIniManager.Properties;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private readonly HashSet<int> _sourceSelectedEditHunks = [];
    private readonly HashSet<int> _targetSelectedEditHunks = [];
    private bool _savingText, _confirmingTextChanges, _allowEditedClose;

    private HashSet<int> SelectedEditHunks(bool source)
    {
        return source ? _sourceSelectedEditHunks : _targetSelectedEditHunks;
    }

    private void InitializeDiffContextMenu()
    {
        var menu = new ContextMenu();

        MenuItem Item(string label, int icon)
        {
            return new MenuItem
            {
                Header = label,
                Icon = new Image { Source = DifferencerStatusIcons.GetCustomIcon(icon), Width = 16, Height = 16 }
            };
        }

        var clipboard = new MenuItem { Header = StringOverlay.Get("Diff_Clipboard") };
        var pasteSource = Item(StringOverlay.Get("Diff_PasteSource"), 4);
        var pasteTarget = Item(StringOverlay.Get("Diff_PasteTarget"), 5);
        pasteSource.Click += async (_, _) => await PasteClipboardAsync(true);
        pasteTarget.Click += async (_, _) => await PasteClipboardAsync(false);
        clipboard.Items.Add(pasteSource);
        clipboard.Items.Add(pasteTarget);
        menu.Items.Add(clipboard);
        menu.Items.Add(new Separator());
        var copyRight = Item(StringOverlay.Get("Diff_CopyRight"), 153);
        var copyLeft = Item(StringOverlay.Get("Diff_CopyLeft"), 151);
        menu.Items.Add(copyRight);
        menu.Items.Add(copyLeft);
        menu.Items.Add(new Separator());
        var hunkId = -1;

        async Task Copy(bool source)
        {
            if (hunkId < 0 || _renderingContent || _savingText || !ViewModel.CanChangeComparison) return;
            if (ViewModel.IsPatch && (!source || !ViewModel.CurrentPatch.CanCopyRun(hunkId))) return;
            var selected = SelectedEditHunks(source);
            ViewModel.CopyHunks(selected.Contains(hunkId) ? selected.ToArray() : new[] { hunkId }, source);
            await RenderTextEditsAsync();
            ClearCopiedHunkSelection(source);
        }

        copyRight.Click += async (_, _) => await Copy(true);
        copyLeft.Click += async (_, _) => await Copy(false);
        Body.ContextMenu = menu;
        Body.PreviewMouseRightButtonDown += (_, e) =>
        {
            hunkId = -1;
            var pane = e.GetPosition(Body).X < Body.ActualWidth / 2 ? _leftList : _rightList;
            if (ViewModel.IsText && pane != null && !_renderingContent)
            {
                var scroll = FindScroll(pane);
                var row = RowAt(e.GetPosition(pane).Y + (scroll?.VerticalOffset ?? 0));
                if (row >= 0 && row < Lines.Count) hunkId = Lines[row].EditHunkId;
            }

            copyRight.IsEnabled = copyLeft.IsEnabled = hunkId >= 0 && ViewModel.CanChangeComparison && !_savingText;
            if (ViewModel.IsPatch)
            {
                copyLeft.IsEnabled = false;
                copyRight.IsEnabled &= ViewModel.CurrentPatch.CanCopyRun(hunkId);
            }
            copyLeft.Visibility = ViewModel.IsPatch ? Visibility.Collapsed : Visibility.Visible;
            clipboard.Visibility = ViewModel.IsPatch ? Visibility.Collapsed : Visibility.Visible;
            // Open without allowing RichTextBox to replace this side's selection.
            e.Handled = true;
            menu.PlacementTarget = Body;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
    }

    private void AddTextSaveButton(TextBlock header, bool source)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = ViewModel.IsDirty(source),
            ToolTip = StringOverlay.Get(source ? "Diff_SaveSource" : "Diff_SaveTarget"),
            Content = new Image { Source = DifferencerStatusIcons.GetCustomIcon(59), Stretch = Stretch.Uniform }
        };
        button.Click += async (_, _) =>
        {
            _clipboardSource = source;
            await SaveTextSideAsync(source);
        };
        header.Inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center });
    }

    private bool HandleTextEditKey(KeyEventArgs e)
    {
        if (!ViewModel.IsText || Keyboard.Modifiers != ModifierKeys.Control) return false;
        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                _ = SaveTextSideAsync(_clipboardSource);
                return true;

            case Key.Z:
                e.Handled = true;
                if (!_savingText)
                {
                    ViewModel.UndoEdit();
                    _ = RenderTextEditsAsync();
                }

                return true;

            case Key.A:
                e.Handled = true;
                var selectedEditHunks = SelectedEditHunks(_clipboardSource);
                selectedEditHunks.Clear();
                foreach (var row in Lines.Where(r => r.EditHunkId >= 0)) selectedEditHunks.Add(row.EditHunkId);
                var pane = _clipboardSource ? _leftList : _rightList;
                pane?.SelectAll();
                RefreshEditArrows();
                return true;
        }

        return false;
    }

    private void UpdateTextHunkSelection(RichTextBox pane)
    {
        if (_renderingContent || _savingText) return;
        var selectedEditHunks = SelectedEditHunks(pane == _leftList);
        selectedEditHunks.Clear();
        if (!pane.Selection.IsEmpty)
        {
            var row = 0;
            foreach (var block in pane.Document.Blocks)
            {
                if (row >= Lines.Count) break;
                if (block.ContentEnd.CompareTo(pane.Selection.Start) >= 0 &&
                    block.ContentStart.CompareTo(pane.Selection.End) < 0 && Lines[row].EditHunkId >= 0)
                    selectedEditHunks.Add(Lines[row].EditHunkId);
                row++;
            }
        }

        RefreshEditArrows();
    }

    private void BuildEditArrows(Canvas viewport, bool source)
    {
        while (viewport.Children.Count > 1) viewport.Children.RemoveAt(1);
        if (!ViewModel.IsText || Lines == null) return;
        if (ViewModel.IsPatch && (!source || !ViewModel.CurrentPatch.CanApply)) return;
        var seen = new HashSet<int>();
        foreach (var row in Hunks)
        {
            var id = Lines[row].EditHunkId;
            // One arrow per run (each patch run has its own id).
            if (id < 0 || !seen.Add(id)) continue;
            var button = new Button
            {
                Width = 24,
                Height = DiffLineHeight,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Focusable = false,
                Tag = (row, id, source),
                Content = new Image { Stretch = Stretch.Uniform }
            };
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                if (_renderingContent || _savingText || !ViewModel.CanChangeComparison) return;
                _clipboardSource = source;
                var selectedEditHunks = SelectedEditHunks(source);
                var undo = ViewModel.CanUndoHunk(id, source);
                if (undo) ViewModel.UndoHunk(id, source);
                else
                    ViewModel.CopyHunks(selectedEditHunks.Contains(id) ? selectedEditHunks.ToArray() : new[] { id },
                        source);
                await RenderTextEditsAsync();
                if (!undo) ClearCopiedHunkSelection(source);
            };
            viewport.Children.Add(button);
            Canvas.SetLeft(button, 0);
        }

        UpdateEditArrowAppearance(viewport);
        PositionEditArrows(viewport, -Canvas.GetTop(viewport.Children[0]));
    }

    private void UpdateEditArrowAppearance(Canvas viewport)
    {
        var firstBySide = new Dictionary<bool, int>();
        foreach (var button in viewport.Children.OfType<Button>())
        {
            var (_, id, source) = ((int, int, bool))button.Tag;
            var selectedEditHunks = SelectedEditHunks(source);
            var undo = ViewModel.CanUndoHunk(id, source);
            if (!firstBySide.TryGetValue(source, out var first))
                firstBySide[source] = first = selectedEditHunks.Where(i => !ViewModel.CanUndoHunk(i, source))
                    .DefaultIfEmpty(-1).Min();
            var grouped = !undo && selectedEditHunks.Contains(id) && first != id;
            var icon = undo ? source ? 154 : 152 : grouped ? source ? 152 : 154 : source ? 153 : 151;
            ((Image)button.Content).Source = DifferencerStatusIcons.GetCustomIcon(icon);
            button.IsEnabled = !grouped;
            if (ViewModel.IsPatch)
                button.IsEnabled &= undo || ViewModel.CurrentPatch.CanCopyRun(id) &&
                    (!selectedEditHunks.Contains(id) || selectedEditHunks.All(ViewModel.CurrentPatch.CanCopyRun));
            button.ToolTip =
                StringOverlay.Get(undo ? "Diff_UndoHunk" : source ? "Diff_CopyToTarget" : "Diff_CopyToSource");
        }
    }

    private void RefreshEditArrows()
    {
        if (_leftList?.Tag is Canvas left) UpdateEditArrowAppearance(left);
        if (_rightList?.Tag is Canvas right) UpdateEditArrowAppearance(right);
    }

    private void ClearCopiedHunkSelection(bool source)
    {
        var pane = source ? _leftList : _rightList;
        SelectedEditHunks(source).Clear();
        if (pane != null)
        {
            var position = pane.Selection.Start;
            pane.Selection.Select(position, position);
            if (pane.IsKeyboardFocusWithin)
            {
                FocusManager.SetFocusedElement(this, null);
                Keyboard.ClearFocus();
            }
        }

        RefreshEditArrows();
        RefreshPatchMoveButtons();
    }

    private void PositionEditArrows(Canvas viewport, double offset)
    {
        foreach (var button in viewport.Children.OfType<Button>())
        {
            var (row, _, _) = ((int, int, bool))button.Tag;
            button.Height = DiffLineHeight;
            Canvas.SetTop(button, RowTop(row) - offset);
        }
    }

    private Task RenderTextEditsAsync()
    {
        if (_leftList == null || _rightList == null) return LoadContent(false, false);
        var scroll = _leftScroll ?? FindScroll(_leftList);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        var restoreSourceSelection = !_leftList.Selection.IsEmpty;
        var restoreTargetSelection = !_rightList.Selection.IsEmpty;
        var selectedHunk = Current;
        if (_renderedPanes.TryGetValue(true, out var previous) && _hunkStart >= 0 && _hunkStart < previous.Lines.Count)
        {
            var id = previous.Lines[_hunkStart].EditHunkId;
            selectedHunk = Hunks.FindIndex(row => Lines[row].EditHunkId == id);
        }

        _renderingContent = true;
        try
        {
            RefreshFoldedPanes(vertical, horizontal, selectedHunk);
            foreach (var side in new[] { true, false })
            {
                if (!(side ? restoreSourceSelection : restoreTargetSelection)) continue;
                var selected = SelectedEditHunks(side);
                var first = Lines.FindIndex(row => selected.Contains(row.EditHunkId));
                var last = Lines.FindLastIndex(row => selected.Contains(row.EditHunkId));
                if (first < 0) continue;
                var pane = side ? _leftList : _rightList;
                var blocks = pane.Document.Blocks.ToArray();
                pane.Selection.Select(blocks[first].ContentStart, blocks[last].ContentEnd);
            }

            _leftScroll?.ScrollToVerticalOffset(vertical);
            _rightScroll?.ScrollToVerticalOffset(vertical);
            _leftScroll?.ScrollToHorizontalOffset(horizontal);
            _rightScroll?.ScrollToHorizontalOffset(horizontal);
        }
        finally
        {
            _renderingContent = false;
        }

        RefreshEditArrows();
        foreach (var side in new[] { true, false })
        {
            var header = side ? SourceHeader : TargetHeader;
            foreach (var button in header.Inlines.OfType<InlineUIContainer>().Select(i => i.Child).OfType<Button>())
                button.IsEnabled = ViewModel.IsDirty(side);
        }

        RefreshPatchMoveButtons();
        return Task.CompletedTask;
    }

    private async Task ReloadTextAsync()
    {
        if (_renderingContent || _savingText || !ViewModel.CanChangeComparison) return;
        if (!await ConfirmTextChangesAsync()) return;
        await ViewModel.ReloadFromDiskAsync();
    }

    private async Task RecompareWorkingTextAsync()
    {
        if (_renderingContent || _savingText || !ViewModel.CanChangeComparison) return;
        if (!ViewModel.IsText)
        {
            await ViewModel.ReloadFromDiskAsync();
            return;
        }

        SetRendering(true);
        try
        {
            await ViewModel.RecompareTextAsync();
        }
        finally
        {
            SetRendering(false);
        }

        await LoadContent(false);
    }

    private async Task<bool> SaveTextSideAsync(bool source)
    {
        if (ViewModel.IsPatch) return source || await SavePatchAsync();
        if (_savingText || _renderingContent || !ViewModel.CanChangeComparison) return false;
        if (!ViewModel.IsDirty(source)) return true;
        _savingText = true;
        SetRendering(true);
        try
        {
            await ViewModel.SaveTextAsync(source);
        }
        catch (EncoderFallbackException ex)
        {
            await ShowEncodingErrorAsync(source, ex.Index);
            return false;
        }
        catch (DiffViewModel.UnrepresentableTextException ex)
        {
            await ShowEncodingErrorAsync(source, ex.Index);
            return false;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
            MessageBox.Show(this, ex.Message, StringOverlay.Get("Diff_SaveError"), MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _savingText = false;
            SetRendering(false);
        }

        await LoadContent(false);
        return true;
    }

    private async Task ShowEncodingErrorAsync(bool source, int index)
    {
        var (line, column) = ViewModel.EncodingErrorPosition(source, Math.Max(0, index));
        MessageBox.Show(this, string.Format(StringOverlay.Get("Diff_EncodingError"),
                source ? "Source" : "Target", (source ? ViewModel.SourceText : ViewModel.TargetText).EncodingName, line,
                column + 1),
            StringOverlay.Get("Diff_SaveError"), MessageBoxButton.OK, MessageBoxImage.Warning);
        if (ViewModel.DifferencesOnly) ViewModel.ToggleDifferencesOnly();
        SetRendering(false);
        await LoadContent(false, false);
        var row = Lines.FindIndex(r => (source ? r.LeftNumber : r.RightNumber) == line);
        if (row < 0) return;
        var pane = source ? _leftList : _rightList;
        pane.Focus();
        _clipboardSource = source;
        ScrollPaneToLine(pane, row);
        var paragraph = pane.Document.Blocks.ElementAt(row) as Paragraph;
        if (paragraph == null) return;
        // Rendered tabs occupy four columns, so map the original column to display text.
        var raw = (source ? ViewModel.SourceText : ViewModel.TargetText).Lines[line - 1];
        var displayColumn = raw[..Math.Min(column, raw.Length)].Replace("\t", "    ").Length;
        var start = TextPosition(paragraph, displayColumn);
        var end = TextPosition(paragraph,
            displayColumn + (column < raw.Length && char.IsHighSurrogate(raw[column]) ? 2 : 1));
        pane.Selection.Select(start, end);
    }

    private static TextPointer TextPosition(Paragraph paragraph, int offset)
    {
        var position = paragraph.ContentStart;
        while (position != null && position.CompareTo(paragraph.ContentEnd) < 0)
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var count = position.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= count) return position.GetPositionAtOffset(offset);
                offset -= count;
                position = position.GetPositionAtOffset(count);
            }
            else
            {
                position = position.GetNextContextPosition(LogicalDirection.Forward);
            }

        return paragraph.ContentEnd;
    }

    private async Task<bool> ConfirmTextChangesAsync()
    {
        if (_confirmingTextChanges || _savingText) return false;
        if (!ViewModel.HasEdits) return true;
        _confirmingTextChanges = true;
        try
        {
            var dialog = new Window
            {
                Owner = this,
                Title = StringOverlay.Get("Diff_UnsavedTitle"),
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            dialog.SetResourceReference(BackgroundProperty, "WindowBackground");
            dialog.SetResourceReference(ForegroundProperty, "Ink");
            panel.Children.Add(new TextBlock
            { Text = StringOverlay.Get("Diff_UnsavedPrompt"), Margin = new Thickness(0, 0, 0, 20) });
            var buttons = new StackPanel
            { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var choice = 0;
            foreach (var item in new[] { ("Diff_Save", 1), ("Diff_Discard", 2), ("Diff_Cancel", 0) })
            {
                var button = new Button
                {
                    Content = StringOverlay.Get(item.Item1),
                    MinWidth = 90,
                    Margin = new Thickness(5),
                    Padding = new Thickness(10, 5, 10, 5),
                    IsCancel = item.Item2 == 0
                };
                button.Click += (_, _) =>
                {
                    choice = item.Item2;
                    dialog.DialogResult = choice != 0;
                };
                buttons.Children.Add(button);
            }

            panel.Children.Add(buttons);
            dialog.Content = panel;
            if (dialog.ShowDialog() != true) return false;
            if (choice == 2)
            {
                if (ViewModel.IsPatch) ViewModel.DiscardPatchEdits();
                return true;
            }
            if (ViewModel.IsPatch) return await SaveAllPatchFilesAsync();
            return await SaveTextSideAsync(true) && await SaveTextSideAsync(false);
        }
        finally
        {
            _confirmingTextChanges = false;
        }
    }
}
